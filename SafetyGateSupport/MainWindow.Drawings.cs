using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class MainWindow
{
    private CancellationTokenSource? _drawingListing;
    private CancellationTokenSource? _drawingRendering;
    private string _drawingPath = "";
    private byte[]? _drawingBytes;
    private int _drawingPage, _drawingPageCount;
    private bool _settingDrawingFiles;

    private void ClearDrawings()
    {
        _drawingListing?.Cancel(); _drawingListing = null;
        _drawingRendering?.Cancel(); _drawingRendering = null;
        _settingDrawingFiles = true; DrawingFileBox.ItemsSource = null; _settingDrawingFiles = false;
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
            var resources = _resources.ToArray();
            var listing = await Task.Run(() => AssignedDrawings.List(_data, gate, unit, resources, token), token);
            if (_isClosed || token.IsCancellationRequested) return;
            DrawingFileBox.Visibility = listing.Files.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _settingDrawingFiles = true; DrawingFileBox.ItemsSource = listing.Files; _settingDrawingFiles = false;
            if (listing.Files.Count == 0)
                DrawingMessage.Text = listing.Unavailable.Count > 0 ? "The assigned drawing folder is unavailable. Check shared-drive access. Use Files & drawings to review the assignment." : "No current electrical drawing is assigned to this unit. Use Files & drawings to add or review its drawing.";
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
        _drawingPath = file.Path; _drawingBytes = null; _drawingPage = 0; _drawingPageCount = 0;
        DrawingFileText.Text = file.Name; DrawingFileText.ToolTip = file.Path;
        await RenderDrawingPageAsync();
    }
    private async Task RenderDrawingPageAsync()
    {
        _drawingRendering?.Cancel();
        var source = new CancellationTokenSource(); _drawingRendering = source; var token = source.Token;
        string path = _drawingPath; int page = _drawingPage;
        DrawingImage.Source = null; DrawingMessage.Text = "Loading drawing…"; DrawingMessage.Visibility = Visibility.Visible; UpdateDrawingControls();
        try
        {
            if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = _drawingBytes ?? await DrawingPreviewService.ReadAsync(path, token);
                token.ThrowIfCancellationRequested(); _drawingBytes = bytes;
                var result = await DrawingPreviewService.RenderPdfAsync(bytes, page, token);
                if (_isClosed || token.IsCancellationRequested) return;
                DrawingImage.Source = result.Image; _drawingPageCount = result.PageCount;
                DrawingPageText.Text = $"Page {page + 1} of {result.PageCount}";
            }
            else if (new[] { ".dwg", ".dxf" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                DrawingMessage.Text = "This CAD drawing needs its installed viewer. Use Open original, or assign a PDF for an inline preview.";
                DrawingPageText.Text = "CAD drawing"; return;
            }
            else
            {
                var result = await IdentificationPhotoLoader.LoadAsync(path, 2000, token);
                if (_isClosed || token.IsCancellationRequested) return;
                DrawingImage.Source = result.Source; _drawingPageCount = 1; DrawingPageText.Text = "Image drawing";
                if (result.Source is null) { DrawingMessage.Text = "This drawing image is unavailable or could not be displayed. Use Open original to inspect the file."; return; }
            }
            DrawingMessage.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_isClosed && !token.IsCancellationRequested)
            {
                DrawingMessage.Text = "Drawing preview unavailable. " + error.Message + " Use Open original or check the file assignment.";
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
        PreviousDrawingPageButton.IsEnabled = idle && _drawingPage > 0;
        NextDrawingPageButton.IsEnabled = idle && _drawingPage + 1 < _drawingPageCount;
        OpenDrawingButton.IsEnabled = _drawingPath.Length > 0;
        EnlargeDrawingButton.IsEnabled = DrawingImage.Source is not null;
    }
    private async void PreviousDrawingPage_Click(object sender, RoutedEventArgs e) { if (_drawingRendering is not null || _drawingPage == 0) return; _drawingPage--; await RenderDrawingPageAsync(); }
    private async void NextDrawingPage_Click(object sender, RoutedEventArgs e) { if (_drawingRendering is not null || _drawingPage + 1 >= _drawingPageCount) return; _drawingPage++; await RenderDrawingPageAsync(); }
    private void OpenDrawing_Click(object sender, RoutedEventArgs e) { if (_drawingPath.Length > 0) FileLauncherService.Open(_drawingPath); }
    private void EnlargeDrawing_Click(object sender, RoutedEventArgs e)
    {
        if (DrawingImage.Source is null) return;
        var image = new Image { Source = DrawingImage.Source, Stretch = Stretch.Uniform, Margin = new Thickness(12) };
        var close = new Button { Content = "Close · Esc", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new DockPanel(); DockPanel.SetDock(close, Dock.Top); panel.Children.Add(close); panel.Children.Add(image);
        var window = new Window { Owner = this, Title = DrawingFileText.Text + " • " + DrawingPageText.Text, WindowState = WindowState.Maximized, Content = panel, Background = Brushes.White };
        close.Click += (_, _) => window.Close(); window.ShowDialog();
    }
}
