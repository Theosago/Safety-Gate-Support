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
            DrawingPairStatusText.Text = status.Message; DrawingPairStatusText.Foreground = new SolidColorBrush(status.NeedsAttention ? Color.FromRgb(150, 88, 19) : Color.FromRgb(65, 100, 115));
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

    private void ClearDrawings()
    {
        _drawingListing?.Cancel(); _drawingListing = null;
        _drawingPairCheck?.Cancel(); _drawingPairCheck = null; _drawingDwg = null; _drawingDwgAvailable = false; _drawingFingerprint = ""; DrawingPairStatusText.Text = "";
        _drawingRendering?.Cancel(); _drawingRendering = null;
        _settingDrawingFiles = true; DrawingFileBox.ItemsSource = null; _settingDrawingFiles = false;
        _drawingOrientationCancellation?.Cancel(); DrawingOrientationText.Text = "";
        _drawingPath = ""; _drawingBytes = null; _drawingPage = _drawingPageCount = 0;
        DrawingImage.Source = null; DrawingFileText.Text = ""; DrawingPageText.Text = ""; DrawingAssignmentText.Text = "";
        DrawingMessage.Text = "Select a unit to see its assigned electrical drawing."; DrawingMessage.Visibility = Visibility.Visible;
        DrawingFileBox.Visibility = Visibility.Collapsed; UpdateDrawingControls();
    }

    private async void LoadDrawings(SafetyGate gate, SafetySystem unit)
    {
        ClearDrawings();
        var source = new CancellationTokenSource(); _drawingListing = source; var token = source.Token;
        DrawingMessage.Text = "Loading this unit’s assigned electrical drawings…";
        try
        {
            var resources = await Task.Run(_data.LoadResources, token);
            var listing = await Task.Run(() => AssignedDrawings.List(_data, gate, unit, resources, token), token);
            if (_isClosed || token.IsCancellationRequested) return;
            DrawingFileBox.Visibility = listing.Files.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _settingDrawingFiles = true; DrawingFileBox.ItemsSource = listing.Files; _settingDrawingFiles = false;
            if (listing.Files.Count == 0)
                DrawingMessage.Text = listing.Unavailable.Count > 0 ? "The assigned drawing folder is unavailable. Check shared-drive access. Use Electrical Schematics to review the assignment." : "No current electrical drawing is assigned to this unit. Use Electrical Schematics to add or review its drawing.";
            else DrawingFileBox.SelectedIndex = 0;
            DrawingAssignmentText.Text = listing.Unavailable.Count > 0 ? "Unavailable drawing folders: " + string.Join(", ", listing.Unavailable) : listing.Files.Count > 1 ? $"{listing.Files.Count} assigned drawings. Select another above if needed." : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_isClosed && !token.IsCancellationRequested) DrawingMessage.Text = "Could not load the assigned drawing: " + error.Message; }
        finally { if (ReferenceEquals(_drawingListing, source)) _drawingListing = null; source.Dispose(); }
    }

    private async void DrawingFile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settingDrawingFiles || DrawingFileBox.SelectedItem is not AssignedDrawings.FileItem file) return;
        _drawingPairCheck?.Cancel(); _drawingDwg = null; _drawingDwgAvailable = false; _drawingFingerprint = ""; DrawingPairStatusText.Text = "";
        _drawingPath = file.Path; _drawingBytes = null; _drawingPage = 0; _drawingPageCount = 0;
        DrawingFileText.Text = file.Name; DrawingFileText.ToolTip = file.Path;
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
    private async void UpdateDrawing_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGate is not { } gate || SelectedUnit is not { } unit) return;
        var resources = await Task.Run(_data.LoadResources);
        var choices = _data.ResourcesFor(gate, unit, resources).Where(AssignedDrawings.IsDrawing).ToList();
        var resource = choices.FirstOrDefault(r => Path.GetDirectoryName(_drawingPath)?.Equals(_data.ResourceCurrentFolder(r, gate), StringComparison.OrdinalIgnoreCase) == true) ?? choices.FirstOrDefault();
        if (resource is null) { MessageBox.Show(this, "Add this asset’s drawing collection in Setup or link a drawing from Support library first."); return; }
        ShowInlineResource("Electrical Drawings");
        if (!_isClosed && SelectedUnit?.Id == unit.Id) LoadDrawings(gate, unit);
    }

}
