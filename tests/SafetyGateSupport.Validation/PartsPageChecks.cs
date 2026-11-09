using System.Net;
using System.Text;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;
namespace SafetyGateSupport.Validation;

internal static class PartsPageChecks
{
    public static IEnumerable<(string Name, Action Check)> Cases =>
    [
        ("Blank plant libraries stay empty across restarts and pilot PDFs preserve other assignments", BlankPlantSetup),
        ("Catalog refresh targets unrelated IDs without trusting diagnostic rows", CatalogLookup),
        ("Long and alphanumeric IDs import full stockroom fields and bulk input stays exact", LongItemIds),
        ("Phone guide exports are self-contained and QR labels decode to offline text or exact approved links", PhoneGuides),
        ("Common problem topics persist shared photos, visibility and edits without losing other topics", CommonProblems),
        ("Unconfirmed Blazor inventory stays diagnostic even when every requested Item ID matches", UnconfirmedInventory),
        ("Item ID import maps stockroom fields and preserves existing photos, documents and assignments", ItemImport),
        ("Selected plant inventories ignore legacy snapshot sites and reject duplicate stocks", PlantInventories),
        ("Unit replacement-part choices preserve other units and never invent availability", UnitParts),
        ("Repeated inventory tables deduplicate identical stock without hiding conflicting quantities", RepeatedTables),
        ("Legacy part notes expose location and whole-number snapshot without re-entry", LegacyParts),
        ("Part photos copy into the shared library, preserve edits and archive replacements", PhotoStorage),
        ("Stockroom tables require exact stock identity, clear quantity headers and a unique site/bin match", StockroomTables),
        ("Stockroom reader submits the Safe2Cross search form with its hidden fields", SearchForms),
        ("Stockroom HTTP fetch stays bounded, rejects failures and honors cancellation", StockroomFetch)
    ];
    private static void LongItemIds()
    {
        string html = "<table><tr><th>ITEM ID</th><th>ITEM NAME</th><th>AVAILABLE</th><th>LOCATION</th><th>DESCRIPTION</th><th>PLANT</th></tr><tr><td><code>0273012250428</code></td><td>ADDITIVE, 1214G, FATTY ALCOHOL</td><td>51,071.52</td><td><span>TRUCK SHOP</span></td><td>Full specification including handling instructions.</td><td>HRM</td></tr><tr><td>02730122504280</td><td>Prefix distractor</td><td>999</td><td>Wrong</td><td>Wrong</td><td>HRM</td></tr><tr><td>NSOP</td><td>Outside metal processing</td><td>0</td><td></td><td>Service item</td><td>HRM</td></tr></table>";
        var snapshot = StockroomService.Parse(html); var part = StockroomService.ImportItem(snapshot, "0273012250428");
        Require(part.PartNumber == "0273012250428" && part.Description == "ADDITIVE, 1214G, FATTY ALCOHOL" && part.StockroomDescription == "Full specification including handling instructions." && part.ReportedQuantity == 51071.52m && part.BinLocation == "TRUCK SHOP" && part.Site == "HRM", "Long ID import lost source information or matched the prefix distractor.");
        Require(StockroomService.ImportItem(snapshot, "nsop").ReportedQuantity == 0 && StockroomService.IsItemId("AB-12.4/5_6"), "Alphanumeric/punctuated IDs or zero stock rejected.");
        Require(StockroomService.StockKey("00ABC") != StockroomService.StockKey("ABC") && StockroomService.StockKey("0115639") == StockroomService.StockKey("115639"), "Normalization changed nonnumeric identity or broke legacy numeric IDs.");
        var ids = StockroomService.ParseItemIds("0273012250428,NSOP\n0273012250428;nsop AB-12.4/5_6");
        Require(ids.SequenceEqual(["0273012250428", "NSOP", "AB-12.4/5_6"]), "Bulk input lost leading zeros or failed to deduplicate.");
        bool refused = false; try { StockroomService.ParseItemIds("valid invalid!"); } catch (InvalidOperationException) { refused = true; }
        Require(refused, "Invalid bulk IDs were silently accepted.");
    }
    private static void CatalogLookup()
    {
        var initial = new StockroomService.Snapshot([new("0115639", 2, "HRM", "A", "Available")], DateTimeOffset.UtcNow);
        var calls = new List<string>();
        Task<StockroomService.Snapshot> Search(string id, CancellationToken token)
        {
            calls.Add(id);
            return id switch
            {
                "0273012250428" => Task.FromResult(new StockroomService.Snapshot([new(id, 51071.52m, "HRM", "TRUCK SHOP", "Available"), new("999999", 999, "", "", "Available")], DateTimeOffset.UtcNow)),
                "NSOP" => Task.FromException<StockroomService.Snapshot>(new TimeoutException("Fixture failure")),
                _ => Task.FromResult(new StockroomService.Snapshot([new(id, 99, "", "", "Available")], DateTimeOffset.UtcNow) { QuantitiesConfirmed = false })
            };
        }
        var result = StockroomService.FetchCatalogAsync(["0115639", "0273012250428", "0273012250428", "NSOP", "UNKNOWN"], _ => Task.FromResult(initial), Search, CancellationToken.None).GetAwaiter().GetResult();
        Require(calls.SequenceEqual(["0273012250428", "NSOP", "UNKNOWN"]) && result.Rows.Count == 2 && StockroomService.Find(result, new PartRecord { PartNumber = "0273012250428" }, true)?.Quantity == 51071.52m, "Catalog refresh did not target only missing exact IDs.");
        Require(StockroomService.Find(result, new PartRecord { PartNumber = "0115639" }, true)?.Quantity == 2 && result.Reader.Contains("Fixture failure") && !result.Rows.Any(r => r.Stock == "999999" || r.Stock == "UNKNOWN"), "Partial failure hid a valid quantity or promoted unrelated/unconfirmed rows.");
        var ambiguous = initial with { Rows = [..initial.Rows, ..initial.Rows] }; calls.Clear();
        StockroomService.FetchCatalogAsync(["0115639"], _ => Task.FromResult(ambiguous), Search, CancellationToken.None).GetAwaiter().GetResult();
        Require(calls.Count == 0, "A targeted request could hide existing ambiguity.");
        using var cancel = new CancellationTokenSource(); bool canceled = false;
        try { StockroomService.FetchCatalogAsync(["NSOP"], _ => Task.FromResult(initial), (id, ct) => { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(initial); }, cancel.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { canceled = true; }
        Require(canceled, "Catalog cancellation returned stale results.");
    }
    private static void BlankPlantSetup()
    {
        string root = Directory.CreateTempSubdirectory("blank-plant-").FullName;
        try
        {
            string local = Path.Combine(root, "local"), share = Path.Combine(root, "new-plant");
            var data = new DataService(false, local); data.CreateBlankPlantLibrary(share, "Packaging");
            Require(data.IsReady && data.LoadCategories().SequenceEqual(["Packaging"]) && data.LoadGates().Count == 0 && data.LoadSystems().Count == 0 && data.LoadParts().Count == 0 && data.LoadHelpItems().Count == 0, "Blank plant contains sample inventory or is not ready.");
            data = new DataService(true, local);
            Require(data.SharedRoot == share && data.LoadParts().Count == 0, "Reopen imported Huntingdon parts into a blank plant.");
            string protectedFile = Path.Combine(share, "keep.txt"); File.WriteAllText(protectedFile, "Keep existing plant data");
            bool refused = false; try { data.CreateBlankPlantLibrary(share, "Overwrite"); } catch (IOException) { refused = true; }
            Require(refused && File.ReadAllText(protectedFile) == "Keep existing plant data" && data.LoadCategories().SequenceEqual(["Packaging"]), "Blank creation overwrote an existing folder.");
            var gate = new SafetyGate { Id = "new-gate", Category = "Packaging", AssetNumber = "BOX-01", Manufacturer = "Other" }; data.SaveGates([gate]);
            var unit = new SafetySystem { Id = "pilot", GateId = gate.Id, UnitNumber = "1", ManualIds = [] };
            var other = new SafetySystem { Id = "other", GateId = gate.Id, UnitNumber = "2", ManualIds = [] }; data.SaveSystems([unit, other]);
            string pdf = Path.Combine(root, "Approved drawing.pdf"); File.WriteAllBytes(pdf, SafetyGateSupport.TestFixtures.DrawingPdfFixture.TextPage(0));
            var manual = data.AddPilotPdf(unit.Id, pdf); File.Delete(pdf);
            Require(File.Exists(data.Resolve(manual.FilePath)) && manual.Title == "Approved drawing" && data.LoadSystems().Single(u => u.Id == unit.Id).ManualIds!.Contains(manual.Id), "Pilot PDF was not copied and assigned.");
            Require(data.LoadSystems().Single(u => u.Id == other.Id).ManualIds!.Count == 0 && !manual.AppliesTo(gate.Id, other.Id), "Pilot PDF changed another unit's assignment.");
            var before = File.ReadAllBytes(data.ManualsIndexFile); string bad = Path.Combine(root, "wrong.pdf"); File.WriteAllText(bad, "This is not a PDF");
            refused = false; try { data.AddPilotPdf(unit.Id, bad); } catch (InvalidDataException) { refused = true; }
            Require(refused && File.ReadAllBytes(data.ManualsIndexFile).SequenceEqual(before), "Invalid PDF changed shared records.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void PhoneGuides()
    {
        string root = Directory.CreateTempSubdirectory("phone-guides-").FullName;
        try
        {
            var data = new DataService(false, Path.Combine(root, "local")); data.SetSharedRoot(Path.Combine(root, "share")); data.InitializeSharedData();
            var units = data.LoadSystems(); var gates = data.LoadGates(); var unit = units.First();
            unit.MobileOfflineInstructions = "Check for a damaged bracket. Follow the approved replacement procedure.";
            unit.MobileSupportUrl = ""; data.SaveSystems(units);
            string picture = Path.Combine(root, "broken.png"); File.WriteAllBytes(picture, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAEElEQVR4nGP4z8AARAwQCgAf7gP9i18U1AAAAABJRU5ErkJggg=="));
            data.SaveHelpTopic(new HelpItem { Section = "Common Problems", Title = "Broken <bracket>", Body = "Replace safely. <script>alert(1)</script>" }, picture);
            data.SaveHelpTopic(new HelpItem { Section = "Common Problems", Title = "DRAFT-NOT-FOR-PHONES", Body = "Draft", Visible = false });
            string notesText = "NOTE-PRIVATE-EXCLUDED"; data.SaveSystemNotes(unit, notesText);
            string output = Path.Combine(root, "guides.zip"); var result = MobileSupportExporter.Export(data, output);
            using (var zip = System.IO.Compression.ZipFile.OpenRead(output))
            {
                string Read(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }
                string page = Read("unit-" + MobileSupportExporter.FileKey(unit.Id) + ".html");
                Require(result.Guides > 0 && result.Labels == 1 && page.Contains("data:image/png;base64,") && page.Contains("&lt;script&gt;") && !page.Contains("<script>") && !page.Contains(notesText) && !page.Contains("DRAFT-NOT-FOR-PHONES"), "Phone guide leaked notes/drafts, lost photo, or allowed HTML injection.");
                string qr = Read("qr-" + MobileSupportExporter.FileKey(unit.Id) + ".svg");
                string decoded = DecodeQrSvg(qr);
                Require(decoded.Contains(unit.MobileOfflineInstructions) && decoded.Contains(gates.Single(g => g.Id == unit.GateId).AssetDisplay), "Offline QR did not decode to the unit guidance.");
            }
            unit.MobileSupportUrl = "https://company.example/docs/unit.pdf?view=1&lang=en"; data.SaveSystems(units);
            MobileSupportExporter.Export(data, output, includeNotes: true);
            using (var zip = System.IO.Compression.ZipFile.OpenRead(output))
            {
                using var qr = new StreamReader(zip.GetEntry("qr-" + MobileSupportExporter.FileKey(unit.Id) + ".svg")!.Open());
                Require(DecodeQrSvg(qr.ReadToEnd()) == unit.MobileSupportUrl, "Document QR changed the approved link.");
                using var page = new StreamReader(zip.GetEntry("unit-" + MobileSupportExporter.FileKey(unit.Id) + ".html")!.Open());
                string guide = page.ReadToEnd();
                Require(guide.Contains(notesText), "Explicit notes export was ignored.");
                if (Environment.GetEnvironmentVariable("SAFETYGATE_PHONE_FIXTURE") is { Length: > 0 } fixture) File.WriteAllText(fixture, guide);
            }
            Require(!MobileSupportExporter.ValidLink("file:///Q:/manual.pdf") && !MobileSupportExporter.ValidLink("https://localhost/guide") && !MobileSupportExporter.ValidLink("https://user:pass@example.com"), "Invalid phone links accepted.");
            unit.MobileSupportUrl = "file:///Q:/manual.pdf"; data.SaveSystems(units);
            var other = units.First(u => u.Id != unit.Id);
            other.MobileOfflineInstructions = "Pilot-only scan text";
            data.SaveSystems(units);
            var pilotResult = MobileSupportExporter.Export(data, output, unitId: other.Id);
            using (var zip = System.IO.Compression.ZipFile.OpenRead(output))
            {
                Require(pilotResult.Guides == 1 && pilotResult.Labels == 1 &&
                    zip.GetEntry("unit-" + MobileSupportExporter.FileKey(unit.Id) + ".html") is null,
                    "Pilot export included another unit or failed on its invalid URL.");
                using var qr = new StreamReader(zip.GetEntry("qr-" + MobileSupportExporter.FileKey(other.Id) + ".svg")!.Open());
                Require(DecodeQrSvg(qr.ReadToEnd()).Contains(other.MobileOfflineInstructions), "Pilot QR lost its saved text.");
            }
            var before = File.ReadAllBytes(output); bool refused = false;
            try { MobileSupportExporter.Export(data, output); } catch (InvalidOperationException) { refused = true; }
            Require(refused && File.ReadAllBytes(output).SequenceEqual(before), "Failed export overwrote the previous package.");
            if (Environment.GetEnvironmentVariable("SAFETYGATE_QR_TEST_OUTPUT") is { Length: > 0 } qrFolder)
            {
                Directory.CreateDirectory(qrFolder);
                const string payload = "Safety Gate Support QR test\nOffline text works without a server or login.\nThis is a test label, not a live unit.";
                string svg = MobileSupportExporter.QrSvg(payload);
                Require(DecodeQrSvg(svg) == payload, "Downloadable test QR did not decode.");
                File.WriteAllText(Path.Combine(qrFolder, "offline-qr-test.svg"), svg);
                using var generator = new QRCoder.QRCodeGenerator(); using var qr = generator.CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.M);
                using var png = new QRCoder.PngByteQRCode(qr);
                File.WriteAllBytes(Path.Combine(qrFolder, "offline-qr-test.png"), png.GetGraphic(8));
                File.WriteAllText(Path.Combine(qrFolder, "READ-ME.txt"), "Open the PNG on your computer or print it, then scan with your phone camera. Expected text:\n\n" + payload + "\n\nThis test contains no company data and does not need Q:, a website or employee login. Camera text-QR handling varies; check the intended phones and label size.");
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private static string DecodeQrSvg(string svg)
    {
        var doc = System.Xml.Linq.XDocument.Parse(svg); var ns = doc.Root!.Name.Namespace;
        int n = int.Parse(doc.Root.Attribute("viewBox")!.Value.Split(' ')[2]); const int scale = 4;
        int size = n * scale; var pixels = Enumerable.Repeat((byte)255, size * size * 3).ToArray();
        foreach (var rect in doc.Root.Element(ns + "g")!.Elements(ns + "rect"))
        {
            int x = int.Parse(rect.Attribute("x")!.Value), y = int.Parse(rect.Attribute("y")!.Value);
            for (int yy = y * scale; yy < (y + 1) * scale; yy++) for (int xx = x * scale; xx < (x + 1) * scale; xx++)
                for (int c = 0; c < 3; c++) pixels[(yy * size + xx) * 3 + c] = 0;
        }
        return new ZXing.BarcodeReaderGeneric().Decode(pixels, size, size, ZXing.RGBLuminanceSource.BitmapFormat.RGB24)?.Text ?? throw new InvalidOperationException("Exported QR could not be decoded.");
    }
    private static void CommonProblems()
    {
        string root = Directory.CreateTempSubdirectory("common-problems-").FullName;
        try
        {
            var data = new DataService(false, Path.Combine(root, "local")); data.SetSharedRoot(Path.Combine(root, "share")); data.InitializeSharedData();
            int original = data.LoadHelpItems().Count;
            string source = Path.Combine(root, "broken.png"); File.WriteAllBytes(source, [1, 2, 3]);
            var topic = data.SaveHelpTopic(new HelpItem { Title = "Broken bracket", Body = "Replace the damaged bracket following the approved procedure.", Section = "Common Problems", SortOrder = 2 }, source);
            string firstPhoto = topic.PhotoPath;
            File.Delete(source);
            Require(File.Exists(data.Resolve(firstPhoto)), "Photo was not copied into shared storage.");
            var updated = data.SaveHelpTopic(new HelpItem { Id = topic.Id, Section = "Common Problems", Title = "Updated bracket", Body = "Updated instructions", Visible = false });
            Require(updated.PhotoPath == firstPhoto && data.LoadHelpItems().Count == original + 1, "Editing lost the photo or unrelated help.");
            Require(!data.LoadHelpItems().Single(h => h.Id == topic.Id).Visible, "Hidden state was not saved.");
            string another = Path.Combine(root, "another.png"); File.WriteAllBytes(another, [4, 5, 6]);
            data.SaveHelpTopicPhotos(updated, [firstPhoto, another]);
            var multiple = data.LoadHelpItems().Single(h => h.Id == topic.Id);
            Require(multiple.Photos.Count == 2 && multiple.PhotoPath == firstPhoto, "Multiple photos or legacy cover were lost.");
            data.SaveHelpTopicPhotos(multiple, multiple.Photos.Reverse().ToList());
            Require(data.LoadHelpItems().Single(h => h.Id == topic.Id).Photos.Last() == firstPhoto, "Cover reorder did not persist.");
            int photoFiles = Directory.GetFiles(Path.Combine(data.SharedRoot, "HelpPhotos")).Length;
            bool rolledBack = false;
            try { data.SaveHelpTopicPhotos(multiple, [another, Path.Combine(root, "unsupported.exe")]); } catch (InvalidDataException) { rolledBack = true; }
            Require(rolledBack && Directory.GetFiles(Path.Combine(data.SharedRoot, "HelpPhotos")).Length == photoFiles && data.LoadHelpItems().Single(h => h.Id == topic.Id).Photos.Count == 2, "Failed multi-photo save left partial files or metadata.");
            data.SaveHelpTopic(updated, removePhoto: true);
            Require(data.LoadHelpItems().Single(h => h.Id == topic.Id).PhotoPath.Length == 0 && File.Exists(data.Resolve(firstPhoto)), "Photo removal broke backed-up records.");
            bool refused = false; try { data.SaveHelpTopic(new HelpItem { Section = "Common Problems", Title = "No instructions" }); } catch (InvalidOperationException) { refused = true; }
            Require(refused && data.LoadHelpItems().Count == original + 1, "Incomplete fix instructions were published.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void UnconfirmedInventory()
    {
        string html = "<div class='inventory-container'><input class='search-input'></div><table>" + Headers + Row("0115639", "99") + "</table>";
        int searches = 0;
        Task<StockroomService.Snapshot> Fail(string _, CancellationToken token)
        { searches++; throw new TimeoutException("Server search was not acknowledged."); }
        var result = StockroomService.FetchAsync("https://inventory.invalid/parts", CancellationToken.None,
            new Reply(HttpStatusCode.OK, html), ["0115639"], Fail).GetAwaiter().GetResult();
        var part = new PartRecord { PartNumber = "0115639" };
        Require(searches == 1, "A complete-looking initial Blazor table bypassed server search.");
        Require(!result.QuantitiesConfirmed && result.Rows.Single().Quantity == 99 && result.TableSample.Length > 0,
            "Failed search lost diagnostic rows or marked them confirmed.");
        Require(StockroomService.Find(result, part, true) is null && StockroomService.Find(result, part) is null,
            "Unconfirmed initial inventory was exposed as a current quantity.");
        Require(result.Reader.Contains("Server search was not acknowledged"), "Failure detail was lost.");
        var confirmed = StockroomService.FetchAsync("https://inventory.invalid/parts", CancellationToken.None,
            new Reply(HttpStatusCode.OK, html), ["0115639"], (_, _) => Task.FromResult(
                new StockroomService.Snapshot([new("0115639", 0, "", "NEW BIN", "Available")], DateTimeOffset.UtcNow))).GetAwaiter().GetResult();
        Require(StockroomService.Find(confirmed, part, true)?.Quantity == 0, "Confirmed zero stock was lost after retry.");
        using var cancel = new CancellationTokenSource();
        bool canceled = false;
        try
        {
            StockroomService.FetchAsync("https://inventory.invalid/parts", cancel.Token, new Reply(HttpStatusCode.OK, html),
                ["0115639"], (_, token) => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(confirmed); }).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { canceled = true; }
        Require(canceled, "User cancellation was converted to diagnostic success.");
    }
    private static void ItemImport()
    {
        string html = "<table><tr><th>Item ID</th><th>Item name</th><th>Description</th><th>Available</th><th>Bin</th><th>Plant</th><th>Manufacturer</th><th>Photo</th></tr><tr><td>0115639</td><td>Yellow module</td><td>Replacement indicator</td><td>0</td><td>K-12</td><td>HRM</td><td>Banner</td><td><img src='/photos/115639.png'></td></tr></table>";
        var snapshot = StockroomService.Parse(html);
        var part = StockroomService.ImportItem(snapshot, "115639");
        Require(part.Description == "Yellow module" && part.Notes == "Replacement indicator" && part.Manufacturer == "Banner" && part.BinLocation == "K-12" && part.Site == "HRM" && part.ReportedQuantity == 0, "Import lost supplied stockroom metadata or zero stock.");
        Require(snapshot.Rows.Single().Photo == "/photos/115639.png", "Photo source lost.");
        foreach (var bad in new[] { snapshot with { QuantitiesConfirmed = false }, snapshot with { Rows = [..snapshot.Rows, ..snapshot.Rows] }, snapshot with { Rows = [] } })
        {
            bool refused = false; try { StockroomService.ImportItem(bad, "115639"); } catch (InvalidOperationException) { refused = true; }
            Require(refused, "Unconfirmed, ambiguous or missing item was imported.");
        }
        string root = Directory.CreateTempSubdirectory("part-import-").FullName;
        try
        {
            var data = new DataService(false, Path.Combine(root, "local")); data.SetSharedRoot(Path.Combine(root, "share")); data.InitializeSharedData();
            var old = data.LoadParts().Single(p => StockroomService.StockKey(p.PartNumber) == "115639");
            old.PhotoPath = "keep.png"; old.DocumentPath = "keep.pdf"; old.AppliesToUnitIds = ["unit-one"];
            var parts = data.LoadParts(); parts[parts.FindIndex(p => p.Id == old.Id)] = old; data.SaveParts(parts);
            var saved = data.ImportStockroomPart(part);
            Require(saved.Id == old.Id && saved.PhotoPath == "keep.png" && saved.DocumentPath == "keep.pdf" && saved.AppliesToUnitIds.SequenceEqual(["unit-one"]), "Reimport overwrote identity or local assignments/files.");
            data.ImportStockroomPart(part);
            Require(data.LoadParts().Count(p => StockroomService.StockKey(p.PartNumber) == "115639") == 1, "Repeated import duplicated an Item ID.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void PlantInventories()
    {
        Require(StockroomPlant.Find("02")?.Name == "Huntingdon" && StockroomPlant.Find("04")?.Url.EndsWith("/inventory/04") == true && StockroomPlant.Find("05")?.Name == "Newport" && StockroomPlant.Find("unknown") is null, "Plant mappings differ from the portal.");
        var snapshot = new StockroomService.Snapshot([new("0115639", 12, "SAL", "SAL BIN", "Quantity")], DateTimeOffset.UtcNow);
        var part = new PartRecord { PartNumber = "0115639", Site = "HRM", BinLocation = "TOP K-12" };
        Require(StockroomService.Find(snapshot, part, plantScoped: true)?.Quantity == 12 && StockroomService.Find(snapshot, part) is null, "Legacy snapshot site leaked into selected-plant matching.");
        var duplicates = snapshot with { Rows = [..snapshot.Rows, new("0115639", 6, "HRM", "TOP K-12", "Quantity")] };
        Require(StockroomService.Find(duplicates, part, plantScoped: true) is null, "Ambiguous stocks were guessed from an old plant snapshot.");
    }
    private static void UnitParts()
    {
        string root = Directory.CreateTempSubdirectory("unit-parts-").FullName;
        try
        {
            var data = new DataService(false, Path.Combine(root, "local")); data.SetSharedRoot(Path.Combine(root, "share")); data.InitializeSharedData();
            var part = new PartRecord { Id = "one", ReportedQuantity = 9 }; data.SaveParts([part]);
            data.SaveSystems([new() { Id = "a", DisplayName = "Preserve me" }, new() { Id = "b", ReplacementPartIds = ["one"] }]);
            data.SaveUnitReplacementParts("a", ["one", "one", "deleted"]);
            var units = data.LoadSystems(); Require(units.Single(u => u.Id == "a").ReplacementPartIds!.SequenceEqual(["one"]) && units.Single(u => u.Id == "a").DisplayName == "Preserve me" && units.Single(u => u.Id == "b").ReplacementPartIds!.SequenceEqual(["one"]), "Unit selection lost data or accepted deleted/duplicate parts.");
            data.SaveManualRecords([new() { Id = "manual-one", Title = "Reference" }]);
            data.SaveUnitManuals("a", ["manual-one", "deleted"]);
            Require(data.LoadSystems().Single(u => u.Id == "a").ManualIds!.SequenceEqual(["manual-one"]) && data.LoadSystems().Single(u => u.Id == "a").ReplacementPartIds!.SequenceEqual(["one"]), "Manual choices overwrote parts or retained deleted manuals.");
            data.SaveUnitReplacementParts("a", []); Require(data.LoadSystems().Single(u => u.Id == "a").ReplacementPartIds is { Count: 0 }, "Empty selection reverted to all parts.");
            Require(part.AvailableDisplay == "Available: unknown", "Reported quantity was treated as available stock.");
            part.SetInventory(3, "LIVE BIN"); Require(part.AvailableDisplay == "Available: 3" && part.AvailableColor == "#16703C" && part.InventoryLocationDisplay == "LIVE BIN", "Live positive availability was not green.");
            part.SetInventory(0); Require(part.AvailableDisplay == "Available: 0" && part.AvailableColor == "#BA2525", "Zero availability was not red.");
            part.SetInventory(null); Require(part.AvailableDisplay == "Available: unknown" && part.QuantityValue == 9, "Failed refresh retained live stock or changed snapshot.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void RepeatedTables()
    {
        var available = StockroomService.Parse("<table><tr><th>Item ID</th><th>On hand</th><th>Available</th></tr><tr><td>0115639</td><td>9</td><td>2</td></tr></table>");
        Require(available.Rows.Single().Quantity == 2, "On-hand quantity was shown instead of explicit available quantity.");
        string table = "<table>" + Headers + Row("0115639", "7") + "</table>";
        var result = StockroomService.Parse(table + table);
        Require(result.Rows.Count == 1 && StockroomService.Find(result, new PartRecord { PartNumber = "0115639" }, true)?.Quantity == 7, "Desktop/mobile copies hid a valid quantity.");
        var conflict = StockroomService.Parse(table + "<table>" + Headers + Row("0115639", "8") + "</table>");
        Require(StockroomService.Find(conflict, new PartRecord { PartNumber = "0115639" }, true) is null, "Conflicting quantities were guessed.");
        var lots = StockroomService.Parse("<table><tr><th>Item ID</th><th>Available</th><th>Lot</th></tr><tr><td>0115639</td><td>7</td><td>A</td></tr><tr><td>0115639</td><td>7</td><td>B</td></tr></table>");
        Require(StockroomService.Find(lots, new PartRecord { PartNumber = "0115639" }, true) is null, "Different lot rows were merged into a false quantity.");
        Require(result.TableSample.Length > 0 && result.TableSample.Any(row => row.Contains("0115639")), "Diagnostic table sample missing.");
    }
    private static void LegacyParts()
    {
        var part = new PartRecord { Description = "Yellow module", PartNumber = "0115639", Notes = "Bin location: TOP K-12\nReported quantity: 5.00 (stock snapshot)\nSite: HRM\nReference: SG-TL70-Y" };
        Require(part.LocationDisplay == "TOP K-12" && part.QuantityDisplay == "5" && part.SiteValue == "HRM", "Imported notes did not populate new detail fields.");
        Require(part.Display.IndexOf("Yellow module") < part.Display.IndexOf("TOP K-12") && part.Display.EndsWith("(0115639)"), "Part display order is incorrect.");
        Require(!part.NotesDisplay.Contains("5.00") && part.NotesDisplay.Contains("SG-TL70-Y"), "Detail notes repeated decimal quantities or lost the reference.");
        part.ReportedQuantity = 0; Require(part.QuantityDisplay == "0", "A reported zero was hidden.");
    }
    private static void PhotoStorage()
    {
        string root = Directory.CreateTempSubdirectory("part-photo-check-").FullName;
        try
        {
            var data = new DataService(false, Path.Combine(root, "settings")); data.SetSharedRoot(Path.Combine(root, "share")); data.InitializeSharedData();
            var part = new PartRecord { Id = "one", PartNumber = "0115639", Description = "Site edited", BinLocation = "TOP K-12", ReportedQuantity = 5, Notes = "Keep existing details" };
            var other = new PartRecord { Id = "two", Description = "Other part" }; data.SaveParts([part, other]);
            string source = Path.Combine(root, "reference.png"); File.WriteAllText(source, "original-photo-bytes");
            var saved = data.SavePartPhoto(part.Id, source); string oldPhoto = data.Resolve(saved.PhotoPath);
            Require(File.ReadAllText(oldPhoto) == "original-photo-bytes" && File.Exists(source), "Photo upload changed or moved the original.");
            var changed = data.LoadParts(); changed.Single(p => p.Id == part.Id).Description = "Newer edit from another PC"; data.SaveParts(changed);
            File.WriteAllText(source, "replacement-photo-bytes"); saved = data.SavePartPhoto(part.Id, source);
            Require(saved.Description == "Newer edit from another PC" && saved.BinLocation == "TOP K-12" && saved.ReportedQuantity == 5 && data.LoadParts().Count == 2, "Photo replacement overwrote inventory edits.");
            Require(!File.Exists(oldPhoto) && File.ReadAllText(Path.Combine(Path.GetDirectoryName(oldPhoto)!, "Archive", Path.GetFileName(oldPhoto))) == "original-photo-bytes", "Prior photo was not archived.");
            Require(File.ReadAllText(data.Resolve(saved.PhotoPath)) == "replacement-photo-bytes", "Photo path did not persist.");
        }
        finally { Directory.Delete(root, true); }
    }
    private const string Headers = "<tr><th>Stock number</th><th>Description</th><th>Qty on hand</th><th>Bin location</th><th>Site</th></tr>";
    private static string Row(string stock, string quantity, string bin = "TOP K-12", string site = "HRM") => $"<tr><td><a href='item'>{stock}</a></td><td>Module &amp; base</td><td>{quantity}</td><td>{bin}</td><td>{site}</td></tr>";
    private static void StockroomTables()
    {
        var part = new PartRecord { PartNumber = "0115639", BinLocation = "TOP K-12", Site = "HRM", ReportedQuantity = 5 };
        var snapshot = StockroomService.Parse("<table>" + Headers + Row("115639", "9.00") + Row("01156390", "999") + Row("0115639", "18", site: "OTHER") + "</table>");
        Require(StockroomService.Find(snapshot, part)?.Quantity == 9 && part.ReportedQuantity == 5, "Stock identity/site lookup failed or changed the reported snapshot.");
        snapshot = StockroomService.Parse("<table>" + Headers + Row("0115639", "0.00") + "</table>"); Require(StockroomService.Find(snapshot, part)?.Quantity == 0, "An actual zero quantity was lost.");
        snapshot = StockroomService.Parse("<table>" + Headers + Row("0115639", "9") + Row("0115639", "7") + "</table>"); Require(StockroomService.Find(snapshot, part) is null, "Conflicting duplicate inventory rows became a current quantity.");
        Require(StockroomService.Parse("<table>" + Row("0115639", "9") + "</table>").Rows.Count == 0, "A headerless table was guessed.");
        Require(StockroomService.Parse("<table>" + Headers + Row("0115639", "5,00") + "</table>").Rows.Count == 0, "Ambiguous numeric punctuation was accepted as 500.");
    }
    private sealed class Reply(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Requests++; Require(request.Method == HttpMethod.Get, "Stock lookup sent a write request."); return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "text/html") }); }
    }
    private sealed class SearchReply(bool post) : HttpMessageHandler
    {
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (Requests == 1) return new(HttpStatusCode.OK) { Content = new StringContent("<table>" + Headers + Row("0999999", "9") + "</table><form action='/inventory/search?site=HRM' method='" + (post ? "post" : "get") + "'><input type='hidden' name='token' value='abc&amp;123'><input name='keyword' type='search'><input type='submit' name='find' value='Search'></form>") };
            Require(request.RequestUri!.Host == "inventory.invalid" && request.RequestUri.AbsolutePath == "/inventory/search", "Search used the wrong form action.");
            string parameters = post ? await request.Content!.ReadAsStringAsync(token) : request.RequestUri.Query;
            Require(request.Method == (post ? HttpMethod.Post : HttpMethod.Get) && parameters.Contains("keyword=Safe2Cross") && parameters.Contains("token=abc%26123"), "Search query or hidden form fields were lost.");
            return new(HttpStatusCode.OK) { Content = new StringContent("<table>" + Headers + Row("0117719", "0.00") + "</table>") };
        }
    }
    private static void SearchForms()
    {
        foreach (bool post in new[] { false, true })
        {
            var handler = new SearchReply(post);
            var result = StockroomService.FetchAsync("https://inventory.invalid/storeroom.html", CancellationToken.None, handler).GetAwaiter().GetResult();
            Require(handler.Requests == 2 && result.Rows.Single().Stock == "0117719" && result.Rows.Single().Quantity == 0, "Search results did not preserve a zero-stock part.");
        }
    }
    private static void StockroomFetch()
    {
        var reply = new Reply(HttpStatusCode.OK, "<table>" + Headers + Row("0115639", "4") + "</table>");
        var snapshot = StockroomService.FetchAsync("https://inventory.invalid/parts", CancellationToken.None, reply).GetAwaiter().GetResult();
        Require(reply.Requests == 1 && snapshot.Rows.Single().Quantity == 4, "A single read did not produce a stock snapshot.");
        bool refused = false; try { StockroomService.FetchAsync("https://inventory.invalid/parts", CancellationToken.None, new Reply(HttpStatusCode.Unauthorized, "login")).GetAwaiter().GetResult(); } catch (HttpRequestException) { refused = true; } Require(refused, "A login failure became inventory.");
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); refused = false;
        try { StockroomService.FetchAsync("https://inventory.invalid/parts", cancel.Token, new Reply(HttpStatusCode.OK, "")).GetAwaiter().GetResult(); } catch (OperationCanceledException) { refused = true; } Require(refused, "Canceled stock checks continued.");
        refused = false; try { StockroomService.Parse(new string('x', 4 * 1024 * 1024 + 1)); } catch (InvalidDataException) { refused = true; } Require(refused, "Oversized stockroom page was accepted.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
