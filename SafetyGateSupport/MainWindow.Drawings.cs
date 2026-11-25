using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using Microsoft.Win32;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class MainWindow
{
    private CancellationTokenSource? _drawingPairCheck;
    private string? _drawingDwg;
    private bool _drawingDwgAvailable;
    private CancellationTokenSource? _drawingOrientationCancellation;
    private string _drawingFingerprint = "";
    private bool _checkingDrawingFiles;
    private readonly System.Windows.Threading.DispatcherTimer _drawingWatch = new() { Interval = TimeSpan.FromSeconds(15) };
    private void InitializeDrawingMonitor()
    {
        _drawingWatch.Tick += async (_, _) =>
        {
            if (_checkingDrawingFiles || _setupModeOpen || _isClosed || !SystemsView.IsVisible || _drawingPath.Length == 0 || SelectedGate is not { } gate || SelectedUnit is not { } unit) return;
            _checkingDrawingFiles = true; string path = _drawingPath, baseline = _drawingFingerprint; string? dwg = _drawingDwg;
            try
            {
                string current = await Task.Run(() => DrawingFingerprint(path, dwg));
                if (!_isClosed && SelectedUnit?.Id == unit.Id && path == _drawingPath && baseline.Length > 0 && current != baseline) LoadDrawings(gate, unit);
            }
            catch { /* A failed share probe is explained by the next explicit preview read. */ }
            finally { _checkingDrawingFiles = false; }
        };
        _drawingWatch.Start();
    }
    private static string DrawingFingerprint(string pdf, string? dwg)
    {
        static string Stamp(string? path) { if (path is null) return "none"; var file = new FileInfo(path); return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing"; }
        return Stamp(pdf) + "|" + Stamp(dwg);
    }
    private async void CheckDrawingPair(AssignedDrawings.FileItem file)
    {
        if (file.Resource is null || SelectedGate is not { } gate) return;
        _drawingPairCheck?.Cancel(); var source = new CancellationTokenSource(); _drawingPairCheck = source; var token = source.Token;
        DrawingPairStatusText.Text = "Checking PDF/DWG companions…";
        try
        {
            var status = await Task.Run(() => DrawingPairStatus.Check(_data, gate, file.Resource, file.Path, token), token);
            if (_isClosed || token.IsCancellationRequested || _drawingPath != file.Path) return;
            _drawingDwg = status.DwgPath;
            DrawingPairStatusText.Text = status.Message; DrawingPairStatusText.SetResourceReference(TextBlock.ForegroundProperty, status.NeedsAttention ? "ThemeForeground965813" : "InkMuted");
            var probe = await Task.Run(() => (Fingerprint: DrawingFingerprint(file.Path, status.DwgPath), Available: status.DwgPath is not null && File.Exists(status.DwgPath)), token);
            if (!token.IsCancellationRequested && !_isClosed && _drawingPath == file.Path) { _drawingFingerprint = probe.Fingerprint; _drawingDwgAvailable = probe.Available; UpdateDrawingControls(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_isClosed && !token.IsCancellationRequested) DrawingPairStatusText.Text = "Cannot check companion revisions: " + error.Message; }
        finally { if (ReferenceEquals(_drawingPairCheck, source)) _drawingPairCheck = null; source.Dispose(); }
    }

    private CancellationTokenSource? _drawingListing;
    private CancellationTokenSource? _drawingRendering;
    private string _drawingPath = "";
    private byte[]? _drawingBytes;
    private int _drawingPage, _drawingPageCount;
    private bool _settingDrawingFiles;
    private IReadOnlyList<string> _drawingPublicationUnits = [];

    private void ClearDrawings()
    {
        _drawingListing?.Cancel(); _drawingListing = null;
        _drawingPairCheck?.Cancel(); _drawingPairCheck = null; _drawingDwg = null; _drawingDwgAvailable = false; _drawingFingerprint = ""; DrawingPairStatusText.Text = "";
        _drawingRendering?.Cancel(); _drawingRendering = null;
        _settingDrawingFiles = true; DrawingFileBox.ItemsSource = null; _settingDrawingFiles = false;
        _drawingOrientationCancellation?.Cancel(); DrawingOrientationText.Text = "";
        _drawingPath = ""; _drawingBytes = null; _drawingPage = _drawingPageCount = 0;
        DrawingImage.Source = null; DrawingFileText.Text = ""; DrawingCoverageText.Text = ""; DrawingPageText.Text = ""; DrawingAssignmentText.Text = "";
        DrawingMessage.Text = "Select a unit to see its assigned electrical drawing."; DrawingMessage.Visibility = Visibility.Visible;
        DrawingFileBox.Visibility = Visibility.Collapsed;
        ShowDrawingOrParts(true);
        UpdateDrawingUploadButtons(false);
        UpdateDrawingControls();
    }

    private async void LoadDrawings(SafetyGate gate, SafetySystem unit)
    {
        ClearDrawings();
        var source = new CancellationTokenSource(); _drawingListing = source; var token = source.Token;
        DrawingMessage.Text = "Loading this unit’s assigned electrical drawings…";
        try
        {
            var listing = await Task.Run(() =>
            {
                var assigned = AssignedDrawings.List(_data, gate, unit, _data.LoadResources(), token);
                try
                {
                    var guideFiles = OperatorPublishingState.GuidePdfsFor(_data.SharedRoot, gate.Id, unit.Id)
                        .Select(pdf => new AssignedDrawings.FileItem("Guide PDF • " + Path.GetFileName(pdf.Path), pdf.Path) { GuideUnitIds = pdf.UnitIds });
                    return new AssignedDrawings.Listing(assigned.Files.Concat(guideFiles).ToArray(), assigned.Unavailable);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    return assigned with { Unavailable = [.. assigned.Unavailable, "Guide PDFs"] };
                }
            }, token);
            if (_isClosed || token.IsCancellationRequested) return;
            DrawingFileBox.Visibility = listing.Files.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _settingDrawingFiles = true; DrawingFileBox.ItemsSource = listing.Files; _settingDrawingFiles = false;
            if (listing.Files.Count == 0)
                DrawingMessage.Text = listing.Unavailable.Count > 0 ? "The drawing folder is unavailable. Check the shared library connection." : "No drawing assigned. Use Upload drawing to add the PDF and DWG.";
            else DrawingFileBox.SelectedIndex = 0;
            ShowDrawingOrParts(listing.Files.Count > 0 || listing.Unavailable.Count > 0);
            DrawingAssignmentText.Text = listing.Unavailable.Count > 0 ? "Unavailable files: " + string.Join(", ", listing.Unavailable)
                : listing.Files.Count > 1 ? $"{listing.Files.Count} files assigned to this unit." : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_isClosed && !token.IsCancellationRequested) DrawingMessage.Text = "Could not load the assigned drawing: " + error.Message; }
        finally
        {
            if (ReferenceEquals(_drawingListing, source))
            {
                _drawingListing = null;
                if (!_isClosed && !token.IsCancellationRequested && SelectedUnit?.Id == unit.Id)
                    UpdateDrawingUploadButtons(true);
            }
            source.Dispose();
        }
    }

    private async void DrawingFile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settingDrawingFiles || DrawingFileBox.SelectedItem is not AssignedDrawings.FileItem file) return;
        _drawingPairCheck?.Cancel(); _drawingDwg = null; _drawingDwgAvailable = false; _drawingFingerprint = ""; DrawingPairStatusText.Text = "";
        _drawingPath = file.Path; _drawingBytes = null; _drawingPage = 0; _drawingPageCount = 0;
        DrawingFileText.Text = file.Name; DrawingFileText.ToolTip = file.Path;
        if (SelectedGate is { } gate)
        {
            var eligible = _systems.Where(unit => unit.GateId == gate.Id && (file.Resource?.AppliesTo(unit.Id) ?? true));
            var scope = file.GuideUnitIds ?? file.Resource?.FileScope(Path.GetFileName(file.Path)) ?? [];
            DrawingCoverageText.Text = "Covers " + DrawingUnitsWindow.UnitNames(eligible, scope);
        }
        if (_drawingListing is null) UpdateDrawingUploadButtons(UploadDrawingButton.IsEnabled);
        await RenderDrawingPageAsync();
        if (!_isClosed && ReferenceEquals(DrawingFileBox.SelectedItem, file)) CheckDrawingPair(file);
    }
    private async Task RenderDrawingPageAsync()
    {
        _drawingRendering?.Cancel();
        var source = new CancellationTokenSource(); _drawingRendering = source; var token = source.Token;
        string path = _drawingPath; int page = _drawingPage;
        _drawingOrientationCancellation?.Cancel(); DrawingImage.Source = null; DrawingMessage.Text = "Loading drawing…"; DrawingMessage.Visibility = Visibility.Visible; UpdateDrawingControls();
        try
        {
            if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = _drawingBytes ?? await DrawingPreviewService.ReadAsync(path, token);
                token.ThrowIfCancellationRequested(); _drawingBytes = bytes;
                var result = await DrawingPreviewService.RenderPdfAsync(bytes, page, token);
                if (_isClosed || token.IsCancellationRequested) return;
                DisplayDrawingImage(result.Image, path, page, bytes); _drawingPageCount = result.PageCount;
                DrawingPageText.Text = result.PageCount > 1 ? $"Page {page + 1} of {result.PageCount}" : "";
            }
            else if (new[] { ".dwg", ".dxf" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                DrawingMessage.Text = "This CAD drawing needs its installed viewer. Use the download link, or assign a PDF for an inline preview.";
                DrawingPageText.Text = ""; return;
            }
            else
            {
                var result = await IdentificationPhotoLoader.LoadAsync(path, 2000, token);
                if (_isClosed || token.IsCancellationRequested) return;
                if (result.Source is not null) DisplayDrawingImage(result.Source, path, page, null);
                _drawingPageCount = 1; DrawingPageText.Text = "";
                if (result.Source is null) { DrawingMessage.Text = "This drawing image is unavailable or could not be displayed. Use the download link to inspect the file."; return; }
            }
            DrawingMessage.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_isClosed && !token.IsCancellationRequested)
            {
                DrawingMessage.Text = "Drawing preview unavailable. " + error.Message + " Use the download link or check the file assignment.";
                DrawingPageText.Text = "";
            }
        }
        finally
        {
            if (ReferenceEquals(_drawingRendering, source)) { _drawingRendering = null; if (!_isClosed) UpdateDrawingControls(); }
            source.Dispose();
        }
    }
    private void ShowDrawingOrParts(bool drawing)
    {
        DrawingPreviewSurface.Visibility = drawing ? Visibility.Visible : Visibility.Collapsed;
        NoDrawingPartsPanel.Visibility = drawing ? Visibility.Collapsed : Visibility.Visible;
        if (!drawing) RefreshReplacementParts();
    }
    private void UpdateDrawingControls()
    {
        bool idle = _drawingRendering is null;
        DrawingPageControls.Visibility = _drawingPageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        PreviousDrawingPageButton.IsEnabled = idle && _drawingPage > 0;
        NextDrawingPageButton.IsEnabled = idle && _drawingPage + 1 < _drawingPageCount;
        DownloadPdfLink.IsEnabled = Path.GetExtension(_drawingPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        DownloadDwgLink.IsEnabled = _drawingDwgAvailable;
    }
    private void DisplayDrawingImage(BitmapSource image, string path, int page, byte[]? pdf)
    {
        _drawingOrientationCancellation?.Cancel(); var source = new CancellationTokenSource(); _drawingOrientationCancellation = source;
        DrawingImage.Source = DrawingOrientationService.Rotate(image, image.PixelHeight > image.PixelWidth ? 90 : 0);
        DrawingMessage.Visibility = Visibility.Collapsed; DrawingOrientationText.Text = "Detecting reading direction…";
        _ = ApplyAutomaticOrientationAsync(image, pdf, page, path, source);
    }
    private async Task ApplyAutomaticOrientationAsync(BitmapSource image, byte[]? pdf, int page, string path, CancellationTokenSource source)
    {
        try
        {
            var result = await DrawingOrientationService.DetectAsync(image, pdf, page, _data.DrawingOrientationCache, source.Token);
            if (_isClosed || source.IsCancellationRequested || _drawingPath != path || _drawingPage != page) return;
            DrawingImage.Source = DrawingOrientationService.Rotate(image, result.Angle);
            DrawingOrientationText.Text = result.Confidence > 0 ? "Reading direction detected automatically" : "Landscape preview; not enough readable text to determine reading direction.";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_isClosed && !source.IsCancellationRequested && _drawingPath == path && _drawingPage == page) DrawingOrientationText.Text = "Landscape preview; text direction could not be determined."; }
        finally { if (ReferenceEquals(_drawingOrientationCancellation, source)) _drawingOrientationCancellation = null; source.Dispose(); }
    }
    private void DrawingPreview_Click(object sender, MouseButtonEventArgs e)
    {
        if (DrawingImage.Source is null) return;
        new DrawingZoomWindow(DrawingFileText.Text + (_drawingPageCount > 1 ? " • " + DrawingPageText.Text : ""), DrawingImage) { Owner = this }.ShowDialog(); e.Handled = true;
    }
    private async void DownloadDrawing_Click(object sender, RoutedEventArgs e)
    {
        string? source = ReferenceEquals(sender, DownloadDwgLink) ? _drawingDwg : _drawingPath;
        if (string.IsNullOrWhiteSpace(source)) return;
        var dialog = new SaveFileDialog { Title = "Save a copy of the drawing", FileName = Path.GetFileName(source), Filter = Path.GetExtension(source).Equals(".dwg", StringComparison.OrdinalIgnoreCase) ? "DWG drawing|*.dwg" : "PDF drawing|*.pdf", AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (Path.GetFullPath(source).Equals(Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase)) { DrawingAssignmentText.Text = "This is already the source file's location."; return; }
            await Task.Run(() => DataService.AtomicCopy(source, dialog.FileName, true));
            if (!_isClosed) DrawingAssignmentText.Text = "Drawing copy saved.";
        }
        catch (Exception error) { if (!_isClosed) DrawingAssignmentText.Text = "The drawing copy could not be saved: " + error.Message; }
    }

    private async void PreviousDrawingPage_Click(object sender, RoutedEventArgs e) { if (_drawingRendering is not null || _drawingPage == 0) return; _drawingPage--; await RenderDrawingPageAsync(); }
    private async void NextDrawingPage_Click(object sender, RoutedEventArgs e) { if (_drawingRendering is not null || _drawingPage + 1 >= _drawingPageCount) return; _drawingPage++; await RenderDrawingPageAsync(); }
    private async Task RefreshDrawingPublishStatusAsync()
    {
        if (_isClosed || SelectedUnit is not { } unit || _drawingPublicationUnits.Count == 0 ||
            !DrawingPublishStatus.Text.Contains(OperatorDriveRunner.QueuedMessage, StringComparison.Ordinal)) return;
        string previous = DrawingPublishStatus.Text;
        var affected = _drawingPublicationUnits;
        var status = await OperatorDriveRunner.DrawingStatusAsync(_data, affected);
        if (!_isClosed && SelectedUnit?.Id == unit.Id && DrawingPublishStatus.Text == previous &&
            ReferenceEquals(affected, _drawingPublicationUnits) && status is not null)
        {
            DrawingPublishStatus.Text = previous.Split(OperatorDriveRunner.QueuedMessage, 2)[0] +
                (status == OperatorDriveRunner.PublishedMessage ? status : OperatorDriveRunner.QueuedMessage + " " + status);
        }
    }

    private void UpdateDrawingUploadButtons(bool enabled)
    {
        UploadDrawingButton.IsEnabled = enabled;
        bool isGuide = DrawingFileBox.SelectedItem is AssignedDrawings.FileItem { GuideUnitIds: not null } && !_data.IsHomePreview;
        RemoveGuidePdfButton.Visibility = isGuide ? Visibility.Visible : Visibility.Collapsed;
        RemoveGuidePdfButton.IsEnabled = enabled && isGuide;
    }

    private async void RemoveGuidePdf_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGate is not { } gate || SelectedUnit is not { } unit || InlineBusy || _drawingListing is not null ||
            DrawingFileBox.SelectedItem is not AssignedDrawings.FileItem { GuideUnitIds: { } covered } file) return;
        string name = Path.GetFileName(file.Path);
        string others = covered.Count > 1 ? " Other units on this asset keep it." : " It is the last unit using this PDF, so the file moves to Removed Guide PDFs on the shared drive.";
        if (MessageBox.Show(this, $"Remove {name} from {unit.UnitDisplay}'s guide?{others}\n\nThe unit guide on Google Drive is rebuilt without it at the next publish.",
            "Remove guide PDF", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        UpdateDrawingUploadButtons(false);
        try
        {
            await OperatorDriveRunner.RemoveGuidePdfAsync(_data, gate.Id, unit.Id, file.Path);
            if (_isClosed) return;
            LoadDrawings(gate, unit);
            DrawingPublishStatus.Text = $"Removed {name} from this unit.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            if (!_isClosed) { MessageBox.Show(this, error.Message, "Guide PDF was not removed", MessageBoxButton.OK, MessageBoxImage.Warning); UpdateDrawingUploadButtons(true); }
        }
    }

    // One upload button: when a replaceable drawing is showing, ask whether this is its new revision.
    private async void UploadDrawing_Click(object sender, RoutedEventArgs e)
    {
        bool canReplace = DrawingFileBox.SelectedItem is AssignedDrawings.FileItem { Resource: { StorageMode: "Asset", AllowUserUpload: true } };
        if (!canReplace) { await UploadDrawingAsync(replace: false); return; }
        string shown = Path.GetFileName(_drawingPath);
        int choice = ChoiceDialog.Ask(this, "Upload drawing", "What are you uploading?",
            ($"A new revision of {shown}", "Replaces the PDF and DWG on every unit this drawing covers. The old pair is archived."),
            ("A different drawing", "Adds a separate PDF and DWG pair. You choose which units it covers."));
        if (choice >= 0) await UploadDrawingAsync(replace: choice == 0);
    }

    private async Task UploadDrawingAsync(bool replace)
    {
        if (SelectedGate is not { } gate || SelectedUnit is not { } unit || InlineBusy || _drawingListing is not null) return;
        UpdateDrawingUploadButtons(false);
        try
        {
            var selected = DrawingFileBox.SelectedItem as AssignedDrawings.FileItem;
            if (replace && selected?.Resource is not { StorageMode: "Asset" }) return;
            var resources = await Task.Run(_data.LoadResources);
            if (_isClosed || SelectedUnit?.Id != unit.Id) return;
            var choices = _data.ResourcesFor(gate, unit, resources).Where(AssignedDrawings.IsDrawing).ToList();
            var resource = choices.FirstOrDefault(r => r.Id == selected?.Resource?.Id && r.StorageMode == "Asset");
            if (replace && resource is null)
                throw new InvalidOperationException("Refresh the selected drawing before replacing it.");
            resource ??= choices.FirstOrDefault(r => r.StorageMode == "Asset");
            if (resource is null && !string.IsNullOrWhiteSpace(gate.AssetFolder))
                resource = AssetFileLayout.Create(gate, "Electrical Drawings").ForUnit(unit.Id);
            if (resource is null || resource.StorageMode != "Asset")
            {
                MessageBox.Show(this, "Organize this library’s asset folders in Plant setup before updating older drawing collections.");
                return;
            }

            var upload = new ResourceWindow(_data, gate, unit, resource) { Owner = this };
            var content = (FrameworkElement)upload.Content;
            upload.Content = null;
            HostInline(upload, content);
            try
            {
                var receipt = await upload.UploadDrawingPairAsync(replace ? selected!.Path : null);
                if (!_isClosed && SelectedUnit?.Id == unit.Id && receipt is not null)
                {
                    _drawingPublicationUnits = receipt.UnitIds;
                    DrawingPublishStatus.Text = receipt.Status;
                    LoadDrawings(gate, unit);
                    _ = RefreshSearchIndexAsync();
                }
            }
            finally
            {
                CloseInlineTool();
            }
        }
        catch (Exception error)
        {
            if (!_isClosed) MessageBox.Show(this, error.Message, "Upload drawing pair", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (!_isClosed && _drawingListing is null && SelectedUnit?.Id == unit.Id) UpdateDrawingUploadButtons(true);
        }
    }
}
