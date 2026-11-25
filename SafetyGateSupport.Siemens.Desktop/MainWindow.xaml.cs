using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using SiemensUsbCheck.Services;
using SafetyGateSupport.Siemens;

namespace SiemensUsbCheck;

public partial class MainWindow : Window
{
    public bool IsEmbeddedBusy => _operationCancellation is not null;
    private CancellationTokenSource? _operationCancellation;
    private bool _isRefreshing;
    private bool _closeWhenFinished;
    private PreparedSignalUpload? _selectedUpload;

    private readonly bool _previewOnly;
    private readonly string _context;
    private string _comparison = "Stored configuration has not been checked against the selected file.";
    private sealed record AssignedFile(string Path, string Label);
    private string? _selectedPath, _pairedPdf;
    private SignalLabels _labels = SignalLabels.Default;
    private CancellationTokenSource? _pdfLoad;

    /// <summary>Renders the first page of the PDF saved with a configuration; supplied by the host app.</summary>
    public Func<string, CancellationToken, Task<ImageSource?>>? RenderPdf { get; set; }
    /// <summary>Opens a file in its default viewer; supplied by the host app.</summary>
    public Action<string>? OpenFile { get; set; }
    /// <summary>The configuration currently shown, so the host can offer to upload its next revision.</summary>
    public string? SelectedConfigurationPath => _selectedPath;

    /// <summary>A configuration's PDF sheet is stored beside it with the same name.</summary>
    public static string? CompanionPdf(string signalPath)
    {
        string candidate = System.IO.Path.ChangeExtension(signalPath, ".pdf");
        return System.IO.File.Exists(candidate) ? candidate : null;
    }

    public MainWindow() : this("", Array.Empty<string>()) { }
    public MainWindow(string context, IEnumerable<string> assignedFiles, bool previewOnly = false)
    {
        InitializeComponent();
        _context = context;
        _previewOnly = previewOnly;
        if (context.Length > 0)
        {
            Title = "Safety Gate Support — Signal column";
            ContextText.Text = context;
            ContextText.Visibility = Visibility.Visible;
            AssignedFilesCombo.ItemsSource = assignedFiles.Select(p => new AssignedFile(p, System.IO.Path.GetFileName(p) + (CompanionPdf(p) is null ? "" : "  ·  with PDF"))).ToList();
            AssignedFilesCombo.Visibility = Visibility.Visible;
            SelectedSignalText.Text = AssignedFilesCombo.Items.Count switch
            {
                0 => "No configuration is saved for this unit yet. Upload a configuration pair, or choose a file under More options.",
                1 => "",
                _ => "Several configurations are saved for this unit. Choose the right one."
            };
        }
        UpdatePreview();
    }

