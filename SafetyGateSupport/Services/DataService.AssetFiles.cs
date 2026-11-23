using System.IO;
using System.Security.Cryptography;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

public partial class DataService
{
    [ThreadStatic] private static HashSet<string>? _heldAssetLocks;
    private sealed class AssetWriteLease(string root, FileStream? stream) : IDisposable
    {
        public void Dispose() { if (stream is not null) { stream.Dispose(); _heldAssetLocks?.Remove(root); } }
    }
    internal IDisposable AcquireAssetWriteLock()
    {
        string root = Path.GetFullPath(SharedRoot); _heldAssetLocks ??= new(StringComparer.OrdinalIgnoreCase);
        if (_heldAssetLocks.Contains(root)) return new AssetWriteLease(root, null);
        string folder = Path.Combine(root, "_AppData"); Directory.CreateDirectory(folder);
        FileStream file;
        try { file = new FileStream(Path.Combine(folder, "asset-files.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException("Another file update or layout conversion is in progress. Wait for it to finish, then retry.", error); }
        _heldAssetLocks.Add(root); return new AssetWriteLease(root, file);
    }

    public string[] GetResourceFiles(ResourceDefinition resource, SafetyGate gate) => Directory.GetFiles(resource.Kind.Equals("Photos", StringComparison.OrdinalIgnoreCase) ? ResourceBaseFolder(resource, gate) : ResourceCurrentFolder(resource, gate))
        .Where(p => resource.IncludesFile(Path.GetFileName(p))).ToArray();

    public string AssignAssetFile(SafetyGate gate, SafetySystem unit, string sourceFile, string category)
    {
        using var lease = AcquireAssetWriteLock();
        var resources = LoadResources();
        var resource = resources.FirstOrDefault(r => r.GateId == gate.Id && r.StorageMode == "Asset" && AssetFileLayout.Category(r) == category);
        if (string.IsNullOrWhiteSpace(gate.AssetFolder))
        {
            // Legacy installations remain usable until the reviewed layout conversion.
            resource = ResourcesFor(gate, unit, resources).FirstOrDefault(r => r.Kind == "ManagedFiles" && AssetFileLayout.Category(r) == category && !r.Label.Contains("(imported)", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Organize asset folders in Setup before assigning a new resource type.");
            resource.ArchiveMode = "SameName";
            var path = AddOrUpdateResource(resource, gate, unit, sourceFile); SaveResources(resources); return path;
        }
        resource ??= AssetFileLayout.Create(gate, category);
        return AddAssetFile(resource.ForUnit(unit.Id), gate, unit, sourceFile);
    }

    private string AddAssetFile(ResourceDefinition view, SafetyGate gate, SafetySystem? unit, string sourceFile)
    {
        using var lease = AcquireAssetWriteLock();
        if (AssetFileLayout.Category(view) == "PLC") unit = null;
        EnsureResourceFolders(view, gate);
        var resources = LoadResources(); var resource = resources.FirstOrDefault(r => r.Id == view.Id) ?? view;
        var folder = ResourceCurrentFolder(resource, gate);
        string source = Path.GetFullPath(sourceFile), name = Path.GetFileName(source);
        string target = Path.Combine(folder, name);
        var identical = Directory.GetFiles(folder).FirstOrDefault(p => Path.GetExtension(p).Equals(Path.GetExtension(source), StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(p).StartsWith(".") && SameBytes(source, p));
        if (identical is not null) { target = identical; name = Path.GetFileName(identical); }
        if (AssetFileLayout.Category(resource) == "Electrical Drawings" && identical is null) throw new InvalidOperationException("Add or replace this print as a PDF/DWG pair. Select both companions together.");
        // A same-name file belonging to another unit is never replaced by assignment.
        if (File.Exists(target) && !SameBytes(source, target))
        {
            bool linkedHere = resource.FileAssignments.TryGetValue(name, out var ids) && (unit is null || ids.Count == 0 || ids.Contains(unit.Id, StringComparer.OrdinalIgnoreCase));
            if (linkedHere) throw new InvalidOperationException("A different file already has that name. Select it and use Replace selected file so the old revision is archived explicitly.");
            name = AssetFileLayout.SafeName(unit?.UnitDisplay ?? "Asset") + " - " + name; target = Path.Combine(folder, name);
            if (File.Exists(target) && !SameBytes(source, target)) throw new IOException("The destination filename is already used by a different file. Choose a distinct filename or replace the intended sheet explicitly.");
        }
        if (!File.Exists(target)) AtomicCopy(source, target);
        var key = resource.FileAssignments.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
        if (!resource.FileAssignments.TryGetValue(key, out var links)) resource.FileAssignments[key] = unit is null ? [] : [unit.Id];
        else if (unit is not null && links.Count != 0 && !links.Contains(unit.Id, StringComparer.OrdinalIgnoreCase)) links.Add(unit.Id);
        else if (unit is null) resource.FileAssignments[key] = [];
        if (!resources.Any(r => r.Id == resource.Id)) resources.Add(resource);
        var drawingPair = resource.DrawingPairs.FirstOrDefault(p => p.Contains(name));
        if (drawingPair is not null && unit is not null)
            foreach (string companion in new[] { drawingPair.Pdf, drawingPair.Dwg })
                if (resource.FileAssignments.TryGetValue(companion, out var scope) && scope.Count != 0 && !scope.Contains(unit.Id)) scope.Add(unit.Id);
        SaveResources(resources); view.FileAssignments = resource.FileAssignments; WriteAssetFileIndex(gate);
        LogAudit("Asset file linked", gate.Id, unit?.Id ?? "", resource.Id, name); return target;
    }

    public string ReplaceAssetFile(ResourceDefinition view, SafetyGate gate, string selectedFile, string sourceFile)
    {
        if (view.StorageMode != "Asset") throw new InvalidOperationException("Organize asset folders before using explicit file replacement.");
        if (AssetFileLayout.Category(view) == "Electrical Drawings") throw new InvalidOperationException("Drawing updates require the PDF and DWG together. Use paired replacement.");
        using var lease = AcquireAssetWriteLock();
        var resources = LoadResources(); var resource = resources.Single(r => r.Id == view.Id);
        string folder = ResourceCurrentFolder(resource, gate), name = Path.GetFileName(selectedFile);
        if (!Path.GetFullPath(selectedFile).Equals(Path.GetFullPath(Path.Combine(folder, name)), StringComparison.OrdinalIgnoreCase) || !view.IncludesFile(name)) throw new InvalidOperationException("Select a current file from this resource.");
        string key = resource.FileAssignments.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("The selected file is not linked in the asset index.");
        string replacementName = Path.GetFileName(sourceFile), target = Path.Combine(folder, replacementName);
        if (SameBytes(selectedFile, sourceFile)) return selectedFile;
        if (!target.Equals(selectedFile, StringComparison.OrdinalIgnoreCase) && File.Exists(target)) throw new IOException("Another current file already has the replacement filename. It has not been overwritten.");
        string archive = ResourceArchiveFolder(resource, gate); Directory.CreateDirectory(archive);
        string backup = Path.Combine(archive, Path.GetFileNameWithoutExtension(name) + " - " + DateTime.Now.ToString("yyyy-MM-dd HHmmssfff") + Path.GetExtension(name));
        File.Copy(selectedFile, backup, false);
        AtomicCopy(sourceFile, target, overwrite: target.Equals(selectedFile, StringComparison.OrdinalIgnoreCase));
        var links = resource.FileAssignments[key]; resource.FileAssignments.Remove(key); resource.FileAssignments[replacementName] = links;
        try { SaveResources(resources); }
        catch { if (target.Equals(selectedFile, StringComparison.OrdinalIgnoreCase)) File.Copy(backup, selectedFile, true); else File.Delete(target); throw; }
        if (!target.Equals(selectedFile, StringComparison.OrdinalIgnoreCase)) File.Delete(selectedFile);
        view.FileAssignments = resource.FileAssignments; WriteAssetFileIndex(gate);
        LogAudit("Asset file replaced", gate.Id, resourceId: resource.Id, details: $"{name} -> {replacementName}; all linked units updated"); return target;
    }
    internal List<SafetySystem> AssetFileUnits(SafetyGate gate, ResourceDefinition resource) =>
        LoadSystems().Where(u => u.GateId == gate.Id && resource.AppliesTo(u.Id))
            .OrderBy(u => int.TryParse(u.UnitNumber, out int number) ? number : int.MaxValue)
            .ThenBy(u => u.UnitDisplay, StringComparer.OrdinalIgnoreCase).ToList();

    private static List<string> ValidateFileUnits(IEnumerable<string> requested, IReadOnlyList<SafetySystem> eligible)
    {
        var selection = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = eligible.Where(unit => selection.Contains(unit.Id)).Select(unit => unit.Id).ToList();
        if (ids.Count == 0 || ids.Count != selection.Count)
            throw new InvalidOperationException("Select at least one unit from this asset's file collection.");
        return ids;
    }

    private static List<string> ExpandFileScope(IReadOnlyCollection<string> scope, IReadOnlyList<SafetySystem> units) =>
        units.Where(unit => scope.Count == 0 || scope.Contains(unit.Id, StringComparer.OrdinalIgnoreCase))
            .Select(unit => unit.Id).ToList();

    private static bool SameFileScope(IEnumerable<string> left, IEnumerable<string> right) =>
        left.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(right);

    private static void WriteFileScope(ResourceDefinition record, string name, List<string> ids)
    {
        string key = record.FileAssignments.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
        record.FileAssignments[key] = ids.ToList();
    }

    public IReadOnlyList<string> SetAssetFileUnits(ResourceDefinition view, SafetyGate gate, string filename,
        IEnumerable<string> unitIds, IReadOnlyCollection<string>? expectedScope = null)
    {
        using var lease = AcquireAssetWriteLock();
        var resources = LoadResources();
        var record = resources.SingleOrDefault(r => r.Id == view.Id && r.GateId == gate.Id && r.StorageMode == "Asset")
            ?? throw new InvalidOperationException("The file collection is no longer available.");
        var eligible = AssetFileUnits(gate, record);
        var ids = ValidateFileUnits(unitIds, eligible);
        var prior = record.FileScope(filename) ?? throw new InvalidOperationException("The file is no longer in the asset index.");
        if (expectedScope is not null && !SameFileScope(prior, expectedScope))
            throw new InvalidOperationException("The unit links changed while this window was open. Open Units covered again.");
        var pair = record.DrawingPairs.FirstOrDefault(p => p.Contains(filename));
        var files = pair is null ? new[] { filename } : new[] { pair.Pdf, pair.Dwg };
        var affected = files.SelectMany(name => ExpandFileScope(record.FileScope(name) ?? ids, eligible))
            .Concat(ids).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Keep the existing all-unit rule if the user leaves that selection as it was.
        var newScope = ids.Count == eligible.Count && files.All(name => record.FileScope(name) is { Count: 0 }) ? [] : ids;
        foreach (string name in files) WriteFileScope(record, name, newScope);
        SaveResources(resources);
        view.FileAssignments = record.FileAssignments;
        view.DrawingPairs = record.DrawingPairs;
        WriteAssetFileIndex(gate);
        LogAudit("Asset file unit links changed", gate.Id, resourceId: record.Id, details: filename);
        return affected;
    }

    // The index is a readable companion, not the authoritative unit assignment record.
    // A locked spreadsheet must not report an already committed drawing upload as failed.
    public string WriteAssetFileIndex(SafetyGate gate)
    {
        if (string.IsNullOrWhiteSpace(gate.AssetFolder)) return "";
        try { WriteAssetFileIndexCore(gate); return ""; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return "Files were saved, but the readable file index could not be refreshed. Close File index.csv and retry folder organization. " + error.Message; }
    }
    private void WriteAssetFileIndexCore(SafetyGate gate)
    {
        string root = Resolve(gate.AssetFolder); Directory.CreateDirectory(root);
        foreach (string category in new[] { "Electrical Drawings", "Light Configurations", "Scanner Configurations", "PLC", "Photos", "Notes", "Archive" }) Directory.CreateDirectory(Path.Combine(root, category));
        var units = LoadSystems().Where(u => u.GateId == gate.Id).ToDictionary(u => u.Id, u => u.UnitDisplay);
        static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var lines = new List<string> { "Folder,File,Applies to" };
        foreach (var resource in LoadResources().Where(r => r.GateId == gate.Id && r.StorageMode == "Asset"))
            foreach (var pair in resource.FileAssignments.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                lines.Add(string.Join(",", Csv(AssetFileLayout.Category(resource)), Csv(pair.Key), Csv(pair.Value.Count == 0 ? "All units" : string.Join("; ", pair.Value.Select(id => units.GetValueOrDefault(id, "Unrecognized unit"))))));
        WriteReadableIndex(Path.Combine(root, "File index.csv"), string.Join(Environment.NewLine, lines), new System.Text.UTF8Encoding(true));
        WriteReadableIndex(Path.Combine(root, "READ ME.txt"), $"{gate.AssetDisplay} — {gate.ManufacturerDisplay}\n{gate.Category} • {gate.Location}\n\nActive files are in the named folders. File index.csv lists the units each file applies to.\nElectrical Drawings, Light Configurations, Scanner Configurations and PLC contain current files.\nPhotos and Notes have a folder for each unit. Archive contains previous revisions.\nUse only the approved configuration for the exact unit. A shared print is one PDF/DWG pair linked to the units it covers.\nIn the app, Add drawing pair saves one PDF and DWG for the selected units. Units covered changes links on an existing print. Replacing a pair archives both old companions and retains unit links. Warnings identify missing or independently changed files; file checks do not prove that a PDF was plotted from its DWG.\n");
    }

    private static void WriteReadableIndex(string destination, string content, System.Text.Encoding? encoding = null)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, content, encoding ?? System.Text.Encoding.UTF8); File.Move(temporary, destination, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static bool SameBytes(string left, string right)
    {
        if (Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase)) return true;
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        using var a = File.OpenRead(left); using var b = File.OpenRead(right);
        return SHA256.HashData(a).SequenceEqual(SHA256.HashData(b));
    }
    internal static void AtomicCopy(string source, string destination, bool overwrite = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temp = Path.Combine(Path.GetDirectoryName(destination)!, ".copy-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.Copy(source, temp, false);
            if (!SameBytes(source, temp)) throw new IOException("File verification failed; source was preserved.");
            File.Move(temp, destination, overwrite);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
