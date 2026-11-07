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
    private async void ImportItem_Click(object sender, RoutedEventArgs e)
    {
        if (_importCancellation is not null) return;
        string id = PartNumberBox.Text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[0-9]{5,12}$")) { ImportItemStatus.Text = "Enter a numeric Item ID (5–12 digits)."; return; }
        if (ImportPlantBox.SelectedItem is not StockroomPlant plant) { ImportItemStatus.Text = "Choose your stockroom plant first."; return; }
        if (!_data.IsReady) { ImportItemStatus.Text = "Initialize shared data first."; return; }
        using var cancel = new CancellationTokenSource(); _importCancellation = cancel;
        SetupTabs.IsEnabled = false; ImportItemStatus.Text = "Looking up " + id + " at " + plant.Name + "…";
        string? temporaryPhoto = null;
        bool saved = false;
        try
        {
            _data.Settings.StockroomPlantId = plant.Id; _data.SaveLocalSettings();
            var snapshot = await _importReader(plant.Url, id, cancel.Token);
            var item = StockroomService.ImportItem(snapshot, id);
            var row = StockroomService.Find(snapshot, item, true)!;
            string photoStatus = "Stockroom supplied no photo. You can add one on the Parts page.";
            if (row.Photo.Length > 0)
            {
                try
                {
                    temporaryPhoto = await DownloadPartPhotoAsync(new Uri(plant.Url), row.Photo, cancel.Token);
                    photoStatus = "Photo imported.";
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { photoStatus = "Item imported; stockroom photo unavailable: " + error.Message; }
            }
            cancel.Token.ThrowIfCancellationRequested();
            if (_isClosed) return;
            var imported = await Task.Run(() => _data.ImportStockroomPart(item)); saved = true;
            if (temporaryPhoto is not null)
            {
                try { imported = await Task.Run(() => _data.SavePartPhoto(imported.Id, temporaryPhoto)); }
                catch (Exception error) { photoStatus = "Item saved; photo could not be saved: " + error.Message; }
            }
            if (_isClosed) return;
            _parts = _data.LoadParts();
            AdminPartList.ItemsSource = _parts.OrderBy(p => p.PartNumber).ThenBy(p => p.Description).ToList();
            AdminPartList.SelectedItem = _parts.Single(p => p.Id == imported.Id);
            ImportItemStatus.Text = "Saved " + imported.Description + " · Item ID " + imported.PartNumber + ". " + photoStatus;
            AdminStatusText.Text = "Stockroom part saved. Optional unit assignments can be changed below.";
        }
        catch (OperationCanceledException) { if (!_isClosed) ImportItemStatus.Text = "Import canceled."; }
        catch (Exception error) { if (!_isClosed) ImportItemStatus.Text = (saved ? "Item was saved, but setup could not reload: " : "Nothing imported: ") + error.Message; }
        finally
        {
            if (temporaryPhoto is not null) { try { File.Delete(temporaryPhoto); } catch (IOException) { } }
            _importCancellation = null;
            if (!_isClosed) SetupTabs.IsEnabled = true;
        }
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
