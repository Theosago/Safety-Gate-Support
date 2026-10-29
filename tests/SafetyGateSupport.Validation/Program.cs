using System.Text.Json;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport.Validation;

internal static class Program
{
    private static string _runRoot = "";
    private static string _settingsPath = "";
    private static int _fixtureNumber;

    private static int Main(string[] args)
    {
        if (args.Any(argument => argument is not "--include-known-issues" and not "--benchmark-search" and not "--include-home-preview"))
        {
            Console.Error.WriteLine("Usage: SafetyGateSupport.Validation [--include-known-issues] [--benchmark-search] [--include-home-preview]");
            return 2;
        }
        // --include-known-issues remains accepted for older setup instructions;
        // its previously failing migration regression is now part of every run.
        // Set XDG_DATA_HOME before any SpecialFolder lookup or DataService construction.
        // Never load the user's real settings or the default Q: shared root.
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("REFUSED: this isolated data validation runner is Linux-only.");
            return 2;
        }

        var originalDirectory = Directory.GetCurrentDirectory();
        try
        {
            _runRoot = Directory.CreateTempSubdirectory("safetygate-validation-").FullName;
            var isolatedXdg = Path.Combine(_runRoot, "xdg-data");
            Directory.CreateDirectory(isolatedXdg);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", isolatedXdg);
            var resolvedLocalData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            Require(IsBeneath(resolvedLocalData, _runRoot) && resolvedLocalData == isolatedXdg,
                "LocalApplicationData did not resolve to the fresh isolated XDG directory; refusing to construct DataService.");
            var settingsDirectory = Path.Combine(resolvedLocalData, "SafetyGateSupport");
            Directory.CreateDirectory(settingsDirectory);
            _settingsPath = Path.Combine(settingsDirectory, "settings.json");
            Directory.SetCurrentDirectory(_runRoot);
            Console.WriteLine($"Isolated validation directory: {_runRoot}");

            List<(string Name, Action Check)> checks =
            [
                ("Resource applicability respects explicit unit assignments", ResourceApplicability),
                ("Manual applicability supports gate/unit assignments and global content", ManualApplicability),
                ("Part applicability supports gate/unit assignments and global content", PartApplicability),
                ("Troubleshooting applicability respects manufacturer and explicit assignments", FlowApplicability),
                ("Embedded seed initializes 60 units, 28 gates, 3 areas, and 360 resources", SeedInitialization),
                ("First-run migration preserves every unit and resource relationship", SeedMigrationConsistency),
                ("Repeated initialization preserves edited data and existing files", InitializationIdempotency),
                ("Shared notes round-trip Unicode and replace earlier content", SharedNotesRoundTrip),
                ("Resource archive mode SameExtension preserves unrelated extensions", () => ResourceArchiveMode("SameExtension")),
                ("Resource archive mode SameName preserves other names", () => ResourceArchiveMode("SameName")),
                ("Resource archive mode All archives all current files", () => ResourceArchiveMode("All")),
                ("Resource archive mode None preserves unrelated files", () => ResourceArchiveMode("None")),
                ("Same-name resource replacement preserves previous bytes in archive", ResourceSameNameReplacement),
                ("Identification photo replacement and current-photo reselection preserve bytes", IdentificationPhotoReplacement),
                ("JSON saves persist prior contents in a backup", BackupPersistence),
                ("Audit events persist, reload newest first, and honor the result limit", AuditPersistence),
                ("Troubleshooting trees round-trip branches, resource links, and assignments", TroubleshootingRoundTrip),
                ("Resource filtering excludes hidden, other-gate, and other-unit content", ResourceFiltering)
            ];
            checks.AddRange(SearchChecks.Cases);
            checks.AddRange(SupportLibraryChecks.Cases);
            checks.AddRange(AssetStorageChecks.Cases);
            // Home preview checks are opt-in; normal builds do not create a preview profile.
            if (args.Contains("--include-home-preview")) checks.AddRange(HomePreviewChecks.Cases);
            checks.Add(("Current-file snapshots retain shared-unit links and refresh files", CurrentFileSnapshots));
            checks.Add(("Cancelled file indexing stops before enumeration", CancelledFileIndexing));
            checks.Add(("Deferred data-service initialization leaves shared data untouched", DeferredInitialization));
            checks.Add(("Partial legacy migration persists newly discovered gates", ExistingHierarchyMigration));

            var passed = 0;
            foreach (var (name, check) in checks)
            {
                try
                {
                    check();
                    passed++;
                    Console.WriteLine($"PASS {name}");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
                }
            }

            Console.WriteLine($"Validation: {passed}/{checks.Count} passed; {checks.Count - passed} failed.");
            if (passed != checks.Count)
            {
                Console.Error.WriteLine($"Failure evidence retained at {_runRoot}");
                return 1;
            }
            if (args.Contains("--benchmark-search", StringComparer.Ordinal)) SearchChecks.RunBenchmark();
            Directory.SetCurrentDirectory(originalDirectory);
            Directory.Delete(_runRoot, recursive: true);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"REFUSED / SETUP FAILED: {exception.Message}");
            return 2;
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    private static void ResourceApplicability()
    {
        var resource = new ResourceDefinition();
        Require(resource.AppliesTo("unit-a"), "Unassigned resources should apply to all units of their gate.");
        resource.AppliesToUnitIds = ["UNIT-A", "unit-b"];
        Require(resource.AppliesTo("unit-a") && resource.AppliesTo("UNIT-B"), "Unit matching should ignore case.");
        Require(!resource.AppliesTo("unit-c"), "Explicit assignments must exclude other units.");
    }

