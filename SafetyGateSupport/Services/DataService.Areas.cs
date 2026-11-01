using System.IO;
using System.Text.Json;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

public partial class DataService
{
    public sealed record AreaSnapshot(List<string> Areas, List<SafetyGate> Gates, List<SafetySystem> Units);

    // Read fresh records under the shared write lease: setup's in-memory inventory may be stale.
    // Never mutate a list that WPF currently uses as its ItemsSource.
    public AreaSnapshot ChangeArea(string? oldName, string? newName)
    {
        using var lease = AcquireAssetWriteLock();
        if (!IsReady) throw new IOException("Shared data is unavailable. Check the shared-drive connection and retry.");
        oldName = oldName?.Trim(); newName = newName?.Trim();
        if (oldName is null && string.IsNullOrWhiteSpace(newName) || oldName is not null && oldName.Length == 0 || newName is not null && newName.Length == 0)
            throw new InvalidOperationException("Enter an area name.");
        var paths = new[] { CategoriesFile, GatesFile, SystemsFile };
        var before = paths.ToDictionary(p => p, File.ReadAllBytes);
        T Read<T>(string path) => JsonSerializer.Deserialize<T>(before[path], _read) ?? throw new InvalidDataException("Shared records are empty: " + path);
        // Invalid JSON is an error, never an empty inventory that could overwrite existing records.
        var areas = Read<List<string>>(CategoriesFile); var gates = Read<List<SafetyGate>>(GatesFile); var units = Read<List<SafetySystem>>(SystemsFile);
        int index = oldName is null ? -1 : areas.FindIndex(a => a.Equals(oldName, StringComparison.OrdinalIgnoreCase));
        if (oldName is not null && index < 0) throw new InvalidOperationException("That area has changed since Setup was opened. Reopen Setup and retry.");
        if (newName is not null && areas.Where((_, i) => i != index).Any(a => a.Equals(newName, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("That area already exists.");
        if (newName is null && gates.Any(g => g.Category.Equals(oldName, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Move the gates out of this area before deleting it.");
        if (oldName is null) areas.Add(newName!);
        else if (newName is null) areas.RemoveAt(index);
        else areas[index] = newName;
        bool rename = oldName is not null && newName is not null;
        if (rename) foreach (var gate in gates.Where(g => g.Category.Equals(oldName, StringComparison.OrdinalIgnoreCase))) gate.Category = newName!;
        SynchronizeLegacyFields(gates, units);
        var changedPaths = rename ? paths : [CategoriesFile];
        // Catch read-only/locked records before publishing the first file. The root lease also
        // serializes changes made by current app versions. Release preflight handles before copy.
        foreach (string path in changedPaths) using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) { }
        string recovery = Path.Combine(SharedRoot, "Data", "Backups", "Area change " + DateTime.Now.ToString("yyyy-MM-dd HHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(recovery);
        foreach (string path in changedPaths) File.WriteAllBytes(Path.Combine(recovery, Path.GetFileName(path)), before[path]);
        var written = new List<string>();
        try
        {
            SaveCategories(areas); written.Add(CategoriesFile);
            if (rename)
            {
                // Avoid SaveGates' extra read of systems.json: this transaction already read it.
                SaveJsonWithBackup(GatesFile, gates.OrderBy(g => g.Category).ThenBy(g => SortNumber(g.AssetNumber)).ThenBy(g => g.Manufacturer).ToList()); written.Add(GatesFile);
                SaveSystems(units); written.Add(SystemsFile);
            }
        }
        catch (Exception saveError)
        {
            var errors = new List<Exception>();
            foreach (string path in written.AsEnumerable().Reverse())
            {
                try { AtomicCopy(Path.Combine(recovery, Path.GetFileName(path)), path, true); }
                catch (Exception restoreError) { errors.Add(restoreError); }
            }
            if (errors.Count > 0) throw new IOException("Area save failed and automatic recovery could not finish. Close other app copies. Original records are preserved at " + recovery, new AggregateException(new[] { saveError }.Concat(errors)));
            throw new IOException("Area was not changed. Check shared-drive access, close any application locking the shared records, and retry. " + saveError.Message, saveError);
        }
        LogAudit(rename ? "Area renamed" : oldName is null ? "Area added" : "Area deleted", details: rename ? oldName + " -> " + newName : newName ?? oldName!);
        return new(areas, gates, units);
    }
}
