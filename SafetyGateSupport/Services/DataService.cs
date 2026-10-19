using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

public class DataService
{
    private readonly JsonSerializerOptions _read = new() { PropertyNameCaseInsensitive = true };
    private readonly JsonSerializerOptions _write = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly string _localSettingsPath;
    private AppSettings _settings;

    public string SharedRoot => _settings.SharedRoot;
    public string SystemsFile => Path.Combine(SharedRoot, "Data", "systems.json"); // unit records
    public string GatesFile => Path.Combine(SharedRoot, "Data", "gates.json");
    public string ResourcesFile => Path.Combine(SharedRoot, "Data", "resources.json");
    public string CategoriesFile => Path.Combine(SharedRoot, "Data", "categories.json");
    public string HelpFile => Path.Combine(SharedRoot, "Data", "help.json");
    public string PartsFile => Path.Combine(SharedRoot, "Data", "parts.json");
    public string ManualsIndexFile => Path.Combine(SharedRoot, "Data", "manuals.json");
    public string TroubleshootingFile => Path.Combine(SharedRoot, "Data", "troubleshooting.json");
    public string SharedContentFile => Path.Combine(SharedRoot, "Data", "shared-content.json");
    public string AuditFile => Path.Combine(SharedRoot, "Data", "audit-log.jsonl");
    public string ManualLibraryFolder => Path.Combine(SharedRoot, "Library", "Manuals");
    public string PartsLibraryFolder => Path.Combine(SharedRoot, "Library", "Parts");
    public bool IsReady => File.Exists(SystemsFile) && File.Exists(CategoriesFile);
    public AppSettings Settings => _settings;

    public DataService(bool initializeSharedDefaults = true, string? localSettingsDirectory = null)
    {
        var localDir = localSettingsDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SafetyGateSupport");
        Directory.CreateDirectory(localDir);
        _localSettingsPath = Path.Combine(localDir, "settings.json");
        _settings = LoadLocalSettings();
        if (initializeSharedDefaults && IsReady) EnsureSharedDefaults();
    }

    private AppSettings LoadLocalSettings()
    {
        try
        {
            if (File.Exists(_localSettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_localSettingsPath), _read) ?? new();
        }
        catch { }
        return new AppSettings();
    }

    public void SaveLocalSettings() => File.WriteAllText(_localSettingsPath, JsonSerializer.Serialize(_settings, _write));

    public void SetSharedRoot(string path)
    {
        _settings.SharedRoot = Path.GetFullPath(path.Trim());
        SaveLocalSettings();
        if (IsReady) EnsureSharedDefaults();
    }

    public void SetSoftwarePaths(string siemens, string uam)
    {
        _settings.SiemensExePath = siemens.Trim();
        _settings.UamExePath = uam.Trim();
        SaveLocalSettings();
    }

