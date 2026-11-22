using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class OperatorDriveWindow : Window
{
    private readonly string _root;
    private readonly SafetyGate _asset;
    private readonly SafetySystem _unit;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _busy;
    private string? _notesLink;

    public OperatorDriveWindow(DataService data, SafetyGate asset, SafetySystem unit)
    {
        InitializeComponent();
        _root = Path.GetFullPath(data.SharedRoot);
        _asset = asset;
        _unit = unit;
        UnitText.Text = $"{asset.AssetDisplay} / {unit.UnitDisplay}";
        Refresh(populate: true);
        _refresh.Tick += (_, _) => Refresh();
        _refresh.Start();
        Closed += (_, _) => _refresh.Stop();
    }

    private void Refresh(bool populate = false)
    {
        if (_busy) return;
        try
        {
            var pilot = OperatorDrivePilot.Load(OperatorDrivePilot.StatePath);
            bool matches = pilot?.Matches(_root, _asset.Id, _unit.Id) == true;
            bool canEdit = pilot is null || (matches && pilot.CanEditMapping);
            BrowseButton.IsEnabled = RootBox.IsEnabled = FileBox.IsEnabled = canEdit;
            SyncButton.IsEnabled = pilot is null || matches;
            PauseButton.IsEnabled = matches && !pilot!.Paused;
            SyncButton.Content = pilot is null ? "Enable sync" : pilot.Paused ? "Resume sync" : canEdit ? "Save and sync" : "Sync now";
            if (matches)
            {
                if (populate || !canEdit)
                {
                    PdfBox.Text = pilot!.RelativePdf;
                    RootBox.Text = pilot.DriveRoot;
                    FileBox.Text = pilot.FileId ?? "";
                }
                LinkBox.Text = pilot!.FolderLink ?? "";
                _notesLink = pilot.NotesLink;
                StatusText.Text = pilot.Paused ? "Automatic sync is paused." : OperatorDriveRunner.Status;
            }
            else if (pilot is not null)
            {
                StatusText.Text = $"This workstation is configured for {pilot.Asset} / {pilot.Unit}.";
            }
            LinkBox.Visibility = LinkBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            NotesButton.IsEnabled = MobileSupportExporter.ValidLink(_notesLink ?? "");
            OpenButton.IsEnabled = QrButton.IsEnabled = MobileSupportExporter.ValidLink(LinkBox.Text);
        }
        catch (Exception error)
        {
            SyncButton.IsEnabled = PauseButton.IsEnabled = false;
            StatusText.Text = error.Message;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "PDF documents|*.pdf", InitialDirectory = _root };
        if (picker.ShowDialog(this) != true) return;
        string relative = Path.GetRelativePath(_root, picker.FileName);
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar)[0] == "..")
        {
            StatusText.Text = "Choose a PDF inside the shared data folder.";
            return;
        }
        PdfBox.Text = relative;
        StatusText.Text = "";
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        string pdf = PdfBox.Text;
        string folder = RootBox.Text.Trim();
        string id = FileBox.Text.Trim();
        if (pdf.Length == 0 || !ValidId(folder) || (id.Length > 0 && !ValidId(id)))
        {
            StatusText.Text = "Choose a shared operator PDF and enter Google Drive IDs.";
            return;
        }
        string gateId = _asset.Id;
        string unitId = _unit.Id;
        string assetName = _asset.AssetDisplay;
        string unitName = _unit.UnitDisplay;
        await ConfigureAsync(previous =>
        {
            if (previous is not null && !RequireMatch(previous).CanEditMapping) return previous with { Paused = false };
            return new OperatorDrivePilot(_root, gateId, unitId, assetName, unitName, pdf, folder,
                id.Length == 0 ? null : id);
        });
    }

    private async void Pause_Click(object sender, RoutedEventArgs e) =>
        await ConfigureAsync(previous => RequireMatch(previous) with { Paused = true });

    private async Task ConfigureAsync(Func<OperatorDrivePilot?, OperatorDrivePilot> configure)
    {
        _busy = true;
        SyncButton.IsEnabled = PauseButton.IsEnabled = false;
        BrowseButton.IsEnabled = RootBox.IsEnabled = FileBox.IsEnabled = false;
        StatusText.Text = "Checking operator PDF sync…";
        try { await OperatorDriveRunner.RunAsync(_root, configure); }
        finally { _busy = false; Refresh(populate: true); }
    }

    private OperatorDrivePilot RequireMatch(OperatorDrivePilot? pilot) =>
        pilot?.Matches(_root, _asset.Id, _unit.Id) == true
            ? pilot : throw new InvalidOperationException("The saved pilot changed. Reopen this window.");

    private static bool ValidId(string id) => id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try { FileLauncherService.Open(LinkBox.Text); }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private void Notes_Click(object sender, RoutedEventArgs e)
    {
        if (_notesLink is not null) FileLauncherService.Open(_notesLink);
    }

    private void Qr_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Filter = "Printable QR image|*.svg", FileName = "Operator-Unit-QR.svg" };
        if (picker.ShowDialog(this) != true) return;
        try { File.WriteAllText(picker.FileName, MobileSupportExporter.QrSvg(LinkBox.Text)); }
        catch (Exception error) { StatusText.Text = error.Message; }
    }
}
