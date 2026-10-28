using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class MainWindow
{
    private const int UnitsPerPage = 12;
    private string _galleryArea = "";
    private int _galleryPage;
    private CancellationTokenSource? _galleryCancellation;
    private readonly System.Windows.Threading.DispatcherTimer _unitSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };

    public sealed class UnitCard : INotifyPropertyChanged
    {
        public required SafetyGate Gate { get; init; }
        public required SafetySystem Unit { get; init; }
        public string Title => $"{Gate.AssetDisplay} • {Unit.UnitDisplay}";
        public string Location => string.Join(" • ", new[] { Gate.Category, Gate.Location, Gate.Manufacturer, Unit.DisplayName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        private BitmapSource? _photo;
        public BitmapSource? Photo { get => _photo; set { _photo = value; PropertyChanged?.Invoke(this, new(nameof(Photo))); } }
        private string _message = "Loading identification photo…";
        public string PhotoMessage { get => _message; set { _message = value; PropertyChanged?.Invoke(this, new(nameof(PhotoMessage))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private void InitializeUnitGallery()
    {
        _unitSearchTimer.Tick += (_, _) => { _unitSearchTimer.Stop(); _galleryPage = 0; RefreshUnitGallery(); };
    }
    private void UnitSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_unitSearchTimer is null || !_sharedDataReady) return;
        _unitSearchTimer.Stop(); _unitSearchTimer.Start();
    }
    private void UnitSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _unitSearchTimer.Stop(); _galleryPage = 0; RefreshUnitGallery();
        // Enter filters; it never selects a potentially ambiguous unit automatically.
    }
    private void AllAreas_Click(object sender, RoutedEventArgs e) => SelectGalleryArea("");
    private void SelectGalleryArea(string area) { _galleryArea = area; _galleryPage = 0; RefreshUnitGallery(); }
    private void PreviousUnits_Click(object sender, RoutedEventArgs e) { _galleryPage = Math.Max(0, _galleryPage - 1); RefreshUnitGallery(); HomeView.ScrollToTop(); }
    private void NextUnits_Click(object sender, RoutedEventArgs e) { _galleryPage++; RefreshUnitGallery(); HomeView.ScrollToTop(); }
    private void UnitCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SafetySystem unit }) { NavigateToSystem(unit); UnitWorkspaceScroll.ScrollToTop(); }
    }

    private void RefreshUnitGallery()
    {
        if (UnitGallery is null || _isClosed) return;
        _galleryCancellation?.Cancel();
        var query = UnitSearchBox.Text.Trim();
        var cards = _systems.Select(unit => (Unit: unit, Gate: GateFor(unit))).Where(x => x.Gate is not null)
            .Select(x => new UnitCard { Gate = x.Gate!, Unit = x.Unit })
            .Where(c => (_galleryArea.Length == 0 || c.Gate.Category.Equals(_galleryArea, StringComparison.OrdinalIgnoreCase)) &&
                query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term => $"{c.Title} {c.Location} {c.Gate.Manufacturer} {c.Gate.Model} {c.Gate.Keywords}".Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => AssetSortKey(c.Gate.AssetNumber)).ThenBy(c => c.Gate.AssetNumber, StringComparer.OrdinalIgnoreCase).ThenBy(c => UnitSortKey(c.Unit)).ThenBy(c => c.Unit.Id).ToList();
        int pages = Math.Max(1, (cards.Count + UnitsPerPage - 1) / UnitsPerPage);
        _galleryPage = Math.Clamp(_galleryPage, 0, pages - 1);
        var page = cards.Skip(_galleryPage * UnitsPerPage).Take(UnitsPerPage).ToList();
        UnitGallery.ItemsSource = page;
        GalleryEmptyText.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GalleryStatusText.Text = !_sharedDataReady ? "Shared data is unavailable. Reconnect the shared folder to see your units." : $"{cards.Count} unit(s) • {(_galleryArea.Length == 0 ? "All areas" : _galleryArea)} • Select the matching photo and unit number.";
        GalleryPageText.Text = $"Page {_galleryPage + 1} of {pages}";
        PreviousUnitsButton.IsEnabled = _galleryPage > 0; NextUnitsButton.IsEnabled = _galleryPage + 1 < pages;
        AllAreasButton.FontWeight = _galleryArea.Length == 0 ? FontWeights.Bold : FontWeights.Normal;
        foreach (var button in HomeCategoryPanel.Children.OfType<Button>()) button.FontWeight = Equals(button.Tag, _galleryArea) ? FontWeights.Bold : FontWeights.Normal;
        var cancellation = new CancellationTokenSource(); _galleryCancellation = cancellation;
        _ = LoadGalleryPhotosAsync(page, cancellation);
    }

    private async Task LoadGalleryPhotosAsync(List<UnitCard> cards, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        using var limit = new SemaphoreSlim(4);
        try
        {
            await Task.WhenAll(cards.Select(async card =>
            {
                await limit.WaitAsync(token);
                try
                {
                    if (string.IsNullOrWhiteSpace(card.Unit.PrimaryPhoto)) { card.PhotoMessage = "No identification photo saved. Confirm the asset and unit number."; return; }
                    var result = await IdentificationPhotoLoader.LoadAsync(_data.Resolve(card.Unit.PrimaryPhoto), 640, token);
                    if (_isClosed || token.IsCancellationRequested) return;
                    card.Photo = result.Source;
                    card.PhotoMessage = result.Status == IdentificationPhotoStatus.Missing ? "Saved photo unavailable. Check the shared drive or photo assignment." : "Photo could not be previewed. Confirm the asset and unit number.";
                }
                finally { limit.Release(); }
            }));
        }
        catch (OperationCanceledException) { }
        catch { if (!_isClosed && !token.IsCancellationRequested) GalleryStatusText.Text += " Some photos could not be loaded; units remain selectable."; }
        finally { if (ReferenceEquals(_galleryCancellation, cancellation)) _galleryCancellation = null; cancellation.Dispose(); }
    }

    private void EnlargePhoto_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGate is { } gate && SelectedUnit is { } unit)
            new UnitPhotoWindow(_data, gate, unit) { Owner = this }.ShowDialog();
    }
}