    public void InitializeSharedData()
    {
        Directory.CreateDirectory(SharedRoot);
        var asm = Assembly.GetExecutingAssembly();
        var resource = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("SeedData.zip", StringComparison.OrdinalIgnoreCase));
        if (resource is null) throw new InvalidOperationException("Starter data is missing from the application.");
        using var stream = asm.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("Could not read starter data.");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            var target = Path.Combine(SharedRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) entry.ExtractToFile(target, false);
        }
        EnsureSharedDefaults();
    }

    public void EnsureSharedDefaults()
    {
        Directory.CreateDirectory(Path.Combine(SharedRoot, "Data"));
        Directory.CreateDirectory(ManualLibraryFolder);
        Directory.CreateDirectory(Path.Combine(ManualLibraryFolder, "Archive"));
        Directory.CreateDirectory(PartsLibraryFolder);
        Directory.CreateDirectory(Path.Combine(PartsLibraryFolder, "Documents"));
        Directory.CreateDirectory(Path.Combine(PartsLibraryFolder, "Archive"));

        if (!File.Exists(HelpFile)) SaveHelpItems(DefaultHelpItems());
        if (!File.Exists(PartsFile)) SaveParts(new List<PartRecord>());
        if (!File.Exists(SharedContentFile)) SaveSharedContent(new SharedContent());

        MigrateGateHierarchyIfNeeded();
        MigrateResourcesIfNeeded();
        MigrateManualIndexIfNeeded();
        MigrateTroubleshootingIfNeeded();
    }

    private void MigrateGateHierarchyIfNeeded()
    {
        if (File.Exists(GatesFile))
        {
            var systemsExisting = LoadList<SafetySystem>(SystemsFile);
            var gatesExisting = LoadList<SafetyGate>(GatesFile);
            var changed = false;
            var gatesChanged = false;
            foreach (var unit in systemsExisting.Where(x => string.IsNullOrWhiteSpace(x.GateId)))
            {
                var gate = FindLegacyGate(gatesExisting, unit);
                if (gate is null)
                {
                    gate = GateFromLegacyUnit(unit);
                    gatesExisting.Add(gate);
                    gatesChanged = true;
                }
                unit.GateId = gate.Id;
                changed = true;
            }
            if (changed)
            {
                // Persist a newly discovered gate before storing any unit that refers to it.
                if (gatesChanged) SaveJsonWithBackup(GatesFile, gatesExisting);
                SaveJsonWithBackup(SystemsFile, systemsExisting);
            }
            return;
        }

        var systems = LoadList<SafetySystem>(SystemsFile);
        var gates = new List<SafetyGate>();
        foreach (var unit in systems)
        {
            var gate = FindLegacyGate(gates, unit);
            if (gate is null)
            {
                gate = GateFromLegacyUnit(unit);
                gates.Add(gate);
            }
            unit.GateId = gate.Id;
        }
        SaveJsonWithBackup(GatesFile, gates);
        SaveJsonWithBackup(SystemsFile, systems);
        LogAudit("Migration", details: $"Created gate hierarchy with {gates.Count} gates and {systems.Count} units from legacy systems.json.");
    }

    private static SafetyGate? FindLegacyGate(IEnumerable<SafetyGate> gates, SafetySystem unit)
    {
        return gates.FirstOrDefault(g =>
            string.Equals(g.Category, unit.Category, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(g.AssetNumber ?? "", unit.AssetNumber ?? "", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(g.Manufacturer, unit.GateBrand, StringComparison.OrdinalIgnoreCase));
    }

    private static SafetyGate GateFromLegacyUnit(SafetySystem unit) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Category = string.IsNullOrWhiteSpace(unit.Category) ? "906 / Outside" : unit.Category,
        AssetNumber = unit.AssetNumber ?? "",
        Manufacturer = string.IsNullOrWhiteSpace(unit.GateBrand) ? "Safe2Cross" : unit.GateBrand,
        EquipmentType = unit.EquipmentType
    };

    private void MigrateResourcesIfNeeded()
    {
        if (File.Exists(ResourcesFile)) return;
        var resources = new List<ResourceDefinition>();
        foreach (var unit in LoadSystemsInternal())
        {
            AddLegacyResource(resources, unit, "Electrical Drawings", "ManagedFiles", unit.DrawingsFolder, unit.ShowElectricalDrawings, true, "SameExtension", "None", 10, "drawing electrical wiring schematic print");
            AddLegacyResource(resources, unit, "Light Configuration", "ManagedFiles", unit.LightConfigFolder, unit.ShowLightConfiguration, false, "All", "Siemens", 20, "light signal tower 8wd46 configuration");
            AddLegacyResource(resources, unit, "Scanner Configuration", "ManagedFiles", unit.ScannerConfigFolder, unit.ShowScannerConfiguration, false, "All", "UAM", 30, "scanner safety laser uam configuration");
            AddLegacyResource(resources, unit, "Manuals", "Manuals", "", unit.ShowManuals, false, "None", "None", 40, "manual documentation instructions");
            AddLegacyResource(resources, unit, "Parts / BOM", "Parts", "", unit.ShowPartsBom, false, "None", "None", 50, "parts bom bill materials inventory");
            AddLegacyResource(resources, unit, "Photos", "Photos", unit.PhotosFolder, unit.ShowPhotos, false, "None", "None", 60, "photos images pictures");
        }
        SaveJsonWithBackup(ResourcesFile, resources);
        LogAudit("Migration", details: $"Created {resources.Count} resource-button records from legacy visibility settings.");
    }

    private static void AddLegacyResource(List<ResourceDefinition> list, SafetySystem unit, string label, string kind, string folder, bool visible, bool upload, string archive, string launch, int sort, string keywords)
    {
        list.Add(new ResourceDefinition
        {
            Id = Guid.NewGuid().ToString("N"), GateId = unit.GateId, Label = label, Kind = kind,
            RelativeFolder = folder, AppliesToUnitIds = new() { unit.Id }, IsVisible = visible,
            AllowUserUpload = upload, ArchiveMode = archive, LaunchAction = launch, SortOrder = sort, Keywords = keywords
        });
    }

    private void MigrateManualIndexIfNeeded()
    {
        if (File.Exists(ManualsIndexFile)) return;
        var records = GetManualFiles().Select(f => new ManualRecord
        {
            Id = Guid.NewGuid().ToString("N"), Title = Path.GetFileNameWithoutExtension(f), FilePath = ToRelative(f)
        }).ToList();
        SaveJsonWithBackup(ManualsIndexFile, records);
    }

    private void MigrateTroubleshootingIfNeeded()
    {
        if (File.Exists(TroubleshootingFile)) return;
        var flows = LoadHelpItems().Where(h => h.Section.Equals("Troubleshooting", StringComparison.OrdinalIgnoreCase)).Select(h =>
        {
            var node = new TroubleshootingNode { Id = Guid.NewGuid().ToString("N"), Prompt = h.Title, Body = h.Body, IsTerminal = true };
            return new TroubleshootingFlow
            {
                Id = Guid.NewGuid().ToString("N"), Title = h.Title, Keywords = h.Keywords,
                StartNodeId = node.Id, Nodes = new() { node }
            };
        }).ToList();
        SaveJsonWithBackup(TroubleshootingFile, flows);
    }

    public List<SafetyGate> LoadGates() => LoadList<SafetyGate>(GatesFile);
    public List<SafetySystem> LoadSystems(IEnumerable<SafetyGate>? knownGates = null)
    {
        var systems = LoadSystemsInternal();
        var gates = knownGates?.ToList() ?? LoadGates();
        SynchronizeLegacyFields(gates, systems);
        return systems;
    }
    private List<SafetySystem> LoadSystemsInternal() => LoadList<SafetySystem>(SystemsFile);
    public List<ResourceDefinition> LoadResources() => LoadList<ResourceDefinition>(ResourcesFile);
    public List<string> LoadCategories() => LoadList<string>(CategoriesFile);
    public List<HelpItem> LoadHelpItems() => LoadList<HelpItem>(HelpFile);
    public List<PartRecord> LoadParts() => LoadList<PartRecord>(PartsFile);
    public List<ManualRecord> LoadManualRecords() => LoadList<ManualRecord>(ManualsIndexFile);
    public List<TroubleshootingFlow> LoadTroubleshootingFlows() => LoadList<TroubleshootingFlow>(TroubleshootingFile);

    public SharedContent LoadSharedContent()
    {
        if (!File.Exists(SharedContentFile)) return new SharedContent();
        try { return JsonSerializer.Deserialize<SharedContent>(File.ReadAllText(SharedContentFile), _read) ?? new SharedContent(); }
        catch { return new SharedContent(); }
    }

    private List<T> LoadList<T>(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), _read) ?? new(); }
        catch { return new(); }
    }

    public void SynchronizeLegacyFields(List<SafetyGate> gates, List<SafetySystem> units)
    {
        var byId = gates.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var u in units)
        {
            if (!byId.TryGetValue(u.GateId, out var g)) continue;
            u.Category = g.Category;
            u.AssetNumber = string.IsNullOrWhiteSpace(g.AssetNumber) ? null : g.AssetNumber;
            u.GateBrand = g.Manufacturer;
            u.EquipmentType = g.EquipmentType;
            u.DisplayName = $"{g.Display} - {u.UnitDisplay}";
        }
    }

    public SafetyGate? GateFor(SafetySystem unit, IEnumerable<SafetyGate>? gates = null) =>
        (gates ?? LoadGates()).FirstOrDefault(g => g.Id.Equals(unit.GateId, StringComparison.OrdinalIgnoreCase));

    public void SaveGates(List<SafetyGate> gates)
    {
        SaveJsonWithBackup(GatesFile, gates.OrderBy(g => g.Category).ThenBy(g => SortNumber(g.AssetNumber)).ThenBy(g => g.Manufacturer).ToList());
        var units = LoadSystemsInternal();
        SynchronizeLegacyFields(gates, units);
        SaveJsonWithBackup(SystemsFile, units);
    }
    public void SaveSystems(List<SafetySystem> systems) => SaveJsonWithBackup(SystemsFile, systems);
    public void SaveResources(List<ResourceDefinition> resources) => SaveJsonWithBackup(ResourcesFile, resources.OrderBy(r => r.GateId).ThenBy(r => r.SortOrder).ThenBy(r => r.Label).ToList());
    public void SaveCategories(List<string> categories) => SaveJsonWithBackup(CategoriesFile, categories);
    public void SaveHelpItems(List<HelpItem> items) => SaveJsonWithBackup(HelpFile, items.OrderBy(i => i.Section).ThenBy(i => i.SortOrder).ThenBy(i => i.Title).ToList());
    public void SaveParts(List<PartRecord> parts) => SaveJsonWithBackup(PartsFile, parts.OrderBy(p => p.PartNumber).ThenBy(p => p.Description).ToList());
    public void SaveManualRecords(List<ManualRecord> manuals) => SaveJsonWithBackup(ManualsIndexFile, manuals.OrderBy(m => m.Title).ToList());
    public void SaveTroubleshootingFlows(List<TroubleshootingFlow> flows) => SaveJsonWithBackup(TroubleshootingFile, flows.OrderBy(f => f.Title).ToList());
    public void SaveSharedContent(SharedContent content) => SaveJsonWithBackup(SharedContentFile, content);

    private static int SortNumber(string? value) => int.TryParse(value, out var n) ? n : int.MaxValue;

    private void SaveJsonWithBackup<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var backups = Path.Combine(Path.GetDirectoryName(path)!, "Backups");
        Directory.CreateDirectory(backups);
        if (File.Exists(path))
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            File.Copy(path, Path.Combine(backups, Path.GetFileNameWithoutExtension(path) + "_" + stamp + Path.GetExtension(path)), true);
        }
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, _write));
        File.Move(temp, path, true);
    }

    public string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return SharedRoot;
        if (Path.IsPathRooted(relativePath)) return Path.GetFullPath(relativePath);
        return Path.GetFullPath(Path.Combine(SharedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    public string ToRelative(string fullPath) => Path.GetRelativePath(SharedRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    public string GateRoot(SafetyGate gate) => $"Gates/{gate.Id}";
    public string UnitRoot(SafetyGate gate, SafetySystem unit) => $"{GateRoot(gate)}/Units/{unit.Id}";

    public void EnsureUnitFolders(SafetyGate gate, SafetySystem unit)
    {
        if (string.IsNullOrWhiteSpace(unit.SystemFolder)) unit.SystemFolder = UnitRoot(gate, unit);
        if (string.IsNullOrWhiteSpace(unit.PhotosFolder)) unit.PhotosFolder = unit.SystemFolder + "/Photos";
        if (string.IsNullOrWhiteSpace(unit.NotesFolder)) unit.NotesFolder = unit.SystemFolder + "/Notes";
        foreach (var relative in new[] { unit.SystemFolder, unit.PhotosFolder, unit.PhotosFolder + "/Archive", unit.NotesFolder })
            Directory.CreateDirectory(Resolve(relative));
    }

    public void EnsureSystemFolders(SafetySystem s)
    {
        var gate = GateFor(s) ?? new SafetyGate { Id = string.IsNullOrWhiteSpace(s.GateId) ? Guid.NewGuid().ToString("N") : s.GateId, Category = s.Category, AssetNumber = s.AssetNumber ?? "", Manufacturer = s.GateBrand };
        EnsureUnitFolders(gate, s);
        foreach (var relative in new[]
        {
            s.DrawingsFolder + "/Current", s.DrawingsFolder + "/Archive",
            s.LightConfigFolder + "/Current", s.LightConfigFolder + "/Archive",
            s.ScannerConfigFolder + "/Current", s.ScannerConfigFolder + "/Archive",
            s.ManualsFolder + "/Current", s.ManualsFolder + "/Archive",
            s.PartsFolder + "/Current", s.PartsFolder + "/Archive"
        }.Where(x => !string.IsNullOrWhiteSpace(x))) Directory.CreateDirectory(Resolve(relative));
    }

    public List<ResourceDefinition> ResourcesFor(SafetyGate gate, SafetySystem unit, IEnumerable<ResourceDefinition>? resources = null) =>
        (resources ?? LoadResources()).Where(r => r.IsVisible && r.GateId.Equals(gate.Id, StringComparison.OrdinalIgnoreCase) && r.AppliesTo(unit.Id))
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Label).ToList();

    public string ResourceBaseFolder(ResourceDefinition resource, SafetyGate gate)
    {
        if (!string.IsNullOrWhiteSpace(resource.RelativeFolder)) return Resolve(resource.RelativeFolder);
        var relative = $"{GateRoot(gate)}/Resources/{resource.Id}";
        resource.RelativeFolder = relative;
        return Resolve(relative);
    }

    public string ResourceCurrentFolder(ResourceDefinition resource, SafetyGate gate)
    {
        var baseFolder = ResourceBaseFolder(resource, gate);
        if (resource.Kind.Equals("ManagedFiles", StringComparison.OrdinalIgnoreCase)) return Path.Combine(baseFolder, "Current");
        return baseFolder;
    }

    public string ResourceArchiveFolder(ResourceDefinition resource, SafetyGate gate) => Path.Combine(ResourceBaseFolder(resource, gate), "Archive");

    public void EnsureResourceFolders(ResourceDefinition resource, SafetyGate gate)
    {
        var baseFolder = ResourceBaseFolder(resource, gate);
        Directory.CreateDirectory(baseFolder);
        if (resource.Kind.Equals("ManagedFiles", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.Combine(baseFolder, "Current"));
            Directory.CreateDirectory(Path.Combine(baseFolder, "Archive"));
        }
    }

    public string AddOrUpdateResource(ResourceDefinition resource, SafetyGate gate, SafetySystem? unit, string sourceFile)
    {
        EnsureResourceFolders(resource, gate);
        var current = ResourceCurrentFolder(resource, gate);
        var archive = ResourceArchiveFolder(resource, gate);
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(archive);
        var existing = Directory.GetFiles(current);
        IEnumerable<string> toArchive = resource.ArchiveMode switch
        {
            "All" => existing,
            "SameName" => existing.Where(f => Path.GetFileName(f).Equals(Path.GetFileName(sourceFile), StringComparison.OrdinalIgnoreCase)),
            "None" => Enumerable.Empty<string>(),
            _ => existing.Where(f => Path.GetExtension(f).Equals(Path.GetExtension(sourceFile), StringComparison.OrdinalIgnoreCase))
        };
        foreach (var old in toArchive.ToList()) ArchiveFile(old, archive);
        var dest = Path.Combine(current, Path.GetFileName(sourceFile));
        if (File.Exists(dest)) ArchiveFile(dest, archive);
        File.Copy(sourceFile, dest, true);
        LogAudit("Resource updated", gate.Id, unit?.Id ?? "", resource.Id, $"{resource.Label}: {Path.GetFileName(sourceFile)}");
        return dest;
    }

    public List<string> AddOrUpdateResourceFiles(ResourceDefinition resource, SafetyGate gate, SafetySystem? unit, IEnumerable<string> sourceFiles)
    {
        var saved = new List<string>();
        foreach (var source in sourceFiles) saved.Add(AddOrUpdateResource(resource, gate, unit, source));
        return saved;
    }

    // Legacy wrappers retained so migrated content remains manageable.
    public string CurrentFolder(SafetySystem s, string resourceType)
    {
        var gate = GateFor(s);
        var resource = gate is null ? null : LoadResources().FirstOrDefault(r => r.GateId == gate.Id && r.AppliesTo(s.Id) && r.Label.Equals(resourceType.Replace("User Manuals", "Manuals"), StringComparison.OrdinalIgnoreCase) && r.Kind == "ManagedFiles");
        if (resource is not null) return ResourceCurrentFolder(resource, gate!);
        return resourceType switch
        {
            "Electrical Drawings" => Resolve(s.DrawingsFolder + "/Current"),
            "Light Configuration" => Resolve(s.LightConfigFolder + "/Current"),
            "Scanner Configuration" => Resolve(s.ScannerConfigFolder + "/Current"),
            "User Manuals" => Resolve(s.ManualsFolder + "/Current"),
            "Parts / BOM" => Resolve(s.PartsFolder + "/Current"),
            _ => Resolve(s.SystemFolder)
        };
    }

    public string ArchiveFolder(SafetySystem s, string resourceType)
    {
        var gate = GateFor(s);
        var resource = gate is null ? null : LoadResources().FirstOrDefault(r => r.GateId == gate.Id && r.AppliesTo(s.Id) && r.Label.Equals(resourceType.Replace("User Manuals", "Manuals"), StringComparison.OrdinalIgnoreCase) && r.Kind == "ManagedFiles");
        if (resource is not null) return ResourceArchiveFolder(resource, gate!);
        return resourceType switch
        {
            "Electrical Drawings" => Resolve(s.DrawingsFolder + "/Archive"),
            "Light Configuration" => Resolve(s.LightConfigFolder + "/Archive"),
            "Scanner Configuration" => Resolve(s.ScannerConfigFolder + "/Archive"),
            "User Manuals" => Resolve(s.ManualsFolder + "/Archive"),
            "Parts / BOM" => Resolve(s.PartsFolder + "/Archive"),
            _ => Resolve(s.SystemFolder)
        };
    }

    public string AddOrUpdateResource(SafetySystem s, string resourceType, string sourceFile)
    {
        var gate = GateFor(s) ?? throw new InvalidOperationException("Gate record could not be found.");
        var label = resourceType.Replace("User Manuals", "Manuals");
        var resource = LoadResources().FirstOrDefault(r => r.GateId == gate.Id && r.AppliesTo(s.Id) && r.Label.Equals(label, StringComparison.OrdinalIgnoreCase) && r.Kind == "ManagedFiles")
            ?? throw new InvalidOperationException($"No managed resource button named '{label}' is assigned to this unit.");
        return AddOrUpdateResource(resource, gate, s, sourceFile);
    }

    public List<string> AddOrUpdateDrawings(SafetySystem s, IEnumerable<string> sourceFiles)
    {
        var gate = GateFor(s) ?? throw new InvalidOperationException("Gate record could not be found.");
        var resource = LoadResources().FirstOrDefault(r => r.GateId == gate.Id && r.AppliesTo(s.Id) && r.Label.Equals("Electrical Drawings", StringComparison.OrdinalIgnoreCase) && r.Kind == "ManagedFiles")
            ?? throw new InvalidOperationException("No Electrical Drawings resource is assigned to this unit.");
        return AddOrUpdateResourceFiles(resource, gate, s, sourceFiles);
    }

    public string SystemNotesPath(SafetySystem s) => Resolve(s.NotesFolder + "/shared-notes.txt");

    public string LoadSystemNotes(SafetySystem s)
    {
        var path = SystemNotesPath(s);
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public DateTime GetSystemNotesLastWriteUtc(SafetySystem s)
    {
        var path = SystemNotesPath(s);
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }

    public void SaveSystemNotes(SafetySystem s, string text)
    {
        var gate = GateFor(s);
        if (gate is not null) EnsureUnitFolders(gate, s); else EnsureSystemFolders(s);
        var path = SystemNotesPath(s);
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $".shared-notes-{Environment.UserName}-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, text ?? "");
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void SetIdentificationPhoto(SafetySystem s, string sourceFile)
    {
        var gate = GateFor(s) ?? throw new InvalidOperationException("Gate record could not be found.");
        EnsureUnitFolders(gate, s);
        var photoFolder = Resolve(s.PhotosFolder);
        var archiveFolder = Path.Combine(photoFolder, "Archive");
        Directory.CreateDirectory(archiveFolder);
        // Read the source before archiving. This also makes choosing the currently saved
        // identification image safe, because the source may itself live in this folder.
        var imageBytes = File.ReadAllBytes(sourceFile);
        foreach (var old in Directory.GetFiles(photoFolder, "Identification*.*")) ArchiveFile(old, archiveFolder);
        var ext = Path.GetExtension(sourceFile);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".png";
        // Give every saved identification image a new path. Besides preserving a clean
        // revision trail, this prevents WPF/Windows URI image caches from showing the
        // previous picture after a replacement.
        var dest = Path.Combine(photoFolder, $"Identification_{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext}");
        File.WriteAllBytes(dest, imageBytes);
        s.PrimaryPhoto = ToRelative(dest);
        LogAudit("Identification photo updated", gate.Id, s.Id, details: Path.GetFileName(sourceFile));
    }

    public string AddOrUpdateManual(string sourceFile)
    {
        EnsureSharedDefaults();
        var dest = Path.Combine(ManualLibraryFolder, Path.GetFileName(sourceFile));
        if (File.Exists(dest)) ArchiveFile(dest, Path.Combine(ManualLibraryFolder, "Archive"));
        File.Copy(sourceFile, dest, true);
        return dest;
    }

    public string AddOrUpdatePartDocument(string sourceFile)
    {
        EnsureSharedDefaults();
        var folder = Path.Combine(PartsLibraryFolder, "Documents");
        var dest = Path.Combine(folder, Path.GetFileName(sourceFile));
        if (File.Exists(dest)) ArchiveFile(dest, Path.Combine(PartsLibraryFolder, "Archive"));
        File.Copy(sourceFile, dest, true);
        return dest;
    }

    public List<string> GetManualFiles()
    {
        if (!Directory.Exists(ManualLibraryFolder)) return new();
        return Directory.GetFiles(ManualLibraryFolder, "*", SearchOption.TopDirectoryOnly)
            .Where(f => !string.Equals(Path.GetFileName(f), "desktop.ini", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f)).ToList();
    }

    public IEnumerable<(SafetySystem System, string Resource, string File)> GetCurrentSystemFiles(
        IEnumerable<SafetySystem>? systems = null, IEnumerable<SafetyGate>? knownGates = null,
        IEnumerable<ResourceDefinition>? knownResources = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var units = (systems ?? LoadSystems()).ToList();
        var gates = (knownGates ?? LoadGates()).ToLookup(g => g.Id, StringComparer.Ordinal);
        var resources = (knownResources ?? LoadResources()).ToLookup(r => r.GateId, StringComparer.OrdinalIgnoreCase);
        var filesByFolder = new Dictionary<string, string[]>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var unit in units)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gate = gates[unit.GateId].FirstOrDefault();
            if (gate is null) continue;
            foreach (var resource in ResourcesFor(gate, unit, resources[gate.Id]).Where(r => r.Kind.Equals("ManagedFiles", StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Resolve from the snapshot without mutating a resource shared with the UI.
                var relative = string.IsNullOrWhiteSpace(resource.RelativeFolder) ? $"{GateRoot(gate)}/Resources/{resource.Id}" : resource.RelativeFolder;
                var folder = Path.Combine(Resolve(relative), "Current");
                if (!filesByFolder.TryGetValue(folder, out var files))
                {
                    try { files = Directory.GetFiles(folder); }
                    catch (IOException) { files = Array.Empty<string>(); }
                    catch (UnauthorizedAccessException) { files = Array.Empty<string>(); }
                    filesByFolder[folder] = files;
                }
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return (unit, resource.Label, file);
                }
            }
        }
    }

    public List<ManualRecord> ManualsFor(SafetyGate gate, SafetySystem unit) => LoadManualRecords().Where(m => m.AppliesTo(gate.Id, unit.Id)).OrderBy(m => m.Title).ToList();
    public List<PartRecord> PartsFor(SafetyGate gate, SafetySystem unit) => LoadParts().Where(p => p.AppliesTo(gate.Id, unit.Id)).OrderBy(p => p.PartNumber).ThenBy(p => p.Description).ToList();

    private static void ArchiveFile(string oldPath, string archiveFolder)
    {
        if (!File.Exists(oldPath)) return;
        Directory.CreateDirectory(archiveFolder);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var name = Path.GetFileNameWithoutExtension(oldPath) + "_Archived_" + stamp + Path.GetExtension(oldPath);
        var dest = Path.Combine(archiveFolder, name);
        var i = 1;
        while (File.Exists(dest)) dest = Path.Combine(archiveFolder, Path.GetFileNameWithoutExtension(name) + "_" + i++ + Path.GetExtension(name));
        File.Move(oldPath, dest);
    }

    public void LogAudit(string action, string gateId = "", string unitId = "", string resourceId = "", string details = "")
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AuditFile)!);
            var entry = new AuditEntry { Action = action, GateId = gateId, UnitId = unitId, ResourceId = resourceId, Details = details };
            var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var fs = new FileStream(AuditFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                    using var sw = new StreamWriter(fs, Encoding.UTF8);
                    sw.Write(line);
                    return;
                }
                catch when (attempt < 2) { Thread.Sleep(80); }
            }
        }
        catch { }
    }

    public List<AuditEntry> LoadRecentAudit(int max = 300)
    {
        if (!File.Exists(AuditFile)) return new();
        try
        {
            var lines = File.ReadLines(AuditFile).Reverse().Take(max).ToList();
            return lines.Select(line =>
            {
                try { return JsonSerializer.Deserialize<AuditEntry>(line, _read); }
                catch { return null; }
            }).Where(x => x is not null).Cast<AuditEntry>().ToList();
        }
        catch { return new(); }
    }

    public async Task<string> LookupStoreroomAsync(string query, CancellationToken cancellationToken = default)
    {
        var content = LoadSharedContent();
        if (string.IsNullOrWhiteSpace(content.StoreroomUrl)) return "Storeroom database URL is not configured.";
        if (string.IsNullOrWhiteSpace(query)) return "Enter or select a part number / keyword first.";

        using var handler = new HttpClientHandler { UseDefaultCredentials = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        var html = await client.GetStringAsync(content.StoreroomUrl, cancellationToken);
        var matchIndex = html.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (matchIndex < 0) return $"'{query}' was not found in the HTML returned by the storeroom page.";

        var rowStart = html.LastIndexOf("<tr", matchIndex, StringComparison.OrdinalIgnoreCase);
        var rowEnd = html.IndexOf("</tr>", matchIndex, StringComparison.OrdinalIgnoreCase);
        string snippet;
        if (rowStart >= 0 && rowEnd > rowStart) snippet = html.Substring(rowStart, rowEnd + 5 - rowStart);
        else
        {
            var start = Math.Max(0, matchIndex - 300);
            var length = Math.Min(html.Length - start, 700);
            snippet = html.Substring(start, length);
        }
        snippet = Regex.Replace(snippet, "<script.*?</script>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        snippet = Regex.Replace(snippet, "<style.*?</style>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        snippet = Regex.Replace(snippet, "<[^>]+>", " ");
        snippet = WebUtility.HtmlDecode(snippet);
        snippet = Regex.Replace(snippet, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(snippet) ? $"Found '{query}', but could not extract readable row text." : snippet;
    }

    private static List<HelpItem> DefaultHelpItems() => new()
    {
        new() { Id="find", Section="Quick Help", Title="How do I find the right gate?", Body="Choose the plant area, select the safety gate, then select the unit. The identification photo helps confirm you have the correct gate.", Keywords="asset unit find gate", SortOrder=10 },
        new() { Id="files", Section="Quick Help", Title="Where are drawings and configs?", Body="Open a system first. Each resource button shows only information linked to that gate/unit.", Keywords="drawing config files folder", SortOrder=20 },
        new() { Id="update", Section="Quick Help", Title="How do I update a drawing?", Body="Open Electrical Drawings for the gate/unit and use Upload / Update. Previous matching current files are archived automatically.", Keywords="drawing upload update revision archive", SortOrder=30 },
        new() { Id="reset", Section="Troubleshooting", Title="Gate will not reset / clear", Body="Start with visible conditions: verify the gate is physically closed/clear, check device indicators, and use the gate drawing and notes. Do not bypass an interlock to force a reset.", Keywords="reset clear gate interlock", SortOrder=10 },
        new() { Id="light", Section="Troubleshooting", Title="Signal light not working", Body="Verify the correct gate/unit, review the electrical drawing, check the expected light configuration, then use the linked parts/manuals as needed.", Keywords="signal light stack tower 8wd46", SortOrder=20 },
        new() { Id="scanner", Section="Troubleshooting", Title="Scanner fault", Body="Check for an obstructed or dirty scanner and visible fault indication, then open the linked scanner configuration/manual. Configuration downloads remain a manual action in the vendor software.", Keywords="scanner fault uam laser safety", SortOrder=30 }
    };
}
