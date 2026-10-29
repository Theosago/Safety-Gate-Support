using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport.Validation;

internal static class AssetStorageChecks
{
    public static IEnumerable<(string Name, Action Check)> Cases =>
    [
        ("Asset layout preview does not move files or change metadata", PreviewOnly),
        ("Shared and separate prints retain explicit unit scopes after consolidation", SharedAndSeparate),
        ("Replacing a shared sheet updates both linked units and preserves unrelated sheets", SharedReplacement),
        ("Adding and linking a shared file reuses identical bytes without assigning other units", Linking),
        ("Conflicting revisions block conversion until the older copy is archived", ConflictReview),
        ("Stale previews and changed source bytes cannot switch the layout", StalePlan),
        ("Readable asset layout organization is repeatable without extra filename prefixes", RepeatedOrganization),
        ("Asset folder conversion retains photo, notes and troubleshooting resource links", PhotoNotesAndFlows),
        ("Drawing updates require both PDF and DWG before publishing", PairRequired),
        ("Drawing companion status detects external one-sided updates and missing files", PairStatus),
        ("Shared print unit changes apply to both companions without affecting separate prints", PairUnits),
        ("Repeated folder organization preserves recorded nonmatching PDF and DWG filenames", RetainPair)
    ];
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "asset-layout-test-" + Guid.NewGuid().ToString("N"));
        public DataService Data { get; }
        public SafetyGate Gate { get; }
        public List<SafetySystem> Units { get; }
        public List<ResourceDefinition> Resources { get; }
        public Fixture(bool conflict = false)
        {
            Data = new(false, Path.Combine(Root, "settings")); Data.SetSharedRoot(Path.Combine(Root, "share")); Data.InitializeSharedData();
            var units = Data.LoadSystems(); var group = units.GroupBy(u => u.GateId).First(g => g.Count() >= 3);
            Gate = Data.LoadGates().Single(g => g.Id == group.Key); Gate.AssetNumber = "1223";
            Units = group.Take(3).ToList(); for (int i = 0; i < 3; i++) { Units[i].UnitNumber = (i + 1).ToString(); Units[i].GateId = Gate.Id; }
            Data.SaveGates([Gate]); Data.SaveSystems(Units);
            Resources =
            [
                new() { Id = "shared", GateId = Gate.Id, Label = "Electrical Drawings", RelativeFolder = "Gates/legacy/Drawings", AppliesToUnitIds = [Units[0].Id, Units[1].Id], AllowUserUpload = true },
                new() { Id = "imported", GateId = Gate.Id, Label = "Electrical Drawings (imported)", RelativeFolder = "Library/Assignments/opaque", AppliesToUnitIds = [Units[0].Id, Units[1].Id], AllowUserUpload = true },
                new() { Id = "separate", GateId = Gate.Id, Label = "Electrical Drawings", RelativeFolder = "Gates/other/Drawings", AppliesToUnitIds = [Units[2].Id], AllowUserUpload = true },
                new() { Id = "light", GateId = Gate.Id, Label = "Light Configuration", RelativeFolder = "Gates/legacy/Light", AppliesToUnitIds = [Units[0].Id], LaunchAction = "Siemens" }
            ];
            Data.SaveResources(Resources);
            Add(Resources[0], "Shared.pdf", "shared-print"); Add(Resources[1], "Shared.pdf", conflict ? "different-revision" : "shared-print");
            Add(Resources[2], "Separate.pdf", "unit-three-print"); Add(Resources[3], "config.signal", "unit-one-config");
        }
        public string Add(ResourceDefinition resource, string name, string contents)
        {
            Data.EnsureResourceFolders(resource, Gate); string file = Path.Combine(Data.ResourceCurrentFolder(resource, Gate), name); File.WriteAllText(file, contents); return file;
        }
        public ResourceDefinition Drawings => Data.LoadResources().Single(r => r.GateId == Gate.Id && r.StorageCategory == "Electrical Drawings");
        public string[] FilesFor(int unit) => Data.GetResourceFiles(Drawings.ForUnit(Units[unit].Id), Data.LoadGates().Single());
        public void Convert() { var plan = AssetStorageMigration.Build(Data); Require(AssetStorageMigration.Validate(Data, plan).Count == 0, "Valid conversion has conflicts."); AssetStorageMigration.Apply(Data, plan); }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private static void PreviewOnly()
    {
        using var f = new Fixture(); string before = File.ReadAllText(f.Data.ResourcesFile);
        var plan = AssetStorageMigration.Build(f.Data);
        Require(before == File.ReadAllText(f.Data.ResourcesFile) && File.Exists(Path.Combine(f.Data.ResourceCurrentFolder(f.Resources[0], f.Gate), "Shared.pdf")), "Preview changed source files.");
        Require(!Directory.Exists(f.Data.Resolve("Assets")), "Preview created active asset folders.");
    }
    private static void SharedAndSeparate()
    {
        using var f = new Fixture(); f.Convert();
        Require(f.FilesFor(0).Length == 1 && f.FilesFor(1).Length == 1 && f.FilesFor(2).Length == 1, "Unit file scopes changed.");
        Require(f.FilesFor(0)[0] == f.FilesFor(1)[0] && f.FilesFor(2)[0] != f.FilesFor(0)[0], "Shared and separate prints were confused.");
        Require(File.ReadAllText(f.FilesFor(2)[0]) == "unit-three-print", "A shared print leaked into the separate unit.");
        Require(Directory.GetFiles(f.Data.Resolve("Assets/Asset 1223/Electrical Drawings")).Length == 2, "Duplicate imported copy stayed active.");
        Require(f.Data.LoadResources().Count(r => r.StorageCategory == "Electrical Drawings") == 1, "Imported drawing button was retained.");
        Require(File.Exists(f.Data.Resolve("Assets/Asset 1223/File index.csv")), "Files-only unit index is missing.");
    }
    private static void SharedReplacement()
    {
        using var f = new Fixture(); f.Convert(); var gate = f.Data.LoadGates().Single();
        string separate = f.FilesFor(2)[0], original = f.FilesFor(0)[0], source = Path.Combine(f.Root, "Updated.pdf"); File.WriteAllText(source, "new-shared-revision");
        string dwg = Path.Combine(f.Root, "Updated.dwg"); File.WriteAllText(dwg, "new-shared-cad");
        var result = f.Data.SaveDrawingPair(f.Drawings.ForUnit(f.Units[0].Id), gate, f.Units[0], [source, dwg], original);
        Require(f.FilesFor(0).Contains(result.PdfPath) && f.FilesFor(1).Contains(result.PdfPath) && f.FilesFor(1).Contains(result.DwgPath), "Replacement did not preserve both unit links.");
        Require(f.FilesFor(2).Single() == separate && File.ReadAllText(separate) == "unit-three-print", "Replacement archived an unrelated sheet.");
        Require(Directory.GetFiles(f.Data.ResourceArchiveFolder(f.Drawings, gate), "*", SearchOption.AllDirectories).Any(p => File.ReadAllText(p) == "shared-print"), "Old shared revision was not archived.");
        Require(!File.Exists(original), "Old revision is still an active file.");
    }
    private static void Linking()
    {
        using var f = new Fixture(); f.Convert(); var gate = f.Data.LoadGates().Single();
        string source = Path.Combine(f.Root, "Another-name.pdf"); File.WriteAllText(source, "unit-three-print");
        string target = f.Data.AssignAssetFile(gate, f.Units[0], source, "Electrical Drawings");
        Require(target == f.FilesFor(2).Single(), "Byte-identical assignment created another copy.");
        Require(f.FilesFor(0).Contains(target) && !f.FilesFor(1).Contains(target), "Linking one unit assigned a different unit implicitly.");
        f.Data.SetAssetFileUnits(f.Drawings.ForUnit(f.Units[0].Id), gate, Path.GetFileName(target), [f.Units[2].Id]);
        Require(!f.FilesFor(0).Contains(target) && f.FilesFor(2).Contains(target), "Explicit unit link change did not take effect.");
    }
    private static void ConflictReview()
    {
        using var f = new Fixture(true); var plan = AssetStorageMigration.Build(f.Data);
        Require(AssetStorageMigration.Validate(f.Data, plan).Any(e => e.Contains("Different contents")), "Conflicting current revisions were silently chosen.");
        var old = plan.Files.Single(x => x.Sources.Any(p => p.Contains("Gates")) && x.Category == "Electrical Drawings" && x.Scope.Contains("Unit 1"));
        old.Decision = AssetStorageMigration.Disposition.Archive;
        Require(AssetStorageMigration.Validate(f.Data, plan).Count == 0, "Archiving the older copy did not resolve its conflict.");
        AssetStorageMigration.Apply(f.Data, plan);
        Require(File.ReadAllText(f.FilesFor(0).Single()) == "different-revision", "Reviewed winner did not become current.");
    }
    private static void StalePlan()
    {
        using var f = new Fixture(); var plan = AssetStorageMigration.Build(f.Data); string records = File.ReadAllText(f.Data.ResourcesFile);
        string source = Path.Combine(f.Data.ResourceCurrentFolder(f.Resources[0], f.Gate), "Shared.pdf"); File.WriteAllText(source, "changed-after-preview");
        bool refused = false; try { AssetStorageMigration.Apply(f.Data, plan); } catch (IOException) { refused = true; }
        Require(refused && File.ReadAllText(f.Data.ResourcesFile) == records && File.ReadAllText(source) == "changed-after-preview", "Stale bytes switched metadata or destroyed the source.");
    }
    private static void RepeatedOrganization()
    {
        using var f = new Fixture(); f.Convert(); string first = f.FilesFor(0).Single();
        var second = AssetStorageMigration.Build(f.Data); Require(AssetStorageMigration.Validate(f.Data, second).Count == 0, "Repeating conversion introduced conflicts."); AssetStorageMigration.Apply(f.Data, second);
        Require(f.FilesFor(0).Single() == first, "Repeating conversion added another unit prefix.");
    }
    private static void PhotoNotesAndFlows()
    {
        using var f = new Fixture();
        f.Data.SaveSystemNotes(f.Units[0], "Unit one shared notes");
        string photo = f.Data.Resolve(f.Units[0].PhotosFolder + "/custom.jpg"); Directory.CreateDirectory(Path.GetDirectoryName(photo)!); File.WriteAllText(photo, "photo-bytes"); f.Units[0].PrimaryPhoto = f.Data.ToRelative(photo); f.Data.SaveSystems(f.Units);
        f.Data.SaveTroubleshootingFlows([new() { Id = "site-flow", Nodes = [new() { Id = "node", ResourceIds = ["shared", "imported"] }] }]);
        f.Convert(); var unit = f.Data.LoadSystems().Single(u => u.Id == f.Units[0].Id);
        Require(f.Data.LoadSystemNotes(unit) == "Unit one shared notes" && File.ReadAllText(f.Data.Resolve(unit.PrimaryPhoto)) == "photo-bytes", "Photo or notes link broke during organization.");
        var links = f.Data.LoadTroubleshootingFlows().Single(x => x.Id == "site-flow").Nodes.Single().ResourceIds;
        Require(links.SequenceEqual([f.Drawings.Id]), "Site troubleshooting links were dropped or duplicated.");
    }
    private static DataService.PairUploadResult Publish(Fixture f, string pdf = "pdf-revision-one", string dwg = "dwg-revision-one", bool replacing = true)
    {
        string folder = Path.Combine(f.Root, "incoming"); Directory.CreateDirectory(folder);
        string p = Path.Combine(folder, "Print export.pdf"), d = Path.Combine(folder, "CAD source.dwg"); File.WriteAllText(p, pdf); File.WriteAllText(d, dwg);
        return f.Data.SaveDrawingPair(f.Drawings.ForUnit(f.Units[0].Id), f.Data.LoadGates().Single(), f.Units[0], [p, d], replacing ? f.FilesFor(0).First(x => x.EndsWith(".pdf")) : null);
    }
    private static void PairRequired()
    {
        using var f = new Fixture(); f.Convert(); var gate = f.Data.LoadGates().Single(); string before = File.ReadAllText(f.Data.ResourcesFile);
        bool refused = false; try { f.Data.SaveDrawingPair(f.Drawings.ForUnit(f.Units[0].Id), gate, f.Units[0], [f.FilesFor(0).Single()], f.FilesFor(0).Single()); } catch (InvalidOperationException error) { refused = error.Message.Contains("DWG"); }
        Require(refused && File.ReadAllText(f.Data.ResourcesFile) == before, "A partial pair changed current metadata.");
        var pair = Publish(f); Require(File.Exists(pair.PdfPath) && File.Exists(pair.DwgPath), "A complete pair was not published.");
        string separate = f.FilesFor(2).Single(); Publish(f, "pdf-revision-two", "dwg-revision-two");
        Require(File.ReadAllText(pair.PdfPath) == "pdf-revision-two" && File.ReadAllText(pair.DwgPath) == "dwg-revision-two" && File.ReadAllText(separate) == "unit-three-print", "Paired replacement changed an unrelated print or left an old companion current.");
    }
    private static void PairStatus()
    {
        using var f = new Fixture(); f.Convert(); var result = Publish(f); var gate = f.Data.LoadGates().Single(); var resource = f.Drawings.ForUnit(f.Units[0].Id);
        Require(!DrawingPairStatus.Check(f.Data, gate, resource, result.PdfPath, CancellationToken.None).NeedsAttention, "New coordinated upload is not recorded.");
        File.WriteAllText(result.DwgPath, "external-dwg-change");
        Require(DrawingPairStatus.Check(f.Data, gate, resource, result.PdfPath, CancellationToken.None).Message.Contains("DWG changed, but the PDF has not"), "One-sided DWG update was not detected.");
        File.WriteAllText(result.DwgPath, "dwg-revision-one"); File.WriteAllText(result.PdfPath, "external-pdf-change");
        Require(DrawingPairStatus.Check(f.Data, gate, resource, result.PdfPath, CancellationToken.None).Message.Contains("PDF changed, but the DWG has not"), "One-sided PDF update was not detected.");
        File.Delete(result.DwgPath);
        Require(DrawingPairStatus.Check(f.Data, gate, resource, result.PdfPath, CancellationToken.None).Message.Contains("DWG companion missing"), "Missing companion was not detected.");
        var unchanged = Publish(f, "pdf-revision-one", "dwg-revision-two");
        Require(unchanged.ReviewNote.Length > 0, "Submitting an unchanged companion did not warn.");
    }
    private static void PairUnits()
    {
        using var f = new Fixture(); f.Convert(); var pair = Publish(f); var gate = f.Data.LoadGates().Single();
        f.Data.SetAssetFileUnits(f.Drawings.ForUnit(f.Units[0].Id), gate, Path.GetFileName(pair.PdfPath), [f.Units[1].Id]);
        Require(f.FilesFor(0).Length == 0 && f.FilesFor(1).Contains(pair.PdfPath) && f.FilesFor(1).Contains(pair.DwgPath), "Changing shared print scope split the companions.");
        Require(f.FilesFor(2).Length == 1, "An unrelated unit inherited the shared pair.");
    }
    private static void RetainPair()
    {
        using var f = new Fixture(); f.Convert(); Publish(f); var before = f.Drawings.DrawingPairs.Single();
        var plan = AssetStorageMigration.Build(f.Data); AssetStorageMigration.Apply(f.Data, plan); var after = f.Drawings.DrawingPairs.Single();
        Require(before.Id == after.Id && before.Pdf == after.Pdf && before.Dwg == after.Dwg && after.UploadedTogetherUtc is not null, "Organization discarded the pair or its upload baseline.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
