using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SiemensUsbCheck.Services;
using SafetyGateSupport.Siemens;

namespace SiemensUsbCheck;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _operationCancellation;
    private bool _isRefreshing;
    private bool _closeWhenFinished;
    private PreparedSignalUpload? _selectedUpload;

    private readonly bool _previewOnly;
    private readonly string _context;
    private string _comparison = "Stored configuration has not been checked against the selected file.";
    private sealed record AssignedFile(string Path, string Label);

    public MainWindow() : this("", Array.Empty<string>()) { }
    public MainWindow(string context, IEnumerable<string> assignedFiles, bool previewOnly = false)
    {
        InitializeComponent();
        _context = context;
        _previewOnly = previewOnly;
        if (context.Length > 0)
        {
            Title = "Safety Gate Support — Signal column";
            ContextText.Text = context + " • confirm the connected column belongs to this unit";
            ContextText.Visibility = Visibility.Visible;
            AssignedFilesCombo.ItemsSource = assignedFiles.Select(p => new AssignedFile(p, System.IO.Path.GetFileName(p))).ToList();
            AssignedFilesCombo.Visibility = Visibility.Visible;
            AssignedFilesCombo.Text = "Select this unit's approved configuration";
            SelectedSignalText.Text = "Select an assigned file above, or browse for an approved .signal file. No file is selected automatically.";
        }
        StateCombo.ItemsSource = Enumerable.Range(0, 64).Select(n => $"State {n} — inputs: {string.Join(", ", Enumerable.Range(1, 6).Where(i => (n & (1 << (i - 1))) != 0))}");
        StateCombo.SelectedIndex = 0;
    }

    private async void AssignedFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AssignedFilesCombo.SelectedItem is AssignedFile file) await SelectFileAsync(file.Path);
    }

    private async Task SelectFileAsync(string path)
    {
        if (_isRefreshing || _operationCancellation is not null) return;
        _selectedUpload = null;
        ResetObservation();
        _comparison = "Stored configuration has not been checked against the selected file.";
        try
        {
            _isRefreshing = true; UpdateControls();
            _selectedUpload = await Task.Run(() => PreparedSignalUpload.Load(path));
            SelectedSignalText.Text = $"{_selectedUpload.FileName} • {_selectedUpload.Archive.ActiveStates.Count} active states\n{path}";
            ResultText.Text = _comparison + "\nConfirm this file's asset/unit assignment before upload.";
            StatusText.Text = _previewOnly || _selectedUpload.IsPreviewOnly ? "Preview only: explore the expected output below. This sample cannot be uploaded." : "File checked. Read the column to compare; upload saves a backup before writing.";
        }
        catch (Exception error)
        {
            SelectedSignalText.Text = "No valid configuration selected.";
            ResultText.Text = error.Message;
        }
        finally { _isRefreshing = false; UpdateControls(); UpdatePreview(); }
    }

    private void StateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdatePreview(); ResetObservation(); }
    private void UpdatePreview()
    {
        if (PreviewText is not null) PreviewText.Text = _selectedUpload is null ? "Choose a configuration to view expected outputs." : SignalStatePreview.Describe(_selectedUpload, Math.Max(0, StateCombo.SelectedIndex));
    }
    private void ResetObservation() { if (ObservationCombo is not null) ObservationCombo.SelectedIndex = 0; if (ObservationText is not null) ObservationText.Text = "Physical function is unverified. Check all applicable states using the approved site test procedure."; }
    private void Observation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ObservationText is null) return;
        ObservationText.Text = ObservationCombo.SelectedIndex switch
        {
            1 when _selectedUpload is not null => "You recorded a match for this input state only. " + _comparison + " Check the remaining applicable states before returning the equipment to service.",
            2 when _selectedUpload is not null => "Physical mismatch recorded. Review supply, wiring, actual input signals, and the assigned configuration using the drawings. USB matching alone does not establish function.",
            _ => "Physical function is unverified. Choose a file and inspect the real light and sound before recording an observation."
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        MaxHeight = SystemParameters.WorkArea.Height;
        if (_previewOnly)
        {
            Title = "Safety Gate Support — Home Preview • Signal column";
            DetectionText.Text = "Home preview: device discovery, USB reads, and uploads are disabled. Explore a selected file and expected output below.";
            ResultText.Text = "Preview only. Choose the assigned DEMO ONLY file, then expand Expected output and select state 1 (red), 2 (amber), 4 (green), or 8 (sound). No hardware is required.";
            StatusText.Text = "Local preview • no USB access • illustrative configuration only";
            UpdateControls();
            return;
        }
        await RefreshDevicesAsync();
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
                0 => "No supported column detected. Check the data cable and Windows driver, then click Refresh. Bootloader devices are excluded.",
                1 => "One supported USB device found. Detection has not opened it.",
                _ => $"{ports.Count} supported USB devices found. Select the spare column before reading."
            };
        }
        catch (Exception error)
        {
            DeviceCombo.ItemsSource = null;
            DetectionText.Text = "Windows could not list the connected USB columns.";
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

        var destination = new SaveFileDialog
        {
            Title = "Save Siemens USB diagnostic report",
            Filter = "ZIP diagnostic report (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            CheckPathExists = true,
            OverwritePrompt = true,
            FileName = $"Siemens-8WD46-USB-check-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        try
        {
            destination.InitialDirectory = LocalReportFolder();
            if (destination.ShowDialog(this) != true)
                return;
        }
        catch (Exception error)
        {
            ResultText.Text = $"Windows could not choose a report destination:\n{error.Message}";
            StatusText.Text = "No device was opened.";
            return;
        }

        ResetObservation();
        _comparison = "Stored configuration has not been verified in this session.";
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        UpdateControls();
        ResultText.Text = $"Reading {port.DisplayName}\n\nReport destination:\n{destination.FileName}";
        StatusText.Text = "Opening the selected spare column…";
        var progress = new Progress<string>(message => StatusText.Text = message);

        try
        {
            var result = await Task.Run(() => UsbCheckSession.Run(
                port, destination.FileName, cancellation.Token, ((IProgress<string>)progress).Report));
            if (result.Success && result.Configuration is not null && _selectedUpload is not null)
                _comparison = SignalConfiguration.Equivalent(result.Configuration, _selectedUpload.GetBytes())
                    ? "MATCH: stored settings match the selected file. Physical operation is unverified."
                    : "DIFFERENT: stored settings differ from the selected file. Confirm the file assignment before changing the column.";
            ResultText.Text = $"{result.Summary}\n\n{_comparison}\n\nReport:\n{destination.FileName}";
            StatusText.Text = result.Success ? "USB read completed. Report saved." : "USB check finished. Review the report for details.";
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
            if (source.ShowDialog(this) != true) return;
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
            var destination = new SaveFileDialog
            {
                Title = "Choose a new upload report and backup location",
                Filter = "Upload report (*.zip)|*.zip", DefaultExt = ".zip", AddExtension = true,
                CheckPathExists = true, OverwritePrompt = true,
                InitialDirectory = LocalReportFolder(),
                FileName = $"Siemens-8WD46-upload-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}.zip"
            };
            if (destination.ShowDialog(this) != true) return;
            reportPath = destination.FileName;
            if (System.IO.File.Exists(reportPath) || System.IO.File.Exists(System.IO.Path.ChangeExtension(reportPath, ".before.signal")))
                throw new System.IO.IOException("Choose a new filename so existing reports and backups are preserved.");
            if (MessageBox.Show(this,
                $"Replace the settings on {port.DisplayName} with '{selected.FileName}'?\n{_context}\n\nIsolate equipment for servicing. Confirm this is the intended column and approved file.\n\nThe tool will save the current configuration first, upload the selected file, request a restart, and verify the stored settings.\n\nContinue?",
                "Upload column configuration", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
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
            ResultText.Text = $"{result.Summary}\n\nOriginal backup:\n{(result.BackupPath.Length == 0 ? "Not created; no upload was sent." : result.BackupPath)}\n\nReport:\n{result.ReportPath}";
            StatusText.Text = result.Success ? "Settings verified. Keep the backup and check the spare column's behavior." :
                result.ReadbackVerified ? "Stored settings matched, but the session did not finish cleanly. Keep the backup and report." :
                "Upload not fully verified. Keep the backup and report; do not assume the old settings are still active.";
        }
        catch (Exception error)
        {
            ResultText.Text = $"The upload session could not finish: {error.Message}\n\nKeep any .before.signal backup beside the report. If writing had started, the column may already have changed settings.";
            StatusText.Text = "Upload could not be verified.";
        }
        finally { _operationCancellation = null; UpdateControls(); if (_closeWhenFinished) Close(); }
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
        if (ObservationCombo is not null) ObservationCombo.IsEnabled = !running && !_isRefreshing && _selectedUpload is not null;
        RefreshButton.IsEnabled = !_previewOnly && !running && !_isRefreshing;
        ReadButton.IsEnabled = !_previewOnly && !running && !_isRefreshing && DeviceCombo.SelectedItem is UsbColumnPort;
        CancelButton.IsEnabled = running && !_operationCancellation!.IsCancellationRequested;
        if (BrowseButton is not null) BrowseButton.IsEnabled = !running && !_isRefreshing;
        if (UploadButton is not null) UploadButton.IsEnabled = !_previewOnly && !running && !_isRefreshing &&
            DeviceCombo.SelectedItem is UsbColumnPort && _selectedUpload is { IsPreviewOnly: false };
    }
}