    private async void AssignedFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AssignedFilesCombo.SelectedItem is AssignedFile file) await SelectFileAsync(file.Path);
    }

    private async Task SelectFileAsync(string path)
    {
        if (_isRefreshing || _operationCancellation is not null) return;
        _selectedUpload = null; _selectedPath = null; _labels = SignalLabels.Default; ApplyInputLabels();
        ResetObservation();
        MatchText.Text = "";
        _comparison = "Stored configuration has not been checked against the selected file.";
        try
        {
            _isRefreshing = true; UpdateControls();
            _selectedUpload = await Task.Run(() => PreparedSignalUpload.Load(path));
            _selectedPath = path;
            _labels = SignalLabels.Read(_selectedUpload); ApplyInputLabels();
            int states = _selectedUpload.Archive.ActiveStates.Count;
            SelectedSignalText.Text = $"{states} input {(states == 1 ? "state" : "states")} configured · {System.IO.Path.GetFileName(path)}";
            SelectedSignalText.ToolTip = path;
            ResultText.Text = _comparison;
            StatusText.Text = _previewOnly || _selectedUpload.IsPreviewOnly ? "Preview only: this sample cannot be uploaded." : "Configuration ready.";
        }
        catch (Exception error)
        {
            SelectedSignalText.Text = "No valid configuration selected.";
            ResultText.Text = error.Message;
        }
        finally { _isRefreshing = false; UpdateControls(); UpdatePreview(); BuildStateTable(); _ = LoadPairedPdfAsync(_selectedPath); }
    }

    // One card per configured input combination, like the Siemens configurator's state list.
    private void BuildStateTable()
    {
        StateTablePanel.Children.Clear();
        if (_selectedUpload is not { } selected) { StateTablePanel.Children.Add(new TextBlock { Text = "Choose a configuration to see its states.", TextWrapping = TextWrapping.Wrap }); return; }
        foreach (int state in selected.Archive.ActiveStates.Distinct().Order())
        {
            SignalStatePreview.Appearance look;
            try { look = SignalStatePreview.ReadAppearance(selected, state); } catch (Exception) { continue; }
            var card = new StackPanel { Width = 118 };
            card.Children.Add(SignalColumnGraphic.Build(look, 9, 26, _labels.UpsideDown, labels: false));
            var heading = StateHeading(state); heading.Margin = new Thickness(0, 8, 0, 0); card.Children.Add(heading);
            string effects = string.Join(", ", look.Segments.Where(s => !s.IsOff && s.Effect != 10).Select(s => s.EffectName.ToLowerInvariant()).Distinct());
            if (look.SoundMode != 0 && look.SoundVolume != 0) effects = effects.Length == 0 ? "sound" : effects + ", sound";
            if (effects.Length > 0) card.Children.Add(new TextBlock { Text = char.ToUpperInvariant(effects[0]) + effects[1..], FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            int target = state;
            var button = new Button { Content = card, Padding = new Thickness(6, 10, 6, 8), Margin = new Thickness(0, 0, 8, 8), VerticalContentAlignment = VerticalAlignment.Top, ToolTip = "Look at this state under Try inputs" };
            button.SetResourceReference(StyleProperty, "QuietButton");
            button.SetResourceReference(ForegroundProperty, "Ink");
            button.Click += (_, _) => { foreach (var (input, bit) in InputSwitches.Select((t, i) => (t, i))) input.IsChecked = (target & (1 << bit)) != 0; WorkflowTabs.SelectedIndex = 1; };
            StateTablePanel.Children.Add(button);
        }
        if (StateTablePanel.Children.Count == 0) StateTablePanel.Children.Add(new TextBlock { Text = "This file has no configured states." });
    }

    private static Brush WireBrush(SignalLabels.Input input) => input.WireColor is { } c ? new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)) : Brushes.Transparent;

    // The inputs that make this state, as wire-coloured chips, and the state's own name when it has one.
    private FrameworkElement StateHeading(int state)
    {
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var chips = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var active = _labels.Inputs.Where(input => (state & (1 << (input.Number - 1))) != 0).ToArray();
        if (active.Length == 0) chips.Children.Add(new TextBlock { Text = "No inputs", FontWeight = FontWeights.SemiBold });
        foreach (var input in active)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(3, 1, 3, 1), ToolTip = input.Label + (input.Description.Length == 0 ? "" : " — " + input.Description) };
            if (input.WireColor is not null) chip.Children.Add(new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(6), Background = WireBrush(input), BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            chip.Children.Add(new TextBlock { Text = $"In {input.Number}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            chips.Children.Add(chip);
        }
        heading.Children.Add(chips);
        if (_labels.NameFor(state) is { } name) heading.Children.Add(new TextBlock { Text = name, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        return heading;
    }

    // Input switches show the wire colour and name stored with the configuration.
    private void ApplyInputLabels()
    {
        foreach (var (toggle, input) in InputSwitches.Zip(_labels.Inputs))
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (input.WireColor is not null) content.Children.Add(new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(6), Background = WireBrush(input), BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = input.Label });
            toggle.Content = content;
            toggle.ToolTip = $"Preview {input.Label} ON / OFF" + (input.Description.Length == 0 ? "" : " — " + input.Description);
        }
    }

    private async Task LoadPairedPdfAsync(string? signalPath)
    {
        _pdfLoad?.Cancel(); var source = new CancellationTokenSource(); _pdfLoad = source;
        PairedPdfImage.Source = null; OpenPdfButton.IsEnabled = false; _pairedPdf = null;
        string? pdf = signalPath is null ? null : await Task.Run(() => CompanionPdf(signalPath));
        if (source.IsCancellationRequested) return;
        _pairedPdf = pdf; OpenPdfButton.IsEnabled = pdf is not null && OpenFile is not null;
        PairedPdfText.Visibility = Visibility.Visible;
        PairedPdfText.Text = signalPath is null ? "The PDF saved with this configuration shows here."
            : pdf is null ? "No PDF is saved with this configuration. Upload it as a pair with the .signal file." : "Loading PDF…";
        if (pdf is null || RenderPdf is null) { if (pdf is not null) PairedPdfText.Text = System.IO.Path.GetFileName(pdf); return; }
        try
        {
            var image = await RenderPdf(pdf, source.Token);
            if (source.IsCancellationRequested) return;
            PairedPdfImage.Source = image;
            PairedPdfText.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
            if (image is null) PairedPdfText.Text = "The PDF could not be shown here. Use Open PDF.";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!source.IsCancellationRequested) PairedPdfText.Text = "The PDF could not be shown here. Use Open PDF."; }
    }
    private void OpenPdf_Click(object sender, RoutedEventArgs e) { if (_pairedPdf is not null) OpenFile?.Invoke(_pairedPdf); }

    private ToggleButton[] InputSwitches => [Input1Toggle, Input2Toggle, Input3Toggle, Input4Toggle, Input5Toggle, Input6Toggle];
    private int PreviewState => InputSwitches.Select((input, bit) => input.IsChecked == true ? 1 << bit : 0).Sum();
    private void Input_Changed(object sender, RoutedEventArgs e) { if (InputStateText is null) return; UpdatePreview(); ResetObservation(); }
    private void ClearInputs_Click(object sender, RoutedEventArgs e) { foreach (var input in InputSwitches) input.IsChecked = false; }
    private void UpdatePreview()
    {
        int state = PreviewState;
        string on = string.Join(", ", _labels.Inputs.Where(input => (state & (1 << (input.Number - 1))) != 0).Select(input => input.Label));
        InputStateText.Text = $"Inputs ON: {(on.Length == 0 ? "none" : on)}" + (_labels.NameFor(state) is { } name ? $"  •  {name}" : "") + $"  •  stored combination {state}";
        SegmentPreviewPanel.Content = null;
        PreviewText.Text = _selectedUpload is null ? "Choose a configuration to view expected outputs." : SignalStatePreview.Describe(_selectedUpload, state);
        if (_selectedUpload is null) { SoundPreviewText.Text = "Choose a configuration to see its lights and sound."; return; }
        var appearance = SignalStatePreview.ReadAppearance(_selectedUpload, state);
        SegmentPreviewPanel.Content = SignalColumnGraphic.Build(appearance, 26, 64, _labels.UpsideDown, labels: true);
        SoundPreviewText.Text = appearance.SoundMode == 0 || appearance.SoundVolume == 0 ? "Sound: off" : $"Sound: configured • mode {appearance.SoundMode}, volume {appearance.SoundVolume}";
    }
    private void ResetObservation() { if (ObservationCombo is not null) ObservationCombo.SelectedIndex = 0; if (ObservationText is not null) ObservationText.Text = ""; }
    private void Observation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ObservationText is null) return;
        ObservationText.Text = ObservationCombo.SelectedIndex switch
        {
            1 when _selectedUpload is not null => "Recorded for this input state only. Check the other states too.",
            2 when _selectedUpload is not null => "Check the wiring and input signals against the drawing, and that this is the right file.",
            _ => ""
        };
    }

    public FrameworkElement CreateEmbeddedView(Window owner)
    {
        Owner = owner;
        foreach (var tab in WorkflowTabs.Items.OfType<TabItem>().Skip(2)) tab.Visibility = Visibility.Collapsed;
        var view = (FrameworkElement)Content; Content = null;
        bool loaded = false;
        view.Loaded += (sender, args) => { if (!loaded) { loaded = true; Window_Loaded(sender, args); } };
        return view;
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        MaxHeight = SystemParameters.WorkArea.Height;
        if (_previewOnly)
        {
            Title = "Safety Gate Support — Home Preview • Signal column";
            DetectionText.Text = "Preview: connecting and uploading are turned off.";
            ResultText.Text = "Preview only. No hardware is used.";
            StatusText.Text = "Preview only • no USB access";
            UpdateControls();
            SelectOnlyConfiguration();
            return;
        }
        await RefreshDevicesAsync();
        SelectOnlyConfiguration();
    }
    // With exactly one configuration on file there is nothing to choose.
    private void SelectOnlyConfiguration()
    {
        if (AssignedFilesCombo.Items.Count == 1 && AssignedFilesCombo.SelectedIndex < 0 && _selectedUpload is null) AssignedFilesCombo.SelectedIndex = 0;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        if (_previewOnly || _isRefreshing || _operationCancellation is not null)
            return;

        var previous = DeviceCombo.SelectedItem as UsbColumnPort;
        _isRefreshing = true;
        UpdateControls();
        DetectionText.Text = "Checking connected devices…";
        try
        {
            // Registry and PnP enumeration only: detection never opens a port.
            var ports = await Task.Run(WindowsColumnConnection.DetectPorts);
            DeviceCombo.ItemsSource = ports;
            DeviceCombo.SelectedItem = previous is null ? null : ports.FirstOrDefault(port =>
                string.Equals(port.PortName, previous.PortName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(port.DeviceInstanceId, previous.DeviceInstanceId, StringComparison.OrdinalIgnoreCase));
            if (DeviceCombo.SelectedItem is null && ports.Count == 1)
                DeviceCombo.SelectedItem = ports[0];

            DetectionText.Text = ports.Count switch
            {
                0 => "No column found. Check the cable, then Look again.",
                1 => "✓ Column found: " + ports[0].DisplayName,
                _ => $"{ports.Count} columns found. Choose the one to load:"
            };
            DeviceCombo.Visibility = ports.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception error)
        {
            DeviceCombo.ItemsSource = null;
            DetectionText.Text = "Windows could not list connected columns. Look again.";
            ResultText.Text = error.Message;
        }
        finally
        {
            _isRefreshing = false;
            UpdateControls();
        }
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged can fire while InitializeComponent is still running.
        if (ReadButton is not null)
        {
            _comparison = "Stored configuration has not been checked for this USB selection.";
            ResetObservation();
            if (ResultText is not null) ResultText.Text = _comparison + "\nRead the selected column to compare. Detection does not open USB.";
            UpdateControls();
        }
    }

    private async void ReadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewOnly || _isRefreshing || _operationCancellation is not null || DeviceCombo.SelectedItem is not UsbColumnPort port)
            return;

        string reportFile;
        try { reportFile = System.IO.Path.Combine(LocalReportFolder(), $"Siemens-8WD46-USB-check-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}.zip"); }
        catch (Exception error)
        {
            ResultText.Text = $"Windows could not create the report folder:\n{error.Message}";
            StatusText.Text = "No device was opened.";
            return;
        }

        ResetObservation();
        _comparison = "Stored configuration has not been verified in this session.";
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        UpdateControls();
        ResultText.Text = $"Reading {port.DisplayName}\n\nReport:\n{reportFile}";
        StatusText.Text = "Reading the column…"; MatchText.Text = "";
        var progress = new Progress<string>(message => StatusText.Text = message);

        try
        {
            var result = await Task.Run(() => UsbCheckSession.Run(
                port, reportFile, cancellation.Token, ((IProgress<string>)progress).Report));
            if (result.Success && result.Configuration is not null && _selectedUpload is not null)
                _comparison = SignalConfiguration.Equivalent(result.Configuration, _selectedUpload.GetBytes())
                    ? "MATCH: stored settings match the selected file. Physical operation is unverified."
                    : "DIFFERENT: stored settings differ from the selected file. Confirm the file assignment before changing the column.";
            ResultText.Text = $"{result.Summary}\n\n{_comparison}\n\nReport:\n{reportFile}";
            StatusText.Text = result.Success ? "Column read." : "The column could not be read completely. See More options for details.";
            SetMatch(_comparison);
        }
        catch (OperationCanceledException)
        {
            ResultText.Text = "The USB check was canceled. A complete report could not be confirmed.";
            StatusText.Text = "Canceled.";
        }
        catch (Exception error)
        {
            ResultText.Text = $"The USB check could not finish:\n{error.Message}\n\nA complete report could not be confirmed. Check the destination and reconnect the spare column before retrying.";
            StatusText.Text = "USB check failed.";
        }
        finally
        {
            _operationCancellation = null;
            UpdateControls();
            if (_closeWhenFinished)
                Close();
        }
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing || _operationCancellation is not null) return;
        try
        {
            var source = new OpenFileDialog { Title = "Choose a column configuration", Filter = "Siemens configuration (*.signal)|*.signal", CheckFileExists = true };
            if (source.ShowDialog(Owner ?? this) != true) return;
            AssignedFilesCombo.SelectedIndex = -1;
            await SelectFileAsync(source.FileName);
        }
        catch (Exception error)
        {
            _selectedUpload = null;
            SelectedSignalText.Text = "No valid configuration selected.";
            ResultText.Text = error.Message;
            StatusText.Text = "The file was not accepted. No USB device was opened.";
        }
        finally { _isRefreshing = false; UpdateControls(); }
    }

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewOnly || _isRefreshing || _operationCancellation is not null || _selectedUpload is not { } selected ||
            DeviceCombo.SelectedItem is not UsbColumnPort port) return;
        string reportPath;
        try
        {
            reportPath = System.IO.Path.Combine(LocalReportFolder(), $"Siemens-8WD46-upload-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}.zip");
            if (System.IO.File.Exists(reportPath) || System.IO.File.Exists(System.IO.Path.ChangeExtension(reportPath, ".before.signal")))
                throw new System.IO.IOException("Choose a new filename so existing reports and backups are preserved.");
            if (MessageBox.Show(Owner ?? this,
                $"Upload {selected.FileName} to the column?\n{_context}\n\nThe column’s current settings are backed up first.",
                "Upload to column", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        catch (Exception error) { ResultText.Text = error.Message; StatusText.Text = "No upload was started."; return; }

        ResetObservation();
        _comparison = "Stored configuration has not been verified in this session.";
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        UpdateControls();
        ResultText.Text = $"Selected file: {selected.FileName}\nUpload report: {reportPath}";
        var progress = new Progress<string>(message => StatusText.Text = message);
        try
        {
            var result = await Task.Run(() => UsbUploadSession.Run(port, selected, reportPath,
                cancellation.Token, ((IProgress<string>)progress).Report));
            _comparison = result.Success ? "MATCH: selected settings were verified. Physical operation is unverified." : "Stored configuration is not fully verified; review the upload report.";
            SetMatch(result.Success ? "MATCH" : "");
            ResultText.Text = $"{result.Summary}\n\nOriginal backup:\n{(result.BackupPath.Length == 0 ? "Not created; no upload was sent." : result.BackupPath)}\n\nReport:\n{result.ReportPath}";
            StatusText.Text = result.Success ? "✓ Uploaded and checked. Now check the lights and sound." :
                result.ReadbackVerified ? "Uploaded, but the session didn’t finish cleanly. See More options for the report." :
                "The upload couldn’t be confirmed. Try again; the previous settings were backed up first.";
        }
        catch (Exception error)
        {
            ResultText.Text = $"The upload session could not finish: {error.Message}\n\nKeep any .before.signal backup beside the report. If writing had started, the column may already have changed settings.";
            StatusText.Text = "Upload could not be verified.";
        }
        finally { _operationCancellation = null; UpdateControls(); if (_closeWhenFinished) Close(); }
    }

    private void SetMatch(string comparison)
    {
        bool match = comparison.StartsWith("MATCH", StringComparison.Ordinal), differ = comparison.StartsWith("DIFFERENT", StringComparison.Ordinal);
        MatchText.Text = match ? "✓ Stored settings match the selected file" : differ ? "✗ The column has different settings" : "";
        MatchText.Foreground = new SolidColorBrush(match ? Color.FromRgb(30, 132, 73) : Color.FromRgb(183, 110, 0));
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Canceling… Waiting for the driver and report. Cancellation cannot undo settings already written.";
    }

    private static string LocalReportFolder()
    {
        string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SiemensUsbCheck", "Reports");
        System.IO.Directory.CreateDirectory(folder);
        return folder;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_operationCancellation is not null)
        {
            e.Cancel = true;
            _closeWhenFinished = true;
            _operationCancellation.Cancel();
            CancelButton.IsEnabled = false;
            StatusText.Text = "Canceling before closing… Waiting for report cleanup.";
        }
        base.OnClosing(e);
    }

    private void UpdateControls()
    {
        bool running = _operationCancellation is not null;
        DeviceCombo.IsEnabled = !_previewOnly && !running && !_isRefreshing;
        if (AssignedFilesCombo is not null) AssignedFilesCombo.IsEnabled = !running && !_isRefreshing;
        foreach (var input in InputSwitches) input.IsEnabled = !running && !_isRefreshing;
        ClearInputsButton.IsEnabled = !running && !_isRefreshing;
        if (ObservationCombo is not null) ObservationCombo.IsEnabled = !running && !_isRefreshing && _selectedUpload is not null;
        RefreshButton.IsEnabled = !_previewOnly && !running && !_isRefreshing;
        ReadButton.IsEnabled = !_previewOnly && !running && !_isRefreshing && DeviceCombo.SelectedItem is UsbColumnPort;
        CancelButton.IsEnabled = running && !_operationCancellation!.IsCancellationRequested;
        CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (BrowseButton is not null) BrowseButton.IsEnabled = !running && !_isRefreshing;
        if (UploadButton is not null) UploadButton.IsEnabled = !_previewOnly && !running && !_isRefreshing &&
            DeviceCombo.SelectedItem is UsbColumnPort && _selectedUpload is { IsPreviewOnly: false };
    }
}