    private static void ManualApplicability()
    {
        var manual = new ManualRecord();
        Require(manual.AppliesTo("other", "other"), "Unassigned manual should be global.");
        manual.AppliesToGateIds = ["GATE-A"];
        manual.AppliesToUnitIds = ["UNIT-B"];
        Require(manual.AppliesTo("gate-a", "other") && manual.AppliesTo("other", "unit-b"), "Gate and unit assignments should match independently, ignoring case.");
        Require(!manual.AppliesTo("gate-c", "unit-c"), "Unassigned systems should be excluded.");
    }

    private static void PartApplicability()
    {
        var part = new PartRecord();
        Require(part.AppliesTo("other", "other"), "Unassigned part should be global.");
        part.AppliesToGateIds = ["GATE-A"];
        part.AppliesToUnitIds = ["UNIT-B"];
        Require(part.AppliesTo("gate-a", "other") && part.AppliesTo("other", "unit-b"), "Gate and unit assignments should match independently, ignoring case.");
        Require(!part.AppliesTo("gate-c", "unit-c"), "Unassigned systems should be excluded.");
    }

    private static void FlowApplicability()
    {
        var gate = new SafetyGate { Id = "gate-a", Manufacturer = "Safe2Cross" };
        var unit = new SafetySystem { Id = "unit-a" };
        var flow = new TroubleshootingFlow();
        Require(flow.AppliesTo(gate, unit), "Unrestricted flow should be global.");
        flow.ManufacturerFilter = "SAFE2";
        Require(flow.AppliesTo(gate, unit), "Manufacturer substring matching should ignore case.");
        flow.ManufacturerFilter = "Other manufacturer";
        Require(!flow.AppliesTo(gate, unit), "Manufacturer filter should exclude other manufacturers.");
        flow.AppliesToGateIds = ["GATE-A"];
        Require(flow.AppliesTo(gate, unit), "Explicit assignment should take precedence over manufacturer filter.");
        flow.AppliesToGateIds = ["gate-other"];
        Require(!flow.AppliesTo(gate, unit), "Explicit assignments should exclude unassigned systems.");
        flow.AppliesToUnitIds = ["UNIT-A"];
        Require(flow.AppliesTo(gate, unit), "Explicit unit assignments should match ignoring case.");
    }

    private static void SeedInitialization()
    {
        var data = NewService();
        Require(!data.IsReady, "A fresh temporary shared root should be uninitialized.");
        data.InitializeSharedData();
        Require(data.IsReady, "Embedded seed initialization should make data ready.");
        Equal(60, data.LoadSystems().Count, "Seeded unit count");
        Equal(28, data.LoadGates().Count, "Migrated gate count");
        Equal(3, data.LoadCategories().Count, "Seeded area count");
        Equal(360, data.LoadResources().Count, "Migrated resource count");
        Equal(3, data.LoadTroubleshootingFlows().Count(f => !f.Id.StartsWith("builtin-")), "Migrated troubleshooting flow count");
        Equal(5, data.LoadTroubleshootingFlows().Count(f => f.Id.StartsWith("builtin-")), "Curated troubleshooting flow count");
        Require(data.LoadSystems().All(unit => File.Exists(data.Resolve(unit.PrimaryPhoto))), "Every seeded identification photo should be present.");
    }

