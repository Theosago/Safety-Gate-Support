using System.IO;
using System.Text.Json;
using SafetyGateSupport.Models;
namespace SafetyGateSupport.Services;

public partial class DataService
{
    public HelpItem SaveHelpTopic(HelpItem item, string photoSource = "", bool removePhoto = false)
    {
        using var lease = AcquireAssetWriteLock();
        var old = LoadHelpItems().SingleOrDefault(h => h.Id == item.Id);
        var paths = removePhoto ? new List<string>() : photoSource.Length > 0 ? [photoSource] : old?.Photos.ToList() ?? [];
        return SaveHelpTopicPhotos(item, paths);
    }
    public HelpItem SaveHelpTopicPhotos(HelpItem item, IReadOnlyList<string> photoSources)
    {
        if (string.IsNullOrWhiteSpace(item.Title) || item.Section.Equals("Common Problems", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(item.Body))
            throw new InvalidOperationException("A common problem needs a title and fix instructions.");
        if (photoSources.Count > 20) throw new InvalidOperationException("Choose at most 20 photos for one problem.");
        using var lease = AcquireAssetWriteLock();
        var topics = JsonSerializer.Deserialize<List<HelpItem>>(File.ReadAllText(HelpFile), _read) ?? throw new InvalidDataException("Shared help records unavailable.");
        var old = topics.SingleOrDefault(h => h.Id == item.Id);
        var copied = new List<string>(); var paths = new List<string>();
        try
        {
            foreach (string source in photoSources.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (old?.Photos.Contains(source, StringComparer.OrdinalIgnoreCase) == true) { paths.Add(source); continue; }
                string extension = Path.GetExtension(source).ToLowerInvariant();
                if (!Path.IsPathRooted(source) || !new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" }.Contains(extension)) throw new InvalidDataException("Choose a supported photo file.");
                if (new FileInfo(source).Length > 64 * 1024 * 1024) throw new InvalidDataException("Choose a photo smaller than 64 MB.");
                string target = Path.Combine(SharedRoot, "HelpPhotos", Guid.NewGuid().ToString("N") + extension);
                AtomicCopy(source, target); copied.Add(target); paths.Add(ToRelative(target));
            }
            item.PhotoPaths = paths; item.PhotoPath = paths.FirstOrDefault() ?? "";
            if (old is not null) topics.Remove(old); topics.Add(item);
            SaveHelpItems(topics);
        }
        catch { foreach (string path in copied) File.Delete(path); throw; }
        // Retain previous files for backed-up help records.
        LogAudit("Help topic saved", details: item.Title); return item;
    }
    public PartRecord ImportStockroomPart(PartRecord imported)
    {
        using var lease = AcquireAssetWriteLock();
        var parts = JsonSerializer.Deserialize<List<PartRecord>>(File.ReadAllText(PartsFile), _read) ?? throw new InvalidDataException("Shared parts records unavailable.");
        var matches = parts.Where(p => StockroomService.StockKey(p.PartNumber) == StockroomService.StockKey(imported.PartNumber)).ToList();
        if (matches.Count > 1) throw new InvalidOperationException("Duplicate Item IDs exist in the shared library. Resolve them before importing.");
        var part = matches.SingleOrDefault();
        if (part is null) { part = imported; parts.Add(part); }
        else
        {
            part.Description = imported.Description; part.BinLocation = imported.BinLocation;
            part.Site = imported.Site; part.ReportedQuantity = imported.ReportedQuantity;
            if (imported.Manufacturer.Length > 0) part.Manufacturer = imported.Manufacturer;
            if (part.Notes.Length == 0) part.Notes = imported.Notes;
        }
        SaveParts(parts); LogAudit("Part imported from stockroom", details: part.Display); return part;
    }
    public SafetySystem SaveUnitManuals(string unitId, IEnumerable<string> manualIds)
    {
        using var lease = AcquireAssetWriteLock();
        var units = JsonSerializer.Deserialize<List<SafetySystem>>(File.ReadAllText(SystemsFile), _read) ?? throw new InvalidDataException("Unit records unavailable.");
        var unit = units.SingleOrDefault(u => u.Id == unitId) ?? throw new InvalidOperationException("Unit removed. Refresh first.");
        var valid = LoadManualRecords().Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        unit.ManualIds = manualIds.Where(valid.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SaveSystems(units); return unit;
    }
    public SafetySystem SaveUnitReplacementParts(string unitId, IEnumerable<string> partIds)
    {
        using var lease = AcquireAssetWriteLock();
        var units = JsonSerializer.Deserialize<List<SafetySystem>>(File.ReadAllText(SystemsFile), _read) ?? throw new InvalidDataException("Unit records unavailable.");
        var unit = units.SingleOrDefault(u => u.Id == unitId) ?? throw new InvalidOperationException("This unit was removed. Refresh first.");
        var valid = LoadParts().Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        unit.ReplacementPartIds = partIds.Where(valid.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SaveSystems(units); return unit;
    }
    public PartRecord SavePartPhoto(string partId, string source)
    {
        using var lease = AcquireAssetWriteLock();
        string extension = Path.GetExtension(source).ToLowerInvariant();
        if (!new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" }.Contains(extension)) throw new InvalidOperationException("Choose a JPG, PNG, BMP, GIF or TIFF photo.");
        if (new FileInfo(source).Length > 64 * 1024 * 1024) throw new IOException("Choose a photo smaller than 64 MB.");
        var parts = JsonSerializer.Deserialize<List<PartRecord>>(File.ReadAllText(PartsFile), _read) ?? throw new InvalidDataException("Shared parts records are empty.");
        var part = parts.SingleOrDefault(p => p.Id == partId) ?? throw new InvalidOperationException("This part was removed. Refresh the parts list before uploading.");
        string photoRoot = Path.Combine(PartsLibraryFolder, "Photos");
        string folder = Path.Combine(photoRoot, AssetFileLayout.SafeName(string.IsNullOrWhiteSpace(part.PartNumber) ? part.Description : part.PartNumber));
        string target = Path.Combine(folder, AssetFileLayout.SafeName(Path.GetFileNameWithoutExtension(source)) + " - " + Guid.NewGuid().ToString("N")[..8] + extension);
        string old = string.IsNullOrWhiteSpace(part.PhotoPath) ? "" : Resolve(part.PhotoPath);
        AtomicCopy(source, target); part.PhotoPath = ToRelative(target);
        try { SaveParts(parts); } catch { File.Delete(target); throw; }
        if (old.Length > 0 && !parts.Any(p => p.Id != partId && !string.IsNullOrWhiteSpace(p.PhotoPath) && Resolve(p.PhotoPath).Equals(old, StringComparison.OrdinalIgnoreCase)) &&
            Path.GetFullPath(old).StartsWith(Path.GetFullPath(photoRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(old))
        {
            try { string archive = Path.Combine(Path.GetDirectoryName(old)!, "Archive"); Directory.CreateDirectory(archive); File.Move(old, Path.Combine(archive, Path.GetFileName(old))); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LogAudit("Previous part photo retained", details: old + ": " + error.Message); }
        }
        LogAudit("Part photo uploaded", details: part.Display); return part;
    }
}
