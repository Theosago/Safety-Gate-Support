using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

/// <summary>Reviewable, hash-verified conversion. Sources stay intact until all new files and metadata exist.</summary>
public static class AssetStorageMigration
{
    public enum Disposition { Current, Archive, SeparateSheet }
    public sealed class FileEntry
    {
        public required List<string> Sources { get; init; }
        public required string Destination { get; init; }
        public required string Hash { get; init; }
        public required string GateId { get; init; }
        public required string Category { get; init; }
        public List<string> UnitIds { get; init; } = [];
        public string ResourceId { get; init; } = "";
        public bool Historical { get; init; }
        public int Number { get; init; }
        public Disposition Decision { get; set; }
        public string Source => string.Join(Environment.NewLine, Sources);
        public string Scope { get; init; } = "";
        public string PreviewTarget { get; set; } = "";
        public string Target(Plan plan)
        {
            if (Historical) return Destination;
            if (Decision == Disposition.Archive)
            {
                string root = plan.Gates.Single(g => g.Id == GateId).AssetFolder;
                return $"{root}/Archive/{Category}/{plan.Stamp}/{Number} - {Path.GetFileName(Destination)}";
            }
            string existing = Path.Combine(plan.SharedRoot, Destination.Replace('/', Path.DirectorySeparatorChar));
            if (Decision == Disposition.Current && File.Exists(existing) && plan.Files.Any(f => f != this && f.Decision == Disposition.Archive && f.Hash != Hash && f.Sources.Contains(existing, StringComparer.OrdinalIgnoreCase)))
                return Path.Combine(Path.GetDirectoryName(Destination)!, Path.GetFileNameWithoutExtension(Destination) + " - revision " + plan.Stamp + Path.GetExtension(Destination)).Replace('\\', '/');
            return Decision == Disposition.SeparateSheet ? Path.Combine(Path.GetDirectoryName(Destination)!, Path.GetFileNameWithoutExtension(Destination) + $" - additional sheet {Number}" + Path.GetExtension(Destination)).Replace('\\', '/') : Destination;
        }
    }
    public sealed class Plan
    {
        public required string SharedRoot { get; init; }
        public string Stamp { get; init; } = DateTime.Now.ToString("yyyy-MM-dd HHmmssfff");
        public required List<SafetyGate> Gates { get; init; }
        public required List<SafetySystem> Units { get; init; }
        public required List<ResourceDefinition> Resources { get; init; }
        public required List<ManualRecord> Manuals { get; init; }
        public required List<PartRecord> Parts { get; init; }
        public List<TroubleshootingFlow> SavedFlows { get; init; } = [];
        public Dictionary<string, List<string>> ResourceAliases { get; } = new();
        public List<string> OldFolders { get; } = [];
        public Dictionary<string, string> MetadataHashes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<FileEntry> Files { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];
        public string Summary => $"{Gates.Count} assets • {Files.Count} distinct files • {Files.Sum(f => f.Sources.Count) - Files.Count} duplicate copies • {Warnings.Count} notices";
    }
    private sealed record Candidate(string Source, string Destination, string GateId, string Category, string ResourceId, List<string> Units, bool Historical);
    public static Plan Build(DataService data, CancellationToken token = default)
    {
        if (!data.IsReady) throw new InvalidOperationException("Connect shared data before planning the file layout.");
        var oldGates = data.LoadGates(); var oldUnits = data.LoadSystems(oldGates); var oldResources = data.LoadResources();
        var plan = new Plan { SharedRoot = Path.GetFullPath(data.SharedRoot), Gates = Clone(oldGates), Units = Clone(oldUnits), Resources = [], Manuals = data.LoadManualRecords(), Parts = data.LoadParts(), SavedFlows = File.Exists(data.TroubleshootingFile) ? JsonSerializer.Deserialize<List<TroubleshootingFlow>>(File.ReadAllText(data.TroubleshootingFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [] : [] };
        foreach (string path in MetadataPaths(data)) plan.MetadataHashes[path] = File.Exists(path) ? Hash(path) : "missing";
        var candidates = new List<Candidate>(); var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gate in plan.Gates)
        {
            token.ThrowIfCancellationRequested();
            var oldGate = oldGates.Single(g => g.Id == gate.Id);
            string root = string.IsNullOrWhiteSpace(gate.AssetFolder) ? AssetFileLayout.SuggestedRoot(gate) : gate.AssetFolder;
            if (!seenRoots.Add(root))
            {
                root += " - " + AssetFileLayout.SafeName($"{gate.Category} - {gate.ManufacturerDisplay}");
                if (!seenRoots.Add(root)) { plan.Errors.Add($"Ambiguous asset folder: {root}. Give these assets distinct numbers or locations first."); continue; }
            }
            gate.AssetFolder = root;
            var units = oldUnits.Where(u => u.GateId == gate.Id).ToList();
            if (units.GroupBy(AssetFileLayout.UnitName, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) { plan.Errors.Add($"{gate.Display}: duplicate or unknown unit numbers must be resolved before organizing."); continue; }
            var managed = oldResources.Where(r => r.GateId == gate.Id && r.Kind.Equals("ManagedFiles", StringComparison.OrdinalIgnoreCase)).ToList();
            // Include the asset PLC files that existed before PLC became an indexed asset resource.
            string oldPlc = data.Resolve("PLC/" + gate.Id);
            if (Directory.Exists(oldPlc) && !managed.Any(r => AssetFileLayout.Category(r) == "PLC")) managed.Add(new ResourceDefinition { Id = "legacy-plc-" + gate.Id, GateId = gate.Id, Label = "PLC files", RelativeFolder = "PLC/" + gate.Id });
            foreach (var group in managed.GroupBy(AssetFileLayout.Category, StringComparer.OrdinalIgnoreCase))
            {
                var canonical = AssetFileLayout.Create(gate, group.Key);
                canonical.IsVisible = group.Any(r => r.IsVisible); canonical.AllowUserUpload = group.Any(r => r.AllowUserUpload) || group.Key is "Electrical Drawings" or "PLC";
                plan.Resources.Add(canonical);
                foreach (var resource in group)
                {
                    token.ThrowIfCancellationRequested();
                    plan.ResourceAliases[resource.Id] = [canonical.Id];
                    string current = data.ResourceCurrentFolder(resource, oldGate); plan.OldFolders.Add(current);
                    var ids = resource.AppliesToUnitIds.Count == 0 ? units.Select(u => u.Id).ToList() : resource.AppliesToUnitIds.ToList();
                    if (Directory.Exists(current))
                        foreach (var file in data.GetResourceFiles(resource, oldGate))
                        {
                            var links = resource.StorageMode == "Asset" && resource.FileAssignments.TryGetValue(Path.GetFileName(file), out var assigned) && assigned.Count != 0 ? assigned : ids;
                            bool linked = resource.IsVisible && (resource.StorageMode != "Asset" || resource.FileAssignments.Keys.Any(k => k.Equals(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)));
                            string preserved = resource.StorageMode == "Asset" && !linked ? data.ToRelative(file) : "";
                            candidates.Add(new(file, preserved, gate.Id, group.Key, linked ? canonical.Id : "", links.ToList(), false));
                        }
                    else plan.Warnings.Add($"{gate.AssetDisplay}: source folder not found: {current}");
                    string archive = data.ResourceArchiveFolder(resource, oldGate); plan.OldFolders.Add(archive);
                    if (Directory.Exists(archive)) foreach (var file in Walk(archive, token))
                        candidates.Add(new(file, oldGate.AssetFolder.Length > 0 ? data.ToRelative(file) : $"{root}/Archive/{group.Key}/{AssetFileLayout.SafeName(resource.Id)}/{Path.GetRelativePath(archive, file).Replace('\\', '/')}", gate.Id, group.Key, "", ids, true));
                }
            }
            foreach (var oldUnit in units)
            {
                var unit = plan.Units.Single(u => u.Id == oldUnit.Id); string name = AssetFileLayout.UnitName(unit);
                unit.SystemFolder = root; unit.PhotosFolder = $"{root}/Photos/{name}"; unit.NotesFolder = $"{root}/Notes/{name}";
                foreach (var pair in new[] { (Old: oldUnit.PhotosFolder, New: unit.PhotosFolder, Type: "Photos"), (Old: oldUnit.NotesFolder, New: unit.NotesFolder, Type: "Notes") })
                {
                    if (string.IsNullOrWhiteSpace(pair.Old)) continue;
                    string folder = data.Resolve(pair.Old); plan.OldFolders.Add(folder); plan.OldFolders.Add(Path.Combine(folder, "Archive"));
                    if (!Directory.Exists(folder)) continue;
                    foreach (var file in Walk(folder, token))
                    {
                        string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                        bool history = relative.StartsWith("Archive/", StringComparison.OrdinalIgnoreCase);
                        string target = history ? $"{root}/Archive/{pair.Type}/{name}/{relative[8..]}" : $"{pair.New}/{relative}";
                        candidates.Add(new(file, target, gate.Id, pair.Type, "", [unit.Id], history));
                    }
                }
                if (!string.IsNullOrWhiteSpace(oldUnit.PrimaryPhoto))
                {
                    string photo = data.Resolve(oldUnit.PrimaryPhoto);
                    if (File.Exists(photo) && !candidates.Any(c => c.Source.Equals(photo, StringComparison.OrdinalIgnoreCase) && c.GateId == gate.Id && c.Units.Contains(unit.Id)))
                        candidates.Add(new(photo, $"{unit.PhotosFolder}/{Path.GetFileName(photo)}", gate.Id, "Photos", "", [unit.Id], false));
                }
                unit.DrawingsFolder = root + "/Electrical Drawings"; unit.LightConfigFolder = root + "/Light Configurations"; unit.ScannerConfigFolder = root + "/Scanner Configurations";
            }
            foreach (var resource in oldResources.Where(r => r.GateId == gate.Id && !r.Kind.Equals("ManagedFiles", StringComparison.OrdinalIgnoreCase)))
            {
                if (resource.Kind.Equals("Photos", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var unit in plan.Units.Where(u => u.GateId == gate.Id && resource.AppliesTo(u.Id)))
                    {
                        var photoResource = Clone(resource); photoResource.Id = "asset-photos-" + unit.Id; photoResource.RelativeFolder = unit.PhotosFolder; photoResource.AppliesToUnitIds = [unit.Id];
                        if (!plan.Resources.Any(r => r.Id == photoResource.Id)) plan.Resources.Add(photoResource);
                        if (!plan.ResourceAliases.TryGetValue(resource.Id, out var aliases)) plan.ResourceAliases[resource.Id] = aliases = [];
                        if (!aliases.Contains(photoResource.Id)) aliases.Add(photoResource.Id);
                    }
                }
                else plan.Resources.Add(Clone(resource));
            }
        }
        // Merge byte-identical managed files across old folders and assignments.
        int number = 0;
        foreach (var group in candidates.GroupBy(c => c.ResourceId.Length > 0 ? c.GateId + "|" + c.Category + "|" + Path.GetExtension(c.Source).ToLowerInvariant() + "|" + Hash(c.Source) : c.GateId + "|" + c.Destination + "|" + Hash(c.Source), StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested(); var first = group.First(); var ids = group.SelectMany(c => c.Units).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string scope = string.Join(" + ", plan.Units.Where(u => ids.Contains(u.Id)).OrderBy(u => int.TryParse(u.UnitNumber, out var n) ? n : int.MaxValue).Select(u => u.UnitDisplay));
            string target = first.Destination;
            if (target.Length == 0)
            {
                string scopeName = scope.Length == 0 ? "All units" : scope;
                string original = Path.GetFileName(first.Source);
                // Existing readable files keep their names on repeated organization.
                var oldRoot = oldGates.Single(g => g.Id == first.GateId).AssetFolder;
                string name = oldRoot.Length > 0 && first.Source.StartsWith(data.Resolve(oldRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? original : AssetFileLayout.SafeName(scopeName) + " - " + AssetFileLayout.SafeName(Path.GetFileNameWithoutExtension(original)) + Path.GetExtension(original);
                target = plan.Gates.Single(g => g.Id == first.GateId).AssetFolder + "/" + first.Category + "/" + name;
            }
            plan.Files.Add(new FileEntry { Sources = group.Select(c => c.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Destination = target, GateId = first.GateId, Category = first.Category, ResourceId = first.ResourceId, UnitIds = ids, Historical = first.Historical, Hash = Hash(first.Source), Scope = scope, Number = ++number });
        }
        foreach (var orphan in oldResources.Where(r => !oldGates.Any(g => g.Id == r.GateId))) { plan.Resources.Add(Clone(orphan)); plan.Warnings.Add("Resource has no known asset and was retained unchanged: " + orphan.Label); }
        foreach (var flow in plan.SavedFlows) foreach (var node in flow.Nodes)
            node.ResourceIds = node.ResourceIds.SelectMany(id => plan.ResourceAliases.GetValueOrDefault(id) ?? [id]).Distinct().ToList();
        foreach (var entry in plan.Files) entry.PreviewTarget = entry.Target(plan);
        return plan;
    }

    public static List<string> Validate(DataService data, Plan plan)
    {
        var errors = plan.Errors.ToList();
        if (!Path.GetFullPath(data.SharedRoot).Equals(plan.SharedRoot, StringComparison.OrdinalIgnoreCase)) errors.Add("Shared root changed. Build a new plan.");
        foreach (var gate in plan.Gates) if (!Beneath(data.Resolve(gate.AssetFolder), data.Resolve("Assets"))) errors.Add("Asset folder must remain under Assets: " + gate.AssetFolder);
        foreach (var group in plan.Files.GroupBy(f => f.Target(plan), StringComparer.OrdinalIgnoreCase))
            if (group.Select(f => f.Hash).Distinct().Count() > 1) errors.Add("Different contents target the same file: " + group.Key + ". Archive the older copy or keep a separate sheet.");
        foreach (var file in plan.Files)
        {
            string target = data.Resolve(file.Target(plan));
            if (!Beneath(target, data.Resolve("Assets"))) { errors.Add("Target must remain under Assets: " + target); continue; }
            if (File.Exists(target) && Hash(target) != file.Hash) errors.Add("Destination already contains a different file: " + target);
        }
        return errors.Distinct().ToList();
    }

    public static string Apply(DataService data, Plan plan, CancellationToken token = default)
    {
        using var lease = data.AcquireAssetWriteLock();
        var errors = Validate(data, plan); if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        string backup = Path.Combine(data.SharedRoot, "_AppData", "Storage Backups", plan.Stamp + "-" + Guid.NewGuid().ToString("N")[..6]);
        CheckReparseParents(Path.Combine(backup, "check"), data.SharedRoot); Directory.CreateDirectory(backup);
        using var migrationLock = new FileStream(Path.Combine(data.SharedRoot, "_AppData", "storage-migration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        CheckMetadata(plan);
        foreach (var file in plan.Files)
        {
            token.ThrowIfCancellationRequested();
            foreach (string source in file.Sources) if (!File.Exists(source) || Hash(source) != file.Hash) throw new IOException("Source changed since preview: " + source + ". Build a new plan.");
            string target = data.Resolve(file.Target(plan)); CheckReparseParents(target, data.SharedRoot);
            if (!File.Exists(target)) DataService.AtomicCopy(file.Sources[0], target);
            if (Hash(target) != file.Hash) throw new IOException("Copied bytes did not match the reviewed source: " + target);
        }
        CheckMetadata(plan); token.ThrowIfCancellationRequested();
        foreach (var resource in plan.Resources.Where(r => r.StorageMode == "Asset"))
            resource.FileAssignments = plan.Files.Where(f => f.ResourceId == resource.Id && !f.Historical && f.Decision != Disposition.Archive)
                .GroupBy(f => Path.GetFileName(f.Target(plan)), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.SelectMany(f => f.UnitIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
        var oldRecords = data.LoadResources(); var oldGates = data.LoadGates();
        foreach (var resource in plan.Resources.Where(r => r.StorageCategory == "Electrical Drawings"))
        {
            foreach (var oldRecord in oldRecords.Where(r => r.GateId == resource.GateId))
            {
                string sourceFolder = data.ResourceCurrentFolder(oldRecord, oldGates.Single(g => g.Id == oldRecord.GateId));
                foreach (var pair in oldRecord.DrawingPairs)
                {
                    var pdf = plan.Files.FirstOrDefault(f => f.ResourceId == resource.Id && !f.Historical && f.Decision != Disposition.Archive && f.Sources.Contains(Path.Combine(sourceFolder, pair.Pdf), StringComparer.OrdinalIgnoreCase));
                    var dwg = plan.Files.FirstOrDefault(f => f.ResourceId == resource.Id && !f.Historical && f.Decision != Disposition.Archive && f.Sources.Contains(Path.Combine(sourceFolder, pair.Dwg), StringComparer.OrdinalIgnoreCase));
                    if (pdf is null || dwg is null) continue;
                    var retained = Clone(pair); retained.Pdf = Path.GetFileName(pdf.Target(plan)); retained.Dwg = Path.GetFileName(dwg.Target(plan));
                    if (!resource.DrawingPairs.Any(p => p.Contains(retained.Pdf) || p.Contains(retained.Dwg))) resource.DrawingPairs.Add(retained);
                }
            }
            foreach (string pdf in resource.FileAssignments.Keys.Where(n => Path.GetExtension(n).Equals(".pdf", StringComparison.OrdinalIgnoreCase)))
            {
                if (resource.DrawingPairs.Any(p => p.Contains(pdf))) continue;
                string? dwg = resource.FileAssignments.Keys.FirstOrDefault(n => Path.GetExtension(n).Equals(".dwg", StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(n).Equals(Path.GetFileNameWithoutExtension(pdf), StringComparison.OrdinalIgnoreCase));
                if (dwg is null || !resource.FileAssignments[pdf].Order().SequenceEqual(resource.FileAssignments[dwg].Order())) continue;
                string folder = data.ResourceCurrentFolder(resource, plan.Gates.Single(g => g.Id == resource.GateId));
                resource.DrawingPairs.Add(new DrawingFilePair { Pdf = pdf, Dwg = dwg, PdfHash = Hash(Path.Combine(folder, pdf)), DwgHash = Hash(Path.Combine(folder, dwg)) });
            }
        }
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in plan.Files)
        {
            var winner = file.Decision == Disposition.Archive && !file.Historical ? plan.Files.FirstOrDefault(f => f.Destination.Equals(file.Destination, StringComparison.OrdinalIgnoreCase) && f.Decision != Disposition.Archive) : null;
            foreach (string source in file.Sources) mapping.TryAdd(source, data.ToRelative(data.Resolve((winner ?? file).Target(plan))));
        }
        foreach (var unit in plan.Units)
        {
            if (string.IsNullOrWhiteSpace(unit.PrimaryPhoto)) continue;
            string source = data.Resolve(unit.PrimaryPhoto);
            var photo = plan.Files.FirstOrDefault(f => f.Category == "Photos" && f.UnitIds.Contains(unit.Id) && f.Sources.Contains(source, StringComparer.OrdinalIgnoreCase));
            if (photo is not null) unit.PrimaryPhoto = photo.Target(plan);
        }
        foreach (var manual in plan.Manuals) if (!string.IsNullOrWhiteSpace(manual.FilePath) && mapping.TryGetValue(data.Resolve(manual.FilePath), out var target)) manual.FilePath = target;
        foreach (var part in plan.Parts) if (!string.IsNullOrWhiteSpace(part.DocumentPath) && mapping.TryGetValue(data.Resolve(part.DocumentPath), out var target)) part.DocumentPath = target;
        var snapshots = MetadataPaths(data).Where(File.Exists).ToDictionary(p => p, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.Combine(backup, "Metadata"));
        foreach (var pair in snapshots) File.WriteAllBytes(Path.Combine(backup, "Metadata", Path.GetFileName(pair.Key)), pair.Value);
        File.WriteAllText(Path.Combine(backup, "plan.json"), JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            data.SaveGates(plan.Gates); data.SaveSystems(plan.Units); data.SaveResources(plan.Resources); data.SaveManualRecords(plan.Manuals); data.SaveParts(plan.Parts); data.SaveTroubleshootingFlows(plan.SavedFlows);
        }
        catch
        {
            foreach (var pair in snapshots) File.WriteAllBytes(pair.Key, pair.Value);
            throw;
        }
        var notices = new List<string>();
        foreach (var gate in plan.Gates) { string notice = data.WriteAssetFileIndex(gate); if (notice.Length > 0) notices.Add(notice); }
        foreach (var unit in plan.Units.Where(u => plan.Gates.Any(g => g.Id == u.GateId))) { Directory.CreateDirectory(data.Resolve(unit.PhotosFolder)); Directory.CreateDirectory(data.Resolve(unit.NotesFolder)); }
        // Old managed copies become recovery material, not another active file tree.
        var currentPaths = plan.Files.Select(f => data.Resolve(f.Target(plan))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referenced = plan.Manuals.Select(m => data.Resolve(m.FilePath)).Concat(plan.Parts.Select(p => data.Resolve(p.DocumentPath))).Concat(plan.Units.Where(u => !string.IsNullOrWhiteSpace(u.PrimaryPhoto)).Select(u => data.Resolve(u.PrimaryPhoto))).Concat(plan.Units.Select(data.SystemNotesPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string source in plan.Files.SelectMany(f => f.Sources).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (currentPaths.Contains(source) || referenced.Contains(source) || !Beneath(source, data.SharedRoot) || Beneath(source, Path.Combine(data.SharedRoot, "Library", "Imports"))) continue;
            try
            {
                string destination = Path.Combine(backup, "Original files", Path.GetRelativePath(data.SharedRoot, source)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(source, destination); PruneEmpty(Path.GetDirectoryName(source)!, data.SharedRoot);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { notices.Add("Retained old recovery copy: " + source + " — " + error.Message); }
        }
        foreach (string folder in plan.OldFolders.Distinct(StringComparer.OrdinalIgnoreCase)) if (!Beneath(folder, data.Resolve("Assets")) && Beneath(folder, data.SharedRoot)) { try { PruneEmpty(folder, data.SharedRoot); } catch (IOException) { } }
        string report = Path.Combine(backup, "Report.txt");
        File.WriteAllText(report, plan.Summary + "\nNew active files are under Assets. Metadata and original files in this backup are recovery copies.\n" + string.Join("\n", notices));
        data.LogAudit("Asset folders organized", details: report); return report;
    }
    public static string Hash(string path) { using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(SHA256.HashData(input)); }
    private static void CheckMetadata(Plan plan) { foreach (var pair in plan.MetadataHashes) if ((File.Exists(pair.Key) ? Hash(pair.Key) : "missing") != pair.Value) throw new IOException("Shared records changed since preview. Close other app copies and build a new plan."); }
    private static string[] MetadataPaths(DataService d) => [d.GatesFile, d.SystemsFile, d.ResourcesFile, d.ManualsIndexFile, d.PartsFile, d.TroubleshootingFile];
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static bool Beneath(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<string> Walk(string directory, CancellationToken token, int depth = 0)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 64) throw new IOException("Folder nesting exceeds the supported migration depth: " + directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked folders must be reviewed separately: " + directory);
        foreach (string file in Directory.EnumerateFiles(directory)) { token.ThrowIfCancellationRequested(); if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked file cannot be migrated: " + file); yield return file; }
        foreach (string child in Directory.EnumerateDirectories(directory)) foreach (string file in Walk(child, token, depth + 1)) yield return file;
    }
    private static void CheckReparseParents(string path, string root)
    {
        for (string? dir = Path.GetDirectoryName(path); dir is not null && Beneath(dir, root); dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new IOException("Destination includes a linked folder: " + dir);
    }
    private static void PruneEmpty(string directory, string root)
    {
        while (Beneath(directory, root) && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) { Directory.Delete(directory); directory = Path.GetDirectoryName(directory)!; }
    }
}