    private static void SeedMigrationConsistency()
    {
        var data = NewService();
        data.InitializeSharedData();
        var gates = data.LoadGates();
        var units = data.LoadSystems();
        var resources = data.LoadResources();
        Equal(gates.Count, gates.Select(g => g.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Unique gate IDs");
        Equal(units.Count, units.Select(u => u.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Unique unit IDs");
        Equal(resources.Count, resources.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Unique resource IDs");
        foreach (var unit in units)
        {
            var gate = gates.SingleOrDefault(g => g.Id == unit.GateId);
            Require(gate is not null, $"Unit {unit.Id} must reference a persisted gate.");
            Equal(gate!.Category, unit.Category, "Migrated unit area");
            Equal(gate.Manufacturer, unit.GateBrand, "Migrated unit manufacturer");
            Equal(6, resources.Count(r => r.GateId == gate.Id && r.AppliesToUnitIds.Contains(unit.Id)), "Resource records per legacy unit");
            Require(IsBeneath(data.Resolve(unit.PhotosFolder), data.SharedRoot), "Legacy photo folders must remain inside the temporary shared root.");
        }
        foreach (var resource in resources)
            Require(gates.Any(g => g.Id == resource.GateId) && resource.AppliesToUnitIds.All(id => units.Any(u => u.Id == id && u.GateId == resource.GateId)), "Resource gate and unit assignments must be consistent.");
        Require(data.LoadTroubleshootingFlows().Where(f => !f.Id.StartsWith("builtin-")).All(f => f.Nodes.Any(n => n.Id == f.StartNodeId && n.IsTerminal)), "Migrated help topics should have valid terminal start nodes.");
    }

    private static void InitializationIdempotency()
    {
        var data = NewService();
        data.InitializeSharedData();
        var gates = data.LoadGates();
        gates[0].Location = "Validation change — preserve this";
        data.SaveGates(gates);
        var photo = data.Resolve(data.LoadSystems()[0].PrimaryPhoto);
        File.WriteAllText(photo, "edited file sentinel");
        var paths = new[] { data.GatesFile, data.SystemsFile, data.ResourcesFile, data.TroubleshootingFile, photo };
        var snapshots = paths.ToDictionary(p => p, File.ReadAllBytes);
        data.InitializeSharedData();
        data.EnsureSharedDefaults();
        foreach (var path in paths)
            Require(snapshots[path].SequenceEqual(File.ReadAllBytes(path)), $"Repeat initialization changed existing file {Path.GetFileName(path)}.");
        Equal(60, data.LoadSystems().Count, "Repeat-initialization unit count");
    }

    private static void ExistingHierarchyMigration()
    {
        var fixture = NewFixture();
        var legacy = new SafetySystem { Id = "legacy-unit", GateId = "", Category = "Legacy area", AssetNumber = "9999", GateBrand = "Legacy manufacturer" };
        fixture.Data.SaveSystems([fixture.Unit, legacy]);
        fixture.Data.EnsureSharedDefaults();
        var migrated = fixture.Data.LoadSystems().Single(u => u.Id == legacy.Id);
        Require(!string.IsNullOrWhiteSpace(migrated.GateId), "Legacy unit should receive a gate ID.");
        Require(fixture.Data.LoadGates().Any(g => g.Id == migrated.GateId), "Migrated legacy unit references a gate that was never persisted to gates.json.");
    }

    private static void SharedNotesRoundTrip()
    {
        var fixture = NewFixture();
        const string first = "Gate 1 — notes\nCheck indicator ✓\n";
        fixture.Data.SaveSystemNotes(fixture.Unit, first);
        Equal(first, fixture.Data.LoadSystemNotes(fixture.Unit), "Unicode notes round-trip");
        Require(fixture.Data.GetSystemNotesLastWriteUtc(fixture.Unit) > DateTime.MinValue, "Saved notes should have a modification time.");
        fixture.Data.SaveSystemNotes(fixture.Unit, "Replacement text");
        Equal("Replacement text", fixture.Data.LoadSystemNotes(fixture.Unit), "Replacing notes");
        fixture.Data.SaveSystemNotes(fixture.Unit, "");
        Equal("", fixture.Data.LoadSystemNotes(fixture.Unit), "Clearing notes");
        Require(!Directory.EnumerateFiles(Path.GetDirectoryName(fixture.Data.SystemNotesPath(fixture.Unit))!, "*.tmp").Any(), "Notes saves should not leave temporary files.");
    }

    private static void ResourceArchiveMode(string mode)
    {
        var fixture = NewFixture();
        fixture.Resource.ArchiveMode = mode;
        var current = fixture.Data.ResourceCurrentFolder(fixture.Resource, fixture.Gate);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "old.pdf"), "old PDF");
        File.WriteAllText(Path.Combine(current, "other.txt"), "unrelated text");
        var source = WriteSource(fixture.Data, "new.pdf", "new PDF");
        var saved = fixture.Data.AddOrUpdateResource(fixture.Resource, fixture.Gate, fixture.Unit, source);
        Equal("new PDF", File.ReadAllText(saved), "Uploaded resource bytes");
        var shouldArchivePdf = mode is "All" or "SameExtension";
        var shouldArchiveText = mode == "All";
        Equal(!shouldArchivePdf, File.Exists(Path.Combine(current, "old.pdf")), "Old PDF retention");
        Equal(!shouldArchiveText, File.Exists(Path.Combine(current, "other.txt")), "Unrelated text retention");
        var archived = Directory.GetFiles(fixture.Data.ResourceArchiveFolder(fixture.Resource, fixture.Gate));
        Equal((shouldArchivePdf ? 1 : 0) + (shouldArchiveText ? 1 : 0), archived.Length, "Archived file count");
        if (shouldArchivePdf) Require(archived.Any(p => File.ReadAllText(p) == "old PDF"), "Archived PDF bytes should be preserved.");
        if (shouldArchiveText) Require(archived.Any(p => File.ReadAllText(p) == "unrelated text"), "Archived text bytes should be preserved.");
    }

    private static void ResourceSameNameReplacement()
    {
        foreach (var mode in new[] { "SameName", "None" })
        {
            var fixture = NewFixture();
            fixture.Resource.ArchiveMode = mode;
            var source = WriteSource(fixture.Data, "drawing.pdf", "revision 1");
            fixture.Data.AddOrUpdateResource(fixture.Resource, fixture.Gate, fixture.Unit, source);
            File.WriteAllText(source, "revision 2");
            var saved = fixture.Data.AddOrUpdateResource(fixture.Resource, fixture.Gate, fixture.Unit, source);
            Equal("revision 2", File.ReadAllText(saved), "Replaced resource bytes");
            var archived = Directory.GetFiles(fixture.Data.ResourceArchiveFolder(fixture.Resource, fixture.Gate));
            Equal(1, archived.Length, $"Same-name replacement archive count for {mode}");
            Equal("revision 1", File.ReadAllText(archived[0]), "Previous resource revision");
        }
    }

    private static void IdentificationPhotoReplacement()
    {
        var fixture = NewFixture();
        // Valid one-pixel PNG; this check exercises storage, not Windows image decoding.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6hS8AAAAASUVORK5CYII=");
        var source = WriteSource(fixture.Data, "identification.png", "");
        File.WriteAllBytes(source, png);
        fixture.Data.SetIdentificationPhoto(fixture.Unit, source);
        var first = fixture.Data.Resolve(fixture.Unit.PrimaryPhoto);
        Require(png.SequenceEqual(File.ReadAllBytes(first)), "First photo should preserve source bytes.");
        // Avoid relying on wall-clock resolution when checking intentionally unique output paths.
        AdvancePastCurrentMillisecond();
        fixture.Data.SetIdentificationPhoto(fixture.Unit, source);
        var second = fixture.Data.Resolve(fixture.Unit.PrimaryPhoto);
        Require(first != second && !File.Exists(first), "Replacement should use a fresh path and archive the first photo.");
        AdvancePastCurrentMillisecond();
        fixture.Data.SetIdentificationPhoto(fixture.Unit, second);
        var third = fixture.Data.Resolve(fixture.Unit.PrimaryPhoto);
        Require(third != second && png.SequenceEqual(File.ReadAllBytes(third)), "Reselecting the current photo should preserve its bytes at a fresh path.");
        var archived = Directory.GetFiles(Path.Combine(fixture.Data.Resolve(fixture.Unit.PhotosFolder), "Archive"));
        Equal(2, archived.Length, "Archived identification revision count");
        Require(archived.All(path => png.SequenceEqual(File.ReadAllBytes(path))), "Archived photos should preserve original bytes.");
        fixture.Data.SaveSystems([fixture.Unit]);
        Equal(fixture.Unit.PrimaryPhoto, fixture.Data.LoadSystems().Single().PrimaryPhoto, "Saved identification path");
    }

    private static void BackupPersistence()
    {
        var data = NewService();
        data.SaveCategories(["Original area"]);
        var original = File.ReadAllBytes(data.CategoriesFile);
        data.SaveCategories(["Updated area"]);
        Equal("Updated area", data.LoadCategories().Single(), "Updated categories");
        var backups = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(data.CategoriesFile)!, "Backups"), "categories_*.json");
        Equal(1, backups.Length, "Category backup count");
        Require(original.SequenceEqual(File.ReadAllBytes(backups[0])), "Backup must retain exact previous JSON bytes.");
        Require(!File.Exists(data.CategoriesFile + ".tmp"), "Successful JSON saves should remove the temporary path.");
    }

    private static void AuditPersistence()
    {
        var data = NewService();
        data.LogAudit("First action", "gate-a", "unit-a", "resource-a", "Unicode ✓");
        data.LogAudit("Second action", details: "second detail");
        var entries = data.LoadRecentAudit();
        Equal(2, entries.Count, "Persisted audit count");
        Equal("Second action", entries[0].Action, "Newest audit event first");
        Equal("gate-a", entries[1].GateId, "Audit gate ID");
        Equal("unit-a", entries[1].UnitId, "Audit unit ID");
        Equal("resource-a", entries[1].ResourceId, "Audit resource ID");
        Equal("Unicode ✓", entries[1].Details, "Audit details");
        Require(entries.All(e => e.TimestampUtc.Kind == DateTimeKind.Utc), "Audit timestamps should retain UTC kind.");
        Equal("Second action", data.LoadRecentAudit(1).Single().Action, "Audit result limit");
    }

    private static void TroubleshootingRoundTrip()
    {
        var fixture = NewFixture();
        var flow = new TroubleshootingFlow
        {
            Id = "validation-flow", Title = "Gate will not reset", Keywords = "reset fault", ManufacturerFilter = "Safe2Cross",
            AppliesToGateIds = [fixture.Gate.Id], AppliesToUnitIds = [fixture.Unit.Id], StartNodeId = "start",
            Nodes =
            [
                new() { Id = "start", Prompt = "Is the gate closed?", Body = "Observe the indicator — do not bypass it.", ResourceIds = [fixture.Resource.Id], Choices = [new() { Text = "Yes", NextNodeId = "end" }, new() { Text = "No", NextNodeId = "start" }] },
                new() { Id = "end", Prompt = "Review manual", Body = "End of check.", IsTerminal = true }
            ]
        };
        fixture.Data.SaveTroubleshootingFlows([flow]);
        var loaded = fixture.Data.LoadTroubleshootingFlows().Single(f => f.Id == flow.Id);
        Equal(JsonSerializer.Serialize(flow), JsonSerializer.Serialize(loaded), "Troubleshooting tree JSON round-trip");
        Require(loaded.Nodes.Any(n => n.Id == loaded.StartNodeId), "Start node should resolve.");
        Require(loaded.Nodes.SelectMany(n => n.Choices).All(c => loaded.Nodes.Any(n => n.Id == c.NextNodeId)), "Every choice target should resolve, including cycles.");
    }

    private static void ResourceFiltering()
    {
        var fixture = NewFixture();
        List<ResourceDefinition> resources =
        [
            new() { Id = "visible", GateId = fixture.Gate.Id, AppliesToUnitIds = [fixture.Unit.Id], SortOrder = 20 },
            new() { Id = "global-for-gate", GateId = fixture.Gate.Id, SortOrder = 10 },
            new() { Id = "hidden", GateId = fixture.Gate.Id, IsVisible = false },
            new() { Id = "other-gate", GateId = "other-gate" },
            new() { Id = "other-unit", GateId = fixture.Gate.Id, AppliesToUnitIds = ["other-unit"] }
        ];
        fixture.Data.SaveResources(resources);
        var actual = fixture.Data.ResourcesFor(fixture.Gate, fixture.Unit);
        Require(actual.Select(r => r.Id).SequenceEqual(new[] { "global-for-gate", "visible" }), "Visible resources must match gate/unit and follow configured sort order.");
    }

    private static void CurrentFileSnapshots()
    {
        var fixture = NewFixture();
        var otherUnit = new SafetySystem { Id = "unit-b", GateId = fixture.Gate.Id, UnitNumber = "2" };
        fixture.Resource.AppliesToUnitIds = [fixture.Unit.Id, otherUnit.Id];
        var directory = fixture.Data.ResourceCurrentFolder(fixture.Resource, fixture.Gate);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "drawing.pdf"), "drawing");
        var units = new[] { fixture.Unit, otherUnit };
        var resources = new[] { fixture.Resource, new ResourceDefinition { Id = "hidden", GateId = fixture.Gate.Id, IsVisible = false } };
        // Captured metadata must be used even if its indexes are no longer available.
        File.Delete(fixture.Data.GatesFile);
        File.Delete(fixture.Data.ResourcesFile);
        var files = fixture.Data.GetCurrentSystemFiles(units, [fixture.Gate], resources).ToList();
        Equal(2, files.Count, "Shared resource results per unit");
        Require(files.Select(f => f.System.Id).SequenceEqual(new[] { "unit-a", "unit-b" }), "Both assigned units must retain their searchable resource link.");
        File.WriteAllText(Path.Combine(directory, "new-file.pdf"), "new drawing");
        var refreshed = fixture.Data.GetCurrentSystemFiles(units, [fixture.Gate], resources).ToList();
        Equal(4, refreshed.Count, "A new indexing pass must see new files rather than use a persistent folder cache");
    }

    private static void CancelledFileIndexing()
    {
        var fixture = NewFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            _ = fixture.Data.GetCurrentSystemFiles([fixture.Unit], [fixture.Gate], [fixture.Resource], cancellation.Token).ToList();
            throw new InvalidOperationException("Cancelled indexing should not continue.");
        }
        catch (OperationCanceledException) { }
    }

    private static void DeferredInitialization()
    {
        var fixture = NewFixture();
        var before = Directory.GetFiles(fixture.Data.SharedRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        var data = new DataService(initializeSharedDefaults: false);
        Equal(fixture.Data.SharedRoot, data.SharedRoot, "Local settings should still load");
        var after = Directory.GetFiles(data.SharedRoot, "*", SearchOption.AllDirectories);
        Equal(before.Count, after.Length, "Deferred construction must not create shared defaults");
        Require(after.All(path => before.TryGetValue(path, out var bytes) && bytes.SequenceEqual(File.ReadAllBytes(path))), "Deferred construction must not change shared files.");
    }

    private static DataService NewService()
    {
        var root = Path.Combine(_runRoot, $"fixture-{++_fixtureNumber:00}", "shared");
        Require(IsBeneath(root, _runRoot) && IsBeneath(_settingsPath, _runRoot), "Fixture paths escaped isolation.");
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new AppSettings { SharedRoot = root }));
        var data = new DataService();
        Equal(root, data.SharedRoot, "Isolated shared root binding");
        return data;
    }

    private static Fixture NewFixture()
    {
        var data = NewService();
        var gate = new SafetyGate { Id = "gate-a", Category = "Validation area", AssetNumber = "101", Manufacturer = "Safe2Cross" };
        var unit = new SafetySystem { Id = "unit-a", GateId = gate.Id, UnitNumber = "1" };
        data.SaveGates([gate]);
        data.EnsureUnitFolders(gate, unit);
        data.SaveSystems([unit]);
        data.SaveCategories([gate.Category]);
        var resource = new ResourceDefinition { Id = "resource-a", GateId = gate.Id, AppliesToUnitIds = [unit.Id], Label = "Electrical Drawings", Kind = "ManagedFiles" };
        data.SaveResources([resource]);
        return new(data, gate, unit, resource);
    }

    private static string WriteSource(DataService data, string name, string contents)
    {
        var directory = Path.Combine(Path.GetDirectoryName(data.SharedRoot)!, "sources");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static void AdvancePastCurrentMillisecond()
    {
        var initial = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
        while (DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond == initial) Thread.Sleep(1);
    }

    private static bool IsBeneath(string candidate, string directory) =>
        Path.GetFullPath(candidate).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string context) =>
        Require(EqualityComparer<T>.Default.Equals(expected, actual), $"{context}: expected '{expected}', got '{actual}'.");

    private sealed record Fixture(DataService Data, SafetyGate Gate, SafetySystem Unit, ResourceDefinition Resource);
}
