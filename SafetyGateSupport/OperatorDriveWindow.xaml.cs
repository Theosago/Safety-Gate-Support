using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class OperatorDriveWindow : Window
{
    private readonly DataService _data;
    private readonly string _root;
    private readonly string? _selectedUnitId;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource _closed = new();
    private OperatorPublicationUnit[] _units = [];
    private OperatorPublishingState _state = new();
    private bool _busy;
    private bool _refreshing;
    private string? _message;
    internal bool GuidePdfsChanged { get; private set; }

    public OperatorDriveWindow(DataService data, string? selectedUnitId = null)
    {
        InitializeComponent();
        _data = data;
        _root = Path.GetFullPath(data.SharedRoot);
        _selectedUnitId = selectedUnitId;
        Loaded += async (_, _) =>
        {
            await RefreshAsync(loadUnits: true);
            if (!_closed.IsCancellationRequested) _timer.Start();
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _closed.Cancel();
        };
    }

    private async Task RefreshAsync(bool loadUnits = false)
    {
        if (_busy || _refreshing || _closed.IsCancellationRequested) return;
        _refreshing = true;
        try
        {
            var snapshot = await Task.Run(() =>
            {
                var state = OperatorPublishingState.Load(_root);
                state.ImportPilot(_root);
                return (state, units: loadUnits ? OperatorPublicationSources.ListUnits(_data) : null);
            });
            if (_closed.IsCancellationRequested) return;
            _state = snapshot.state;
            if (snapshot.units is not null)
            {
                string? selected = (GuideUnitPicker.SelectedItem as OperatorPublicationUnit)?.Unit.Id ?? _selectedUnitId;
                _units = snapshot.units;
                GuideUnitPicker.ItemsSource = _units;
                GuideUnitPicker.SelectedItem = _units.FirstOrDefault(item => item.Unit.Id == selected) ?? _units.FirstOrDefault();
            }
            Render();
        }
        catch (Exception error)
        {
            if (!_closed.IsCancellationRequested)
            {
                StatusLabel.Text = error.Message;
                PublishButton.IsEnabled = PrintButton.IsEnabled = false;
            }
        }
        finally { _refreshing = false; }
    }

    private void Render()
    {
        bool connected = OperatorDriveRunner.CanPublish;
        ConnectionLabel.Text = connected ? "Google Drive connected" : "Connect Google Drive";
        ConnectButton.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        int ready = _units.Count(item => HasQr(item));
        SummaryLabel.Text = _state.PublishAll
            ? $"{ready} of {_units.Length} unit folders ready for QR labels. Drawing updates publish automatically while a signed-in app is open."
            : connected
                ? "Publish all units to create their Drive folders, guides and QR destinations automatically."
                : ready > 0 ? $"{ready} saved unit folders. Sign in to publish updates." : "Sign in once on this computer to publish the library.";
        StatusLabel.Text = _message ?? OperatorDriveRunner.Status;
        PublishButton.Content = _state.PublishAll ? "Publish updates now" : "Publish all units";
        PublishButton.IsEnabled = !_busy && connected && _units.Length > 0;
        PublishButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        PrintButton.Visibility = ready > 0 && ready == _units.Length ? Visibility.Visible : Visibility.Collapsed;
        PrintButton.IsEnabled = !_busy;
        AddGuideButton.IsEnabled = !_busy && _units.Length > 0;
        SwitchClientButton.IsEnabled = !_busy;
        OpenDriveButton.Visibility = MobileSupportExporter.ValidLink(_state.Root?.Link ?? "") ? Visibility.Visible : Visibility.Collapsed;
        ChooseFolderPanel.Visibility = _state.Root is null ? Visibility.Visible : Visibility.Collapsed;
        SyncNotesBox.IsChecked = _state.SyncSharedNotes;
        SyncNotesBox.IsEnabled = !_busy && ready > 0 && (connected || _state.SyncSharedNotes);
        NotesStatusLabel.Text = _state.SyncSharedNotes
            ? _state.NotesError ?? "Open Operator notes from a unit's QR folder. Changes sync with the app while a publisher is running."
            : ready > 0 ? "Copies existing shared notes to the QR folders under their current Drive sharing settings."
                : "Publish a unit to make its notes available for sync.";
        EnableDocsButton.Visibility = _state.SyncSharedNotes && _state.NotesError?.StartsWith("Enable the Google Docs API", StringComparison.Ordinal) == true
            ? Visibility.Visible : Visibility.Collapsed;
        var issues = new List<string>();
        foreach (var item in _units)
        {
            if (!_state.Units.TryGetValue(item.Unit.Id, out var unit)) continue;
            if (unit.Error is not null) issues.Add($"{item.Label} — {unit.Error}");
            if (_state.SyncSharedNotes && unit.NotesError is not null) issues.Add($"{item.Label} · Shared notes — {unit.NotesError}");
        }
        IssuesList.ItemsSource = issues;
        IssuesPanel.Header = $"Publishing issues ({issues.Count})";
        IssuesPanel.Visibility = issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ConnectAsync(!OperatorDriveRunner.HasSavedClient);
    private async void SwitchClient_Click(object sender, RoutedEventArgs e) => await ConnectAsync(chooseJson: true);

    private async Task ConnectAsync(bool chooseJson)
    {
        if (_busy) return;
        string? path = null;
        if (chooseJson)
        {
            var picker = new OpenFileDialog { Title = "Choose the Google Desktop app OAuth JSON", Filter = "Google OAuth client JSON|*.json" };
            if (picker.ShowDialog(this) != true) return;
            path = picker.FileName;
        }
        await RunActionAsync("Complete Google sign-in in your browser.", async () =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            await OperatorDriveRunner.SignInAsync(path, timeout.Token);
            return "Connected to Google Drive.";
        });
    }

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string? rootId = null;
        _message = null;
        if (_state.Root is null && !string.IsNullOrWhiteSpace(FolderInput.Text))
        {
            rootId = FolderId(FolderInput.Text);
            if (rootId is null)
            {
                _message = "Enter the link to the existing Google Drive publishing folder.";
                StatusLabel.Text = _message;
                return;
            }
        }

        await RunActionAsync("Preparing unit folders and documents…", async () =>
        {
            var progress = new Progress<OperatorPublishProgress>(p =>
            {
                if (_closed.IsCancellationRequested || !_busy || p.Unit.Length == 0) return;
                PublishProgress.Visibility = Visibility.Visible;
                PublishProgress.Maximum = Math.Max(1, p.Total);
                PublishProgress.Value = p.Completed;
                StatusLabel.Text = $"Checking {p.Completed + 1} of {p.Total} · {p.Unit}";
            });
            return await OperatorDriveRunner.RunAsync(_data, publishNow: true, requestedRoot: rootId, progress: progress, token: _closed.Token);
        });
    }

    private async void SyncNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        bool enabled = SyncNotesBox.IsChecked == true;
        await RunActionAsync("Saving notes sync settings…", () =>
            OperatorDriveRunner.RunAsync(_data, token: _closed.Token, syncSharedNotes: enabled));
    }

    private void EnableDocs_Click(object sender, RoutedEventArgs e) =>
        FileLauncherService.Open("https://console.cloud.google.com/apis/library/docs.googleapis.com");

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _units.Length == 0 || !_units.All(HasQr)) return;
        var labels = _units.Select(item => new UnitQrLabel(item.AssetFolder, item.UnitFolder,
            item.Gate.Location, _state.Units[item.Unit.Id].Folder!.Link!)).ToArray();
        var picker = new SaveFileDialog { Title = "Save all unit QR codes for printing", Filter = "PDF document|*.pdf",
            FileName = "Safety Gate Unit QR Codes.pdf", AddExtension = true };
        if (picker.ShowDialog(this) != true) return;
        await RunActionAsync("Preparing the QR sheets…", async () =>
        {
            await Task.Run(() => OperatorPublishingState.WriteFile(picker.FileName, OperatorPublishingPdf.QrLabels(labels)), _closed.Token);
            if (!_closed.IsCancellationRequested) FileLauncherService.Open(picker.FileName);
            return $"{labels.Length} QR codes saved in one PDF. Print all pages at actual size.";
        });
    }

    private bool HasQr(OperatorPublicationUnit item) =>
        _state.Units.GetValueOrDefault(item.Unit.Id) is { Folder: { Pending: false, Link: { } url } } entry
        && entry.GateId == item.Gate.Id &&
        MobileSupportExporter.ValidLink(url);

    private void OpenDrive_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Root?.Link is { } link) FileLauncherService.Open(link);
    }

    private async void AddGuide_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || GuideUnitPicker.SelectedItem is not OperatorPublicationUnit item) return;
        var picker = new OpenFileDialog { Title = $"Choose a guide PDF for {item.AssetFolder}", Filter = "PDF document|*.pdf" };
        if (picker.ShowDialog(this) != true) return;
        string name = Path.GetFileName(picker.FileName);
        await RunActionAsync("Checking unit coverage…", async () =>
        {
            var previous = await Task.Run(() => OperatorPublishingState.LoadGuidePdfs(_root, item.Gate.Id)
                .SingleOrDefault(pdf => pdf.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)), _closed.Token);
            _closed.Token.ThrowIfCancellationRequested();
            var eligible = _units.Where(unit => unit.Gate.Id == item.Gate.Id).Select(unit => unit.Unit).ToArray();
            var selection = new DrawingUnitsWindow(item.Gate, eligible, previous is null ? name : $"Replacing {name}",
                previous?.UnitIds ?? [item.Unit.Id], previous is null ? "Save guide PDF" : "Replace guide PDF") { Owner = this };
            if (selection.ShowDialog() != true) return "";
            StatusLabel.Text = "Saving the guide PDF…";
            await OperatorDriveRunner.SaveGuidePdfAsync(_data, _root, item.Gate.Id, selection.UnitIds, picker.FileName, previous);
            GuidePdfsChanged = true;
            return $"Saved {name} for {DrawingUnitsWindow.UnitNames(eligible, selection.UnitIds)}.";
        });
    }

    private async Task RunActionAsync(string working, Func<Task<string>> action)
    {
        _busy = true;
        _message = working;
        ConnectButton.IsEnabled = false;
        Render();
        try { _message = await action(); }
        catch (OperationCanceledException) { _message = "The operation ended before it was completed."; }
        catch (Google.Apis.Auth.OAuth2.Responses.TokenResponseException)
        {
            _message = "Google sign-in could not be completed. Check that this account is listed as a test user.";
        }
        catch (Exception error) { _message = error.Message; }
        finally
        {
            _busy = false;
            if (!_closed.IsCancellationRequested)
            {
                ConnectButton.IsEnabled = true;
                PublishProgress.Visibility = Visibility.Collapsed;
                await RefreshAsync(loadUnits: true);
            }
        }
    }

    private static string? FolderId(string value)
    {
        value = value.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase)) return null;
            string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int index = Array.IndexOf(parts, "folders");
            if (index < 0 || index + 1 >= parts.Length) return null;
            value = parts[index + 1];
        }
        return value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? value : null;
    }
}
