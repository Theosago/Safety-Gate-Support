using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class AdminWindow
{
    private readonly Func<string, string, CancellationToken, Task<StockroomService.Snapshot>> _importReader;
    private CancellationTokenSource? _importCancellation;
    private void ImportItem_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; ImportItem_Click(sender, e); }
    }
    public sealed record ItemImportResult(string ItemId, string Status);
    private void SetImportStatus(string text) { ImportItemStatus.Text = text; AdminStatusText.Text = text; }
    private async void ImportItem_Click(object sender, RoutedEventArgs e)
    {
        string id = PartNumberBox.Text.Trim();
        if (!StockroomService.IsItemId(id)) { SetImportStatus("Enter a valid Item ID (up to 64 letters, digits or - _ . /)."); return; }
        await RunItemImportsAsync([id]);
    }
    private async void BulkImport_Click(object sender, RoutedEventArgs e)
    {
        try { await RunItemImportsAsync(StockroomService.ParseItemIds(BulkItemIdsBox.Text)); }
        catch (InvalidOperationException error) { SetImportStatus(error.Message); }
    }
    private void CancelPartImport_Click(object sender, RoutedEventArgs e) => _importCancellation?.Cancel();
    private async Task RunItemImportsAsync(IReadOnlyList<string> ids)
    {
        if (_importCancellation is not null) return;
        if (ImportPlantBox.SelectedItem is not StockroomPlant plant) { SetImportStatus("Choose your stockroom plant once, then import by Item ID."); return; }
        if (!_data.IsReady) { SetImportStatus("Connect to a plant library first."); return; }
        using var cancel = new CancellationTokenSource(); _importCancellation = cancel;
        SetupTabs.IsEnabled = false; CancelPartImportButton.Visibility = Visibility.Visible;
        var results = new List<ItemImportResult>(); string? lastSavedId = null; int importedCount = 0;
        void ShowResults() { ImportResultsList.ItemsSource = null; ImportResultsList.ItemsSource = results.ToArray(); }
        try
        {
            _data.Settings.StockroomPlantId = plant.Id; _data.SaveLocalSettings();
            foreach (string id in ids)
            {
                if (cancel.IsCancellationRequested || _isClosed) break;
                SetImportStatus($"Looking up {id} at {plant.Name} ({results.Count + 1}/{ids.Count})…");
                string? photo = null;
                try
                {
                    var snapshot = await _importReader(plant.Url, id, cancel.Token);
                    var item = StockroomService.ImportItem(snapshot, id);
                    var row = StockroomService.Find(snapshot, item, true)!;
                    string photoStatus = "No photo supplied by the stockroom.";
                    if (row.Photo.Length > 0)
                    {
                        try { photo = await DownloadPartPhotoAsync(new Uri(plant.Url), row.Photo, cancel.Token); photoStatus = "Photo imported."; }
                        catch (Exception error) when (error is not OperationCanceledException) { photoStatus = "Photo unavailable: " + error.Message; }
                    }
                    cancel.Token.ThrowIfCancellationRequested();
                    var saved = await Task.Run(() => _data.ImportStockroomPart(item));
                    importedCount++; lastSavedId = saved.Id;
                    if (photo is not null)
                    {
                        try { await Task.Run(() => _data.SavePartPhoto(saved.Id, photo)); }
                        catch (Exception error) { photoStatus = "Item saved; photo not saved: " + error.Message; }
                    }
                    results.Add(new(id, "Saved " + saved.Description + ". " + photoStatus));
                }
                catch (OperationCanceledException) { results.Add(new(id, "Canceled; any already committed item is retained.")); break; }
                catch (Exception error) { results.Add(new(id, "Not imported: " + error.Message)); }
                finally { if (photo is not null) { try { File.Delete(photo); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } } }
                if (!_isClosed) ShowResults();
            }
            if (!_isClosed)
            {
                ShowResults();
                _parts = await Task.Run(() => _data.LoadParts());
                if (_isClosed) return;
                AdminPartList.ItemsSource = _parts.OrderBy(p => p.PartNumber).ThenBy(p => p.Description).ToList();
                if (lastSavedId is not null) AdminPartList.SelectedItem = _parts.Single(p => p.Id == lastSavedId);
                SetImportStatus($"Imported {importedCount} of {ids.Count} Item IDs. " + (cancel.IsCancellationRequested ? "Canceled. " : "") + "See each result below. Existing assignments and documents are retained.");
            }
        }
        catch (Exception error) { if (!_isClosed) SetImportStatus($"Imported {importedCount} items before the operation stopped: " + error.Message); }
        finally { _importCancellation = null; if (!_isClosed) { SetupTabs.IsEnabled = true; CancelPartImportButton.Visibility = Visibility.Collapsed; } }
    }
    private static async Task<string> DownloadPartPhotoAsync(Uri origin, string source, CancellationToken token)
    {
        if (!Uri.TryCreate(origin, source, out var uri) || uri.Scheme != origin.Scheme || uri.Host != origin.Host || uri.Port != origin.Port)
            throw new InvalidDataException("The photo is outside the selected stockroom site.");
        using var handler = new HttpClientHandler { UseDefaultCredentials = true, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        string extension = response.Content.Headers.ContentType?.MediaType switch
        { "image/jpeg" => ".jpg", "image/png" => ".png", "image/gif" => ".gif", "image/bmp" => ".bmp", _ => throw new InvalidDataException("Unsupported photo format.") };
        const int max = 8 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > max) throw new InvalidDataException("Photo exceeds 8 MB.");
        string path = Path.Combine(Path.GetTempPath(), "stockroom-part-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = File.Create(path))
            {
                byte[] buffer = new byte[16384]; int count, total = 0;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                { total += count; if (total > max) throw new InvalidDataException("Photo exceeds 8 MB."); await output.WriteAsync(buffer.AsMemory(0, count), token); }
            }
            if ((await IdentificationPhotoLoader.LoadAsync(path, 640, token)).Source is null) throw new InvalidDataException("The photo could not be decoded.");
            return path;
        }
        catch { File.Delete(path); throw; }
    }
}
