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
            part.StockroomDescription = imported.StockroomDescription;
            part.Description = imported.Description; part.BinLocation = imported.BinLocation;
            part.Site = imported.Site; part.ReportedQuantity = imported.ReportedQuantity;
            if (imported.Manufacturer.Length > 0) part.Manufacturer = imported.Manufacturer;
            if (part.Notes.Length == 0) part.Notes = imported.Notes;
        }
        SaveParts(parts); LogAudit("Part imported from stockroom", details: part.Display); return part;
    }
    public SafetySystem SaveUnitManuals(string unitId, IEnumerable<string> manualIds) => SaveUnitsManuals([unitId], manualIds).Single();
    public SafetySystem SaveUnitReplacementParts(string unitId, IEnumerable<string> partIds) => SaveUnitsReplacementParts([unitId], partIds).Single();

    // Each unit keeps its own explicit list of parts and manuals. These save the same list to one
    // unit or to every unit on an asset in one locked write.
    public List<SafetySystem> SaveUnitsManuals(IReadOnlyCollection<string> unitIds, IEnumerable<string> manualIds)
    {
        using var lease = AcquireAssetWriteLock();
        var units = ReadUnitsForUpdate();
        var valid = LoadManualRecords().Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = manualIds.Where(valid.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var changed = SelectUnits(units, unitIds);
        foreach (var unit in changed) unit.ManualIds = ids.ToList();
        SaveSystems(units); return changed;
    }
    public List<SafetySystem> SaveUnitsReplacementParts(IReadOnlyCollection<string> unitIds, IEnumerable<string> partIds)
    {
        using var lease = AcquireAssetWriteLock();
        var units = ReadUnitsForUpdate();
        var valid = LoadParts().Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = partIds.Where(valid.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var changed = SelectUnits(units, unitIds);
        foreach (var unit in changed) unit.ReplacementPartIds = ids.ToList();
        SaveSystems(units); return changed;
    }

    /// <summary>
    /// Sets exactly which units use one part. Every unit's current effective list is written out
    /// explicitly first, so older asset-wide or "everywhere" assignments keep showing the same parts.
    /// </summary>
    public void SetPartUsage(string partId, IReadOnlyCollection<string> unitIds)
    {
        using var lease = AcquireAssetWriteLock();
        var units = ReadUnitsForUpdate(); var parts = LoadParts();
        if (!parts.Any(p => p.Id.Equals(partId, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Save the part before choosing where it is used.");
        var chosen = unitIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in units)
        {
            var list = ReplacementPartsFor(unit, parts).Select(p => p.Id).Where(id => !id.Equals(partId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (chosen.Contains(unit.Id)) list.Add(partId);
            unit.ReplacementPartIds = list;
        }
        SaveSystems(units);
    }
    /// <summary>Sets exactly which units list one manual, keeping every other unit's manuals unchanged.</summary>
    public void SetManualUsage(string manualId, IReadOnlyCollection<string> unitIds)
    {
        using var lease = AcquireAssetWriteLock();
        var units = ReadUnitsForUpdate(); var manuals = LoadManualRecords();
        if (!manuals.Any(m => m.Id.Equals(manualId, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Save the manual before choosing where it is used.");
        var chosen = unitIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in units)
        {
            var list = ManualIdsFor(unit, manuals).Where(id => !id.Equals(manualId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (chosen.Contains(unit.Id)) list.Add(manualId);
            unit.ManualIds = list;
        }
        SaveSystems(units);
    }
    internal static List<string> ManualIdsFor(SafetySystem unit, IEnumerable<ManualRecord> manuals) =>
        manuals.Where(m => unit.ManualIds is { } ids ? ids.Contains(m.Id, StringComparer.OrdinalIgnoreCase) : m.AppliesTo(unit.GateId, unit.Id)).Select(m => m.Id).ToList();

    private List<SafetySystem> ReadUnitsForUpdate() =>
        JsonSerializer.Deserialize<List<SafetySystem>>(File.ReadAllText(SystemsFile), _read) ?? throw new InvalidDataException("Unit records unavailable.");
    private static List<SafetySystem> SelectUnits(List<SafetySystem> units, IReadOnlyCollection<string> unitIds)
    {
        var selected = units.Where(u => unitIds.Contains(u.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count != unitIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()) throw new InvalidOperationException("A unit was removed. Refresh first.");
        return selected;
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
