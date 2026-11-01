using System.IO;
using System.Reflection;
using System.Text.Json;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

public partial class DataService
{
    private string PartsCatalogMarker => Path.Combine(SharedRoot, "_AppData", "Catalogs", "safe2cross-parts-v2.json");
    private List<PartRecord> MergePartsCatalog(List<PartRecord> existing) => MergePartsCatalog(existing, out _);
    private List<PartRecord> MergePartsCatalog(List<PartRecord> existing, out bool updated)
    {
        updated = false;
        var assembly = Assembly.GetExecutingAssembly();
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("Catalog.Safe2CrossParts.json", StringComparison.Ordinal));
        using var input = assembly.GetManifestResourceStream(name)!;
        var catalog = JsonSerializer.Deserialize<List<PartRecord>>(input, _read) ?? throw new InvalidDataException("The bundled parts catalog is empty.");
        static string StockKey(string? value) => (value ?? "").Trim().TrimStart('0');
        foreach (var part in existing.Where(p => StockKey(p.PartNumber) == "117716" && (p.SiteValue.Length == 0 || p.SiteValue.Equals("Not supplied", StringComparison.OrdinalIgnoreCase))))
        {
            part.Site = "HRM"; part.Notes = (part.Notes ?? "").Replace("Site: Not supplied", "Site: HRM", StringComparison.OrdinalIgnoreCase); updated = true;
        }
        var ids = existing.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stocks = existing.Select(p => StockKey(p.PartNumber)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return existing.Concat(catalog.Where(p => !ids.Contains(p.Id) && stocks.Add(StockKey(p.PartNumber)))).ToList();
    }
    private void EnsurePartsCatalog()
    {
        if (File.Exists(PartsCatalogMarker)) return;
        try
        {
            using var lease = AcquireAssetWriteLock();
            if (File.Exists(PartsCatalogMarker)) return;
            // Strict read prevents a damaged custom catalog from being overwritten by defaults.
            var existing = JsonSerializer.Deserialize<List<PartRecord>>(File.ReadAllText(PartsFile), _read) ?? throw new InvalidDataException("Shared parts records are empty.");
            var merged = MergePartsCatalog(existing, out bool updated);
            if (updated || merged.Count != existing.Count) SaveParts(merged);
            SaveJsonWithBackup(PartsCatalogMarker, new { catalog = "Safe2Cross parts v2", importedUtc = DateTimeOffset.UtcNow });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Read-only technician PCs can still display bundled additions through LoadParts.
            // When writable, the marker makes this a one-time import: edits/deletions stay local.
        }
    }
}
