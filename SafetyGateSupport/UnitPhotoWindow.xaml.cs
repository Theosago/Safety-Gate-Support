using System.IO;
using System.Windows;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class UnitPhotoWindow : Window
{
    private readonly DataService _data;
    private readonly SafetySystem _unit;
    private List<string> _photos = [];
    private int _index;
    private bool _closed;
    private string _folderWarning = "";
    private CancellationTokenSource? _load;
    public UnitPhotoWindow(DataService data, SafetyGate gate, SafetySystem unit)
    {
        InitializeComponent(); _data = data; _unit = unit;
        UnitTitleText.Text = $"{gate.AssetDisplay} • {unit.UnitDisplay}";
        LocationText.Text = string.Join(" • ", new[] { gate.Category, gate.Location }.Where(s => !string.IsNullOrWhiteSpace(s)));
        Loaded += async (_, _) => await DiscoverAsync();
        Closed += (_, _) => { _closed = true; _load?.Cancel(); };
    }
    private async Task DiscoverAsync()
    {
        try
        {
            var result = await Task.Run(() =>
            {
                var files = new List<string>();
                string warning = "";
                if (!string.IsNullOrWhiteSpace(_unit.PrimaryPhoto)) files.Add(_data.Resolve(_unit.PrimaryPhoto));
                if (!string.IsNullOrWhiteSpace(_unit.PhotosFolder))
                {
                    try
                    {
                        files.AddRange(Directory.EnumerateFiles(_data.Resolve(_unit.PhotosFolder), "*", SearchOption.TopDirectoryOnly)
                            .Where(p => new[] { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warning = "Additional photos are unavailable. Check the shared drive or assigned photo folder."; }
                }
                return (Files: files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Warning: warning);
            });
            if (_closed) return;
            _photos = result.Files;
            _folderWarning = result.Warning;
            PhotoCaption.Text = result.Warning;
            if (_photos.Count == 0) { PhotoMessage.Text = "No photos are assigned to this unit. Confirm the asset and unit number from its label."; return; }
            await ShowPhotoAsync();
        }
        catch (Exception error) { if (!_closed) PhotoMessage.Text = "Unable to load this unit’s photos: " + error.Message; }
    }
    private async Task ShowPhotoAsync()
    {
        _load?.Cancel();
        var source = new CancellationTokenSource(); _load = source; var token = source.Token;
        FullPhoto.Source = null; PhotoMessage.Visibility = Visibility.Visible; PhotoMessage.Text = "Loading photo…";
        PreviousButton.IsEnabled = _index > 0; NextButton.IsEnabled = _index + 1 < _photos.Count;
        PhotoCountText.Text = $"Photo {_index + 1} of {_photos.Count} · whole image";
        string path = _photos[_index];
        PhotoCaption.Text = Path.GetFileName(path) + " • " + _folderWarning;
        try
        {
            var photo = await IdentificationPhotoLoader.LoadAsync(path, 4096, token);
            if (_closed || token.IsCancellationRequested) return;
            FullPhoto.Source = photo.Source;
            PhotoMessage.Visibility = photo.Source is null ? Visibility.Visible : Visibility.Collapsed;
            PhotoMessage.Text = photo.Status == IdentificationPhotoStatus.Missing ? "This assigned photo is unavailable. Check the shared drive or file assignment." : "This photo could not be displayed. Try another photo.";
            if (photo.Source is not null) PhotoCaption.Text = Path.GetFileName(path) + " • Full proportions preserved; original file unchanged. " + _folderWarning;
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_load, source)) _load = null; source.Dispose(); }
    }
    private async void Previous_Click(object sender, RoutedEventArgs e) { if (_index == 0) return; _index--; await ShowPhotoAsync(); }
    private async void Next_Click(object sender, RoutedEventArgs e) { if (_index + 1 >= _photos.Count) return; _index++; await ShowPhotoAsync(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
