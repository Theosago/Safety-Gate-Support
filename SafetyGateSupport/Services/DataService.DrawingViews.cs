using System.IO;
using System.Text.Json;
namespace SafetyGateSupport.Services;

public partial class DataService
{
    private string DrawingViewsFile => Path.Combine(SharedRoot, "_AppData", "DrawingViews.json");
    public string DrawingViewKey(string file, int page)
    {
        var info = new FileInfo(file);
        return ToRelative(file) + $"|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{page}";
    }
    public Dictionary<string, int> LoadDrawingViews(string unitId)
    {
        try { return File.Exists(DrawingViewsFile) ? (JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(DrawingViewsFile))?.GetValueOrDefault(unitId) ?? new()) : new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void SaveDrawingView(string unitId, string key, int rotation)
    {
        if (rotation is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(rotation));
        using var lease = AcquireAssetWriteLock();
        var records = File.Exists(DrawingViewsFile) ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(DrawingViewsFile)) ?? throw new InvalidDataException("Drawing view settings are empty.") : new();
        if (!records.TryGetValue(unitId, out var views)) records[unitId] = views = new();
        views[key] = rotation; SaveJsonWithBackup(DrawingViewsFile, records);
    }
}
