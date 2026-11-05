using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;
namespace SafetyGateSupport;

public partial class MainWindow
{
    private readonly Func<string, CancellationToken, Task<StockroomService.Snapshot>> _stockroomReader;
    private StockroomService.Snapshot? _stockroomSnapshot;
    private CancellationTokenSource? _stockroomRefresh, _partPhotoLoad;
    private string _stockroomRequestedUrl = "", _stockroomMessage = "Current quantity has not been checked.";
    private bool _partPhotoSaving;
    private readonly Dictionary<Image, CancellationTokenSource> _partThumbnailLoads = new();
    private StockroomPlant? SelectedStockroomPlant => StockroomPlantBox.SelectedItem as StockroomPlant;
    private void InitializeStockroomPlants()
    {
        RailPlantBox.ItemsSource = StockroomPlant.All;
        StockroomPlantBox.ItemsSource = StockroomPlant.All;
        StockroomPlantBox.SelectedItem = StockroomPlant.Find(_data.Settings.StockroomPlantId);
        UpdatePartQuantity();
    }
    private async void StockroomPlant_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_data is null) return;
        _stockroomRefresh?.Cancel(); _stockroomRefresh = null;
        _stockroomSnapshot = null; _stockroomRequestedUrl = "";
        var plant = SelectedStockroomPlant;
        RailPlantBox.SelectedItem = plant;
        _data.Settings.StockroomPlantId = plant?.Id ?? "";
        try { _data.SaveLocalSettings(); }
        catch (Exception error) { MessageBox.Show(this, "The plant choice could not be saved on this computer: " + error.Message); }
        _stockroomMessage = plant is null ? "Choose a stockroom plant to check current quantities." : "Selected stockroom: " + plant.Name;
        UpdatePartQuantity();
        if (_sharedDataReady && plant is not null) { _stockroomRequestedUrl = plant.Url; await RefreshStockroomAsync(); }
    }
    private async Task RefreshStockroomAsync()
    {
        if (_stockroomRefresh is not null || _isClosed || _data.IsHomePreview) return;
        var plant = SelectedStockroomPlant;
        if (plant is null) { _stockroomMessage = "Choose a stockroom plant to check current quantities."; UpdatePartQuantity(); StockroomPlantBox.Focus(); return; }
        var source = new CancellationTokenSource(); _stockroomRefresh = source;
        _stockroomSnapshot = null; _stockroomMessage = $"Checking {plant.Name} stockroom in the background…"; RefreshStockroomButton.IsEnabled = false; UpdatePartQuantity();
        try
        {
            var snapshot = await _stockroomReader(plant.Url, source.Token);
            if (_isClosed || source.IsCancellationRequested || !ReferenceEquals(_stockroomRefresh, source)) return;
            _stockroomSnapshot = snapshot;
            _stockroomMessage = snapshot.Rows.Count == 0 ? $"{plant.Name}: the Safe2Cross search returned no readable stock rows. Open the stockroom to check its search results." : $"{plant.Name} stockroom checked {snapshot.CheckedUtc.LocalDateTime:g}.";
        }
        catch (OperationCanceledException) { if (!_isClosed && ReferenceEquals(_stockroomRefresh, source)) { _stockroomSnapshot = null; _stockroomMessage = $"{plant.Name} check timed out or was canceled. Reported snapshot retained."; } }
        catch (Exception error) { if (!_isClosed && ReferenceEquals(_stockroomRefresh, source)) { _stockroomSnapshot = null; _stockroomMessage = $"{plant.Name} stockroom unavailable: " + error.Message + " Reported snapshot retained."; } }
        finally
        {
            if (ReferenceEquals(_stockroomRefresh, source)) { _stockroomRefresh = null; if (!_isClosed) { RefreshStockroomButton.IsEnabled = true; UpdatePartQuantity(); } }
            source.Dispose();
        }
    }
    private void UpdatePartQuantity()
    {
        var plant = SelectedStockroomPlant;
        var p = SelectedPart; var row = p is null || _stockroomSnapshot is null ? null : StockroomService.Find(_stockroomSnapshot, p, plantScoped: true);
        PartCurrentQuantityText.Text = row is null ? "Current quantity unavailable" : "Current quantity: " + PartRecord.FormatQuantity(row.Quantity);
        PartStockroomStatusText.Text = row is not null ? $"{plant?.Name} • {row.QuantityLabel} • bin: {(row.Location.Length > 0 ? row.Location : "not provided")} • checked {_stockroomSnapshot!.CheckedUtc.LocalDateTime:g}" : _stockroomSnapshot?.Rows.Count > 0 && p is not null ? $"{plant?.Name}: no unique stockroom quantity matched this stock number." : plant is null ? "Choose a plant above. Reported quantities and locations are the original supplied snapshot, not current inventory for this plant." : _stockroomMessage;
        StockroomPlantHint.Text = plant is null ? "Choose a plant for current stock levels" : plant.Url + " • saved on this PC";
        foreach (var part in _parts) { var live = _stockroomSnapshot is null ? null : StockroomService.Find(_stockroomSnapshot, part, plantScoped: true); part.SetInventory(live?.Quantity, live?.Location ?? ""); }
        PartAvailableText.Text = p?.AvailableDisplay ?? "Available: unknown"; PartAvailableText.Foreground = (Brush)new BrushConverter().ConvertFromString(p?.AvailableColor ?? "#617585")!;
        RailStockStatus.Text = plant is null ? "Select your plant to check live availability. Supplied quantities are snapshots only." : _stockroomMessage + " Updates every 2 minutes while open.";
        StoreroomResultText.Text = plant is null ? "Choose a stockroom plant. No plant has been checked." : _stockroomMessage;
    }
    private async void LoadPartPhoto(PartRecord part)
    {
        _partPhotoLoad?.Cancel(); var source = new CancellationTokenSource(); _partPhotoLoad = source;
        PartPhotoImage.Source = null; EnlargePartPhotoButton.IsEnabled = false; PartPhotoStatus.Text = "";
        PartPhotoMessage.Visibility = Visibility.Visible;
        PartPhotoMessage.Text = string.IsNullOrWhiteSpace(part.PhotoPath) ? "No photo yet. Upload a photo to help identify this part." : "Loading part photo…";
        try
        {
            if (string.IsNullOrWhiteSpace(part.PhotoPath)) return;
            var result = await IdentificationPhotoLoader.LoadAsync(_data.Resolve(part.PhotoPath), source.Token);
            if (_isClosed || source.IsCancellationRequested || SelectedPart?.Id != part.Id) return;
            PartPhotoImage.Source = result.Source; EnlargePartPhotoButton.IsEnabled = result.Source is not null;
            PartPhotoMessage.Visibility = result.Source is null ? Visibility.Visible : Visibility.Collapsed;
            if (result.Source is null) PartPhotoMessage.Text = "Photo unavailable. Check shared-drive access or upload a replacement.";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_isClosed && !source.IsCancellationRequested && SelectedPart?.Id == part.Id) PartPhotoMessage.Text = "Photo unavailable. Check the image path or upload a replacement."; }
        finally { if (ReferenceEquals(_partPhotoLoad, source)) _partPhotoLoad = null; source.Dispose(); }
    }
    private async void UploadPartPhoto_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPart is not { } part || _partPhotoSaving) return;
        var dialog = new OpenFileDialog { Title = "Choose a photo of " + part.Description, Filter = "Part photos|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff" };
        if (dialog.ShowDialog(this) != true) return;
        await SavePartPhotoAsync(part, dialog.FileName);
    }
    private async Task SavePartPhotoAsync(PartRecord part, string sourceFile)
    {
        _partPhotoSaving = true; UploadPartPhotoButton.IsEnabled = false; PartPhotoStatus.Text = "Checking and saving photo…";
        try
        {
            if (new FileInfo(sourceFile).Length > 64 * 1024 * 1024) throw new InvalidDataException("Choose a photo smaller than 64 MB.");
            var preview = await IdentificationPhotoLoader.LoadAsync(sourceFile);
            if (preview.Source is null) throw new InvalidDataException("This file could not be read as a photo. Choose a different image.");
            var saved = await Task.Run(() => _data.SavePartPhoto(part.Id, sourceFile));
            if (_isClosed) return;
            int index = _parts.FindIndex(p => p.Id == saved.Id); if (index >= 0) _parts[index] = saved;
            RefreshParts(); if (SelectedPart?.Id == saved.Id) PartPhotoStatus.Text = "Photo saved in the shared parts library.";
        }
        catch (Exception error) { if (!_isClosed) PartPhotoStatus.Text = "Photo was not saved: " + error.Message; }
        finally { _partPhotoSaving = false; if (!_isClosed) UploadPartPhotoButton.IsEnabled = SelectedPart is not null; }
    }
    private void EnlargePartPhoto_Click(object sender, RoutedEventArgs e)
    {
        if (PartPhotoImage.Source is null) return;
        var image = new Image { Source = PartPhotoImage.Source, Stretch = Stretch.Uniform, Margin = new Thickness(16) };
        var close = new Button { Content = "Close · Esc", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new DockPanel(); DockPanel.SetDock(close, Dock.Top); panel.Children.Add(close); panel.Children.Add(image);
        var window = new Window { Owner = this, Title = SelectedPart?.Display ?? "Part photo", Content = panel, WindowState = WindowState.Maximized, Background = Brushes.White };
        close.Click += (_, _) => window.Close(); window.ShowDialog();
    }
    private async void PartThumbnail_Loaded(object sender, RoutedEventArgs e) => await LoadPartThumbnailAsync((Image)sender);
    private async void PartThumbnail_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) { if (((Image)sender).IsLoaded) await LoadPartThumbnailAsync((Image)sender); }
    private void PartThumbnail_Unloaded(object sender, RoutedEventArgs e) { if (_partThumbnailLoads.Remove((Image)sender, out var source)) source.Cancel(); }
    private async Task LoadPartThumbnailAsync(Image image)
    {
        if (_partThumbnailLoads.Remove(image, out var prior)) prior.Cancel(); image.Source = null;
        if (_isClosed || image.DataContext is not PartRecord part || string.IsNullOrWhiteSpace(part.PhotoPath)) return;
        var source = new CancellationTokenSource(); _partThumbnailLoads[image] = source;
        try { var result = await IdentificationPhotoLoader.LoadAsync(_data.Resolve(part.PhotoPath), 192, source.Token); if (!_isClosed && !source.IsCancellationRequested && ReferenceEquals(image.DataContext, part)) image.Source = result.Source; }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Keep the placeholder for invalid or unavailable image paths. */ }
        finally { if (_partThumbnailLoads.TryGetValue(image, out var current) && ReferenceEquals(current, source)) _partThumbnailLoads.Remove(image); source.Dispose(); }
    }
    private void CancelPartPhotos()
    {
        _partPhotoLoad?.Cancel(); foreach (var source in _partThumbnailLoads.Values) source.Cancel(); _partThumbnailLoads.Clear();
    }
}
