using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;
using SafetyGateSupport.TestFixtures;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using ZXing;

namespace SafetyGateSupport.Validation;

internal static class OperatorDriveChecks
{
    internal static IEnumerable<(string Name, Action Check)> Cases =>
    [
        ("Drive publishing creates and updates unit folders, guides and original electrical PDFs in place", () => PublishLibraryAsync().GetAwaiter().GetResult()),
        ("The previous pilot's Drive folder, drawing and notes IDs survive upgrade and a renamed upload", () => ImportPilotAsync().GetAwaiter().GetResult()),
        ("Shared drawing coverage updates every linked Drive guide and clears removed drawings", () => SharedDrawingPublicationAsync().GetAwaiter().GetResult()),
        ("A shared supplemental PDF appears in exactly the selected units and updates their guides in place", () => SharedGuidePdfAsync().GetAwaiter().GetResult()),
        ("Removing a guide PDF affects one unit, archives the last copy and rebuilds the Drive guide in place", () => RemoveGuidePdfAsync().GetAwaiter().GetResult()),
        ("Spare parts follow unit assignments and keep each item's last confirmed scan in the original Drive PDF", () => SparePartsAsync().GetAwaiter().GetResult()),
        ("Operator notes sync to and from the phone document while preserving previous edits", SharedNotesSyncAsyncTest),
        ("Light column configurations add a drawn chart and their PDF sheet to covered units' QR guides only", () => LightColumnAsync().GetAwaiter().GetResult()),
        ("Batch QR sheets contain every stable unit URL across multiple print pages", CheckQrLabels),
        ("Larger electrical PDFs open a resumable upload and preserve their file ID on update", () => ResumablePdfAsync().GetAwaiter().GetResult())
    ];

    private static async Task LightColumnAsync()
    {
        using var library = new Fixture();
        using var drive = new FakeDrive();
        using var http = new HttpClient(drive);
        var publisher = new OperatorDrivePublisher(new(http, _ => Task.FromResult("fixture")), (_, _) => Task.FromResult<UnitGuidePhoto?>(null));
        var state = new OperatorPublishingState { PublishAll = true };
        var first = library.Units[0];
        var second = library.Units[1];
        string signal = Path.Combine(library.DirectoryPath, "Column A.signal");
        File.WriteAllBytes(signal, SignalFixture());
        string sheet = Path.Combine(library.DirectoryPath, "column sheet.pdf");
        File.WriteAllBytes(sheet, DrawingPdfFixture.TextPage(90));
        var saved = library.Data.SaveSignalPair(library.Gate, first, signal, sheet);

        var run = await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        Require(run.Issues == 0, "Light column publishing failed: " + string.Join("; ", state.Units.Values.Select(u => u.Error).OfType<string>()));
        string guideId = state.Units[first.Id].Guide!.Id;
        using (var guide = PdfDocument.Open(drive.Items[guideId].Bytes!))
        {
            string chart = guide.GetPage(2).Text;
            Require(guide.NumberOfPages == 3 && guide.GetPage(1).Text.Contains("Light column") &&
                guide.GetPage(1).Text.Contains("Light column configuration sheet"), "The guide should list the light column chart and its sheet.");
            Require(chart.Contains("What the light means") && chart.Contains("Input 1 (white)") && chart.Contains("Gate open") &&
                chart.Contains("blink") && chart.Contains("sound"), "The chart should name the inputs, wires, states, blinking and sound.");
            Require(guide.GetPage(2).Letters.Count > 0 && guide.GetPage(3).Text.Length > 0, "The configuration sheet should follow the chart.");
        }
        using (var other = PdfDocument.Open(drive.Items[state.Units[second.Id].Guide!.Id].Bytes!))
            Require(!other.GetPage(1).Text.Contains("Light column"), "A unit without the configuration should not show it.");

        // A copy of the sheet already attached to the guide by hand is not added twice; the guide keeps its file ID.
        OperatorPublishingState.SaveGuidePdf(library.Data, library.Root, library.Gate.Id, [first.Id], saved.Pdf, null);
        var again = await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        using (var guide = PdfDocument.Open(drive.Items[guideId].Bytes!))
            Require(again.Issues == 0 && state.Units[first.Id].Guide!.Id == guideId && guide.NumberOfPages == 3 &&
                !guide.GetPage(1).Text.Contains("Light column configuration sheet"), "A hand-attached sheet should replace the automatic copy, not duplicate it.");
    }

    // A minimal schema-5, nine-segment configuration as the Siemens configurator writes it.
    internal static byte[] SignalFixture()
    {
        using var bytes = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Write(string name, object value)
            {
                using var stream = zip.CreateEntry(name).Open();
                JsonSerializer.Serialize(stream, value);
            }
            Write("configurator.conf", new Dictionary<string, object>
            {
                ["version"] = 5, ["device"] = new { Variant = 21, SegmentCount = 9, HasSound = true }, ["mode"] = 4,
                ["stages"] = Array.Empty<object>(),
                ["states"] = new object[] { new Dictionary<string, object> { ["<Name>k__BackingField"] = "Gate open", ["<PinCombination>k__BackingField"] = new { state = new[] { true, false, false, false, false, false } } } },
                ["<Pins>k__BackingField"] = new object[] { new Dictionary<string, object> { ["<Number>k__BackingField"] = 1, ["<Name>k__BackingField"] = "Input 1", ["<Color>k__BackingField"] = "WH", ["<Description>k__BackingField"] = "" } }
            });
            for (int state = 0; state < 64; state++)
                Write($"state_config_{state}.json", new
                {
                    Segments = Enumerable.Range(0, 9).Select(segment => new
                    {
                        r = state == 2 ? 134 : 0, g = state is 1 or 2 ? (state == 1 ? 255 : 120) : 0, b = 0,
                        brightness = state is 1 or 2 && segment < 3 ? 255 : 0, lightEffect = state == 2 ? 50 : 10
                    }).ToArray(),
                    Sound = new { mode = state == 4 ? 2 : 0, volume = state == 4 ? 15 : 0, frequency01 = 0, frequency02 = 0, frequency03 = 0, repeatCount = 0, repeatPause = 0, cyclePause = 0, holdTime = 0 }
                });
        }
        return bytes.ToArray();
    }

    private static async Task PublishLibraryAsync()
    {
        using var library = new Fixture();
        using var drive = new FakeDrive();
        using var http = new HttpClient(drive);
        var photo = new UnitGuidePhoto(Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAAQABgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD1GiiioMAooooA/9k="), 24, 16);
        string photoPath = Path.Combine(library.Root, "fixture-identification.jpg");
        File.WriteAllBytes(photoPath, photo.Jpeg);
        library.Units[0].PrimaryPhoto = library.Data.ToRelative(photoPath);
        library.Data.SaveSystems([.. library.Units]);
        var publisher = new OperatorDrivePublisher(new(http, _ => Task.FromResult("fixture")),
            (path, _) => Task.FromResult<UnitGuidePhoto?>(path == photoPath ? photo : null));
        var state = new OperatorPublishingState { PublishAll = true };
        var first = library.AddPair(library.Units[0], "Wiring A", 0);
        string attachment = Path.Combine(OperatorPublishingState.AttachmentsPath(library.Root, library.Units[0].Id), "Signal column configuration.pdf");
        OperatorPublishingState.WriteFile(attachment, DrawingPdfFixture.TextPage(90, 90));
        drive.LoseNextFolderReply = true;
        bool interrupted = false;
        try { await publisher.RunAsync(library.Data, library.Root, state, false, null, default); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.ServiceUnavailable) { interrupted = true; }
        Require(interrupted && state.Root?.Pending == true, "A lost folder confirmation should retain the reserved ID for retry.");
        state = OperatorPublishingState.Load(library.Root);
        string reservedRoot = state.Root!.Id;
        var initial = await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        Require(initial.Published == 2 && state.Root.Id == reservedRoot && !state.Root.Pending && drive.FolderWithName(OperatorPublishingState.DefaultFolderName).Id == reservedRoot,
            "The app should create the root once and reuse it after a lost response.");

        var unit = state.Units[library.Units[0].Id];
        var other = state.Units[library.Units[1].Id];
        Require(unit.Folder!.Id != other.Folder!.Id && other.Electrical is null && other.Guide is not null,
            "Every unit should have a separate QR folder and guide, including units without an electrical PDF.");
        Require(drive.Items[unit.Electrical!.Id].Bytes!.SequenceEqual(File.ReadAllBytes(first.PdfPath)),
            "The electrical PDF should retain the original PDF bytes.");
        using (var guide = PdfDocument.Open(drive.Items[unit.Guide!.Id].Bytes!))
        {
            Require(guide.GetPage(1).GetImages().Count() == 1, "The cover should include the unit identification photo.");
            Require(guide.NumberOfPages == 3, "The guide should contain a cover, the drawing and the explicitly attached PDF.");
            Require(guide.GetPage(1).Text.Contains("1066", StringComparison.Ordinal) &&
                guide.GetPage(1).Text.Contains("Gränges", StringComparison.Ordinal) &&
                guide.GetPage(1).Text.Contains("Signal column", StringComparison.Ordinal) &&
                guide.GetPage(2).Text.Contains("ELECTRICAL SCHEMATIC", StringComparison.Ordinal), "The cover and source page text should be readable.");
            using var standalone = PdfDocument.Open(File.ReadAllBytes(first.PdfPath));
            Require(guide.GetPage(2).Width == standalone.GetPage(1).Width && guide.GetPage(2).Height == standalone.GetPage(1).Height,
                "The standalone drawing page dimensions should be preserved in the guide.");
            using var supplemental = PdfDocument.Open(File.ReadAllBytes(attachment));
            Require(guide.GetPage(3).Rotation.Equals(supplemental.GetPage(1).Rotation),
                "An attached drawing should keep its page rotation.");
            Require(guide.TryGetBookmarks(out var bookmarks) && bookmarks.Roots.Count == 3, "Each included document should have a bookmark.");
        }
        using (var emptyUnitGuide = PdfDocument.Open(drive.Items[other.Guide!.Id].Bytes!))
            Require(emptyUnitGuide.NumberOfPages == 1 && emptyUnitGuide.GetPage(1).Text.Contains("No electrical PDF"),
                "The other unit must never receive the first unit's drawing or attachments.");

        string originalFolder = unit.Folder.Id, originalElectrical = unit.Electrical.Id, originalGuide = unit.Guide.Id;
        string originalNotes = unit.NotesId!;
        int requestCount = drive.Requests;
        var unchanged = await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        Require(unchanged.Current == 2 && drive.Requests == requestCount, "Polling unchanged units should avoid Drive API requests.");

        var second = library.AddPair(library.Units[0], "Wiring B", 180, first.PdfPath);
        drive.LoseNextPdfReply = true;
        var pending = await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        Require(pending.Issues == 1 && OperatorPublishingState.Load(library.Root).Units[library.Units[0].Id].Electrical?.PendingChecksum is not null,
            "Persist the expected revision before uploading so restart can recover a lost confirmation.");
        state = OperatorPublishingState.Load(library.Root);
        var revised = await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        unit = state.Units[library.Units[0].Id];
        Require(revised.Published == 1 && unit.Folder!.Id == originalFolder && unit.Electrical!.Id == originalElectrical &&
            unit.Guide!.Id == originalGuide && unit.NotesId == originalNotes &&
            drive.Items[originalElectrical].Bytes!.SequenceEqual(File.ReadAllBytes(second.PdfPath)), "A normal renamed pair upload should update the existing Drive files in place.");
        Require(drive.Items.Values.Count(f => f.Parent == originalFolder) == 3, "Publishing the revision must not create duplicate PDFs or notes.");

        var third = library.AddPair(library.Units[0], "Wiring C", 90, second.PdfPath);
        drive.Items[originalElectrical].Bytes = DrawingPdfFixture.TextPage(270);
        var conflict = await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        Require(conflict.Issues == 1 && !drive.Items[originalElectrical].Bytes!.SequenceEqual(File.ReadAllBytes(third.PdfPath)),
            "A conflicting Drive edit should be preserved while the source remains on Q.");
    }

    private static async Task ImportPilotAsync()
    {
        using var library = new Fixture();
        using var drive = new FakeDrive();
        using var http = new HttpClient(drive);
        var publisher = new OperatorDrivePublisher(new(http, _ => Task.FromResult("fixture")), (_, _) => Task.FromResult<UnitGuidePhoto?>(null));
        var drawing = library.AddPair(library.Units[0], "Pilot wiring", 0);
        byte[] first = File.ReadAllBytes(drawing.PdfPath);
        var root = drive.Create("pilot-root", "Pilot Drive root", FakeDrive.Folder, null, driveId: "shared-drive");
        var asset = drive.Create("pilot-asset", "Asset 1066", FakeDrive.Folder, root.Id, driveId: root.DriveId);
        var unit = drive.Create("pilot-unit", "Unit 2", FakeDrive.Folder, asset.Id, driveId: root.DriveId);
        var pdf = drive.Create("pilot-pdf", "Pilot wiring.pdf", "application/pdf", unit.Id, first, root.DriveId);
        var notes = drive.Create("pilot-notes", "Operator notes", FakeDrive.Doc, unit.Id, driveId: root.DriveId);
        string legacyPath = Path.Combine(library.DirectoryPath, "operator-drive-pilot.json");
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new
        {
            SharedRoot = library.Root, GateId = library.Gate.Id, UnitId = library.Units[0].Id,
            Asset = "Asset 1066", Unit = "Unit 2", RelativePdf = library.Data.ToRelative(drawing.PdfPath),
            DriveRoot = root.Id, FileId = pdf.Id, LastHash = Convert.ToHexStringLower(MD5.HashData(first)),
            FolderId = unit.Id, FolderLink = $"https://drive.google.com/drive/folders/{unit.Id}", NotesId = notes.Id
        }));

        // Simulate replacing the pre-existing, unpaired source during upgrade, before the new publisher first runs.
        var records = library.Data.LoadResources();
        records[0].DrawingPairs.Clear();
        library.Data.SaveResources(records);
        var replacement = library.AddPair(library.Units[0], "Pilot wiring rev B", 180, drawing.PdfPath);
        var state = new OperatorPublishingState { PublishAll = true };
        Require(state.ImportPilot(library.Root, legacyPath) && !state.ImportPilot(library.Root, legacyPath), "Import should only run once.");
        var summary = await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        var imported = state.Units[library.Units[0].Id];
        Require(summary.Issues == 0 && state.Root!.Id == root.Id && imported.Folder!.Id == unit.Id
            && imported.Electrical!.Id == pdf.Id && imported.NotesId == notes.Id &&
            drive.Items[pdf.Id].Bytes!.SequenceEqual(File.ReadAllBytes(replacement.PdfPath)),
            "Upgrade and an unpaired-source rename should keep the old folder, file and notes IDs.");
        Require(state.Units[library.Units[1].Id].Folder!.Id != unit.Id, "Bulk publish should create additional folders under the existing root.");
    }

    private static void SharedNotesSyncAsyncTest() => VerifySharedNotesAsync().GetAwaiter().GetResult();

    private static async Task VerifySharedNotesAsync()
    {
        using var library = new Fixture();
        using var handler = new FakeDrive();
        using var http = new HttpClient(handler);
        Func<CancellationToken, Task<string>> authorize = _ => Task.FromResult("fixture");
        var drive = new OperatorDriveClient(http, authorize);
        var sync = new OperatorNotesSynchronizer(drive, new OperatorNotesClient(http, authorize));
        var publisher = new OperatorDrivePublisher(drive, (_, _) => Task.FromResult<UnitGuidePhoto?>(null));
        var state = new OperatorPublishingState { PublishAll = true, SyncSharedNotes = true };
        var unit = library.Units[0];
        library.Data.EnsureUnitFolders(library.Gate, unit);
        library.Data.SaveSystems([.. library.Units]);
        library.Data.SaveSystemNotes(unit, "Existing desktop note");
        var result = await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        Require(result.Published == 2, "The unit folders and the Google notes documents should exist.");
        var entry = state.Units[unit.Id];
        string doc = entry.NotesId!;
        handler.EditNotes(doc, "Existing phone note");

        handler.NotesApiEnabled = false;
        await sync.RunAsync(library.Data, library.Root, state, default);
        Require(state.NotesError?.StartsWith("Enable the Google Docs API") == true &&
            library.Data.LoadSystemNotes(unit) == "Existing desktop note", "Missing API setup should preserve both versions.");
        handler.NotesApiEnabled = true;
        await sync.RunAsync(library.Data, library.Root, state, default);
        string common = "Existing desktop note\n\nExisting phone note";
        Require(handler.Items[doc].Notes == common && library.Data.LoadSystemNotes(unit) == common &&
            state.NotesError is null, "First sync should include the pre-existing Q and Drive notes.");

        handler.EditNotes(doc, common + "\nPhone update");
        await sync.RunAsync(library.Data, library.Root, state, default);
        string received = common + "\nPhone update";
        Require(library.Data.LoadSystemNotes(unit) == received, "A phone edit did not reach the Q notes file.");
        string archive = Path.Combine(Path.GetDirectoryName(library.Data.SystemNotesPath(unit))!, "Archive");
        Require(Directory.GetFiles(archive).Any(path => File.ReadAllText(path) == common), "Sync should retain the previous Q notes.");

        string deskUpdate = received + "\nDesktop update";
        library.Data.SaveSystemNotes(unit, deskUpdate);
        handler.LoseNextNotesReply = true;
        await sync.RunAsync(library.Data, library.Root, state, default);
        Require(entry.NotesCheckpoint?.Transfer is not null && handler.Items[doc].Notes == deskUpdate,
            "A write with a lost reply should hold the intended notes until checked.");
        state = OperatorPublishingState.Load(library.Root);
        entry = state.Units[unit.Id];
        await sync.RunAsync(library.Data, library.Root, state, default);
        Require(entry.NotesCheckpoint?.Transfer is null && entry.NotesError is null, "Rechecking a write should clear its pending state.");

        library.Data.SaveSystemNotes(unit, deskUpdate + "\nDesktop added 🛠");
        handler.EditNotes(doc, deskUpdate + "\nPhone added ✅");
        await sync.RunAsync(library.Data, library.Root, state, default);
        string bothAdded = deskUpdate + "\nDesktop added 🛠\nPhone added ✅";
        Require(handler.Items[doc].Notes == bothAdded && library.Data.LoadSystemNotes(unit) == bothAdded,
            "Independent note additions should keep both users' lines.");

        library.Data.SaveSystemNotes(unit, "Rewritten on desktop");
        handler.EditNotes(doc, "Rewritten on phone");
        await sync.RunAsync(library.Data, library.Root, state, default);
        Require(entry.NotesError?.Contains("edited in both places") == true &&
            handler.Items[doc].Notes == "Rewritten on phone" && library.Data.LoadSystemNotes(unit) == "Rewritten on desktop",
            "Overlapping edits should leave both originals intact.");
        Require(!library.Data.TryUpdateSystemNotes(unit, bothAdded, "Stale desktop draft"),
            "A stale desktop editor should not replace newer notes.");
    }

    private static async Task SharedDrawingPublicationAsync()
    {
        using var library = new Fixture();
        using var drive = new FakeDrive();
        using var http = new HttpClient(drive);
        var publisher = new OperatorDrivePublisher(new(http, _ => Task.FromResult("fixture")), (_, _) => Task.FromResult<UnitGuidePhoto?>(null));
        var state = new OperatorPublishingState { PublishAll = true };
        var units = library.Units;
        var ids = units.Select(u => u.Id).ToArray();
        var shared = library.AddPair(units[0], "Common print", 0, unitIds: [ids[0]]);
        var first = await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        Require(first.Published == 2, "Both QR folders should be created, including the unit without a drawing.");
        var left = state.Units[ids[0]];
        var right = state.Units[ids[1]];
        string leftFolder = left.Folder!.Id, rightFolder = right.Folder!.Id;
        string leftPdf = left.Electrical!.Id;
        string leftGuide = left.Guide!.Id, rightGuide = right.Guide!.Id;
        Require(right.Electrical is null, "A revision should link a new unit only when selected.");
        shared = library.AddPair(units[0], "Common print now shared", 270, shared.PdfPath,
            unitIds: ids, expectedReplacementUnits: [ids[0]]);
        var expanded = await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        string rightPdf = right.Electrical!.Id;
        byte[] sharedPdf = File.ReadAllBytes(shared.PdfPath);
        Require(shared.UnitIds.ToHashSet().SetEquals(ids) && expanded.Published == 2 && left.Electrical.Id == leftPdf &&
            drive.Items[leftPdf].Bytes!.SequenceEqual(sharedPdf) && drive.Items[rightPdf].Bytes!.SequenceEqual(sharedPdf),
            "Adding a unit during replacement should publish the revised pair to both units and retain the original PDF link.");
        var extra = library.AddPair(units[0], "Extra print", 90, unitIds: [ids[0]]);
        var extraResult = await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        Require(extraResult.Published == 1 && extraResult.Current == 1, "The extra print should update only its unit.");
        using (var leftDoc = PdfDocument.Open(drive.Items[leftGuide].Bytes!))
        using (var rightDoc = PdfDocument.Open(drive.Items[rightGuide].Bytes!))
            Require(leftDoc.NumberOfPages == 3 && rightDoc.NumberOfPages == 2, "A unit-specific print appeared in a different unit's guide.");

        var revision = library.AddPair(units[1], "Common print revised", 180, shared.PdfPath);
        var batch = await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        byte[] revisedPdf = File.ReadAllBytes(revision.PdfPath);
        Require(batch.Published == 2 && drive.Items[leftPdf].Bytes!.SequenceEqual(revisedPdf) &&
            drive.Items[rightPdf].Bytes!.SequenceEqual(revisedPdf) && left.Guide.Id == leftGuide && right.Guide.Id == rightGuide,
            "A single shared replacement should update both Drive PDFs and guides in place.");

        var record = library.Data.LoadResources().Single(r => r.StorageCategory == "Electrical Drawings");
        var affected = library.Data.SetAssetFileUnits(record, library.Gate, Path.GetFileName(revision.PdfPath), [ids[1]]);
        var moved = await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        Require(affected.ToHashSet().SetEquals(ids) && moved.Issues == 0 && drive.Items[leftPdf].Bytes!.SequenceEqual(File.ReadAllBytes(extra.PdfPath)) &&
            drive.Items[rightPdf].Bytes!.SequenceEqual(revisedPdf), "Unlinking the main shared drawing should promote the other assigned print for the affected unit.");

        library.Data.SetAssetFileUnits(record, library.Gate, Path.GetFileName(extra.PdfPath), [ids[1]]);
        var unlinked = await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        using var emptyPdf = PdfDocument.Open(drive.Items[leftPdf].Bytes!);
        using var emptyGuide = PdfDocument.Open(drive.Items[leftGuide].Bytes!);
        Require(unlinked.Issues == 0 && emptyPdf.GetPage(1).Text.Contains("No electrical drawing is assigned") &&
            emptyGuide.NumberOfPages == 1 && left.Folder.Id == leftFolder && right.Folder.Id == rightFolder,
            "Removing the last drawing should clear the obsolete PDF and retain the unit's QR link.");
    }

    private static async Task SharedGuidePdfAsync()
    {
        using var library = new Fixture();
        using var drive = new FakeDrive();
        using var http = new HttpClient(drive);
        var publisher = new OperatorDrivePublisher(new(http, _ => Task.FromResult("fixture")), (_, _) => Task.FromResult<UnitGuidePhoto?>(null));
        var state = new OperatorPublishingState { PublishAll = true };
        var first = library.Units[0];
        var second = library.Units[1];
        var third = new SafetySystem { Id = "unit4", GateId = library.Gate.Id, UnitNumber = "4" };
        library.Data.SaveSystems([first, second, third]);
        library.AddPair(first, "Primary wiring", 0);
        await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        var firstGuide = state.Units[first.Id].Guide!.Id;
        var secondGuide = state.Units[second.Id].Guide!.Id;
        var firstPdf = state.Units[first.Id].Electrical!.Id;
        string pdf = Path.Combine(library.DirectoryPath, "Shared supplemental print.pdf");
        File.WriteAllBytes(pdf, DrawingPdfFixture.TextPage(90));
        OperatorPublishingState.SaveGuidePdf(library.Data, library.Root, library.Gate.Id, [first.Id, second.Id], pdf, null);

        var left = OperatorPublishingState.GuidePdfsFor(library.Root, library.Gate.Id, first.Id).Single();
        var right = OperatorPublishingState.GuidePdfsFor(library.Root, library.Gate.Id, second.Id).Single();
        Require(left.Path == right.Path && File.ReadAllBytes(left.Path).SequenceEqual(File.ReadAllBytes(pdf)) &&
            left.UnitIds.SequenceEqual([first.Id, second.Id]) &&
            !OperatorPublishingState.GuidePdfsFor(library.Root, library.Gate.Id, third.Id).Any(),
            "The desktop listing should show one shared original for the selected units only.");
        var initial = await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        using (var leftDocument = PdfDocument.Open(drive.Items[firstGuide].Bytes!))
        using (var rightDocument = PdfDocument.Open(drive.Items[secondGuide].Bytes!))
            Require(initial.Published == 2 && initial.Current == 1 &&
                leftDocument.GetPage(1).Text.Contains("Primary wiring") &&
                leftDocument.GetPage(1).Text.Contains("Shared supplemental print") &&
                rightDocument.GetPage(1).Text.Contains("Shared supplemental print") &&
                state.Units[second.Id].Electrical is null,
                "The guide index should list the actual names in both units with no extra standalone electrical file.");

        var previous = OperatorPublishingState.LoadGuidePdfs(library.Root, library.Gate.Id).Single();
        File.WriteAllBytes(pdf, DrawingPdfFixture.TextPage(180));
        OperatorPublishingState.SaveGuidePdf(library.Data, library.Root, library.Gate.Id, previous.UnitIds, pdf, previous);
        var revised = await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        Require(revised.Published == 2 && revised.Current == 1 && File.Exists(left.Path) &&
            state.Units[first.Id].Guide!.Id == firstGuide && state.Units[second.Id].Guide!.Id == secondGuide &&
            state.Units[first.Id].Electrical!.Id == firstPdf, "Revising the shared PDF should preserve earlier bytes and the Drive IDs.");
    }

    private static async Task RemoveGuidePdfAsync()
    {
        using var library = new Fixture();
        using var drive = new FakeDrive();
        using var http = new HttpClient(drive);
        var publisher = new OperatorDrivePublisher(new(http, _ => Task.FromResult("fixture")), (_, _) => Task.FromResult<UnitGuidePhoto?>(null));
        var state = new OperatorPublishingState { PublishAll = true };
        var first = library.Units[0];
        var second = library.Units[1];
        library.AddPair(first, "Primary wiring", 0);
        string pdf = Path.Combine(library.DirectoryPath, "Shared supplemental print.pdf");
        File.WriteAllBytes(pdf, DrawingPdfFixture.TextPage(90));
        OperatorPublishingState.SaveGuidePdf(library.Data, library.Root, library.Gate.Id, [first.Id, second.Id], pdf, null);
        // An attachment saved by the earlier one-unit pilot lives in a per-unit folder.
        string legacy = Path.Combine(OperatorPublishingState.AttachmentsPath(library.Root, first.Id), "Pilot leftover.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllBytes(legacy, DrawingPdfFixture.TextPage(0));
        await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        var firstGuide = state.Units[first.Id].Guide!.Id;
        Require(OperatorPublishingState.GuidePdfsFor(library.Root, library.Gate.Id, first.Id).Count() == 2, "Fixture should list the shared and pilot PDFs.");

        var shared = OperatorPublishingState.GuidePdfsFor(library.Root, library.Gate.Id, first.Id).Single(p => p.UnitIds.Count == 2);
        var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        OperatorPublishingState.RemoveGuidePdf(library.Data, library.Root, library.Gate.Id, first.Id, shared.Path, now);
        Require(OperatorPublishingState.GuidePdfsFor(library.Root, library.Gate.Id, first.Id).Select(p => Path.GetFileName(p.Path)).SequenceEqual(["Pilot leftover.pdf"]) &&
            OperatorPublishingState.GuidePdfsFor(library.Root, library.Gate.Id, second.Id).Single().UnitIds.SequenceEqual([second.Id]) &&
            File.Exists(shared.Path), "Removing from one unit should leave the shared file with the other unit.");

        OperatorPublishingState.RemoveGuidePdf(library.Data, library.Root, library.Gate.Id, first.Id, legacy, now);
        OperatorPublishingState.RemoveGuidePdf(library.Data, library.Root, library.Gate.Id, second.Id, shared.Path, now);
        string removed = OperatorPublishingState.RemovedGuideFolder(library.Root);
        Require(!OperatorPublishingState.GuidePdfsFor(library.Root, library.Gate.Id, first.Id).Any() &&
            !OperatorPublishingState.GuidePdfsFor(library.Root, library.Gate.Id, second.Id).Any() &&
            !OperatorPublishingState.LoadGuidePdfs(library.Root, library.Gate.Id).Any() &&
            !File.Exists(shared.Path) && !File.Exists(legacy) &&
            Directory.EnumerateFiles(removed, "*.pdf", SearchOption.AllDirectories).Select(Path.GetFileName).Order()
                .SequenceEqual(["Pilot leftover.pdf", "Shared supplemental print.pdf"]),
            "The last copies should move to Removed Guide PDFs rather than being deleted.");

        await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        using var document = PdfDocument.Open(drive.Items[firstGuide].Bytes!);
        Require(state.Units[first.Id].Guide!.Id == firstGuide && document.GetPage(1).Text.Contains("Primary wiring") &&
            !document.GetPage(1).Text.Contains("Shared supplemental print") && !document.GetPage(1).Text.Contains("Pilot leftover"),
            "The Drive guide should be rebuilt in place without the removed PDFs.");
    }

    private static async Task SparePartsAsync()
    {
        using var library = new Fixture();
        using var drive = new FakeDrive();
        using var http = new HttpClient(drive);
        var publisher = new OperatorDrivePublisher(new(http, _ => Task.FromResult("fixture")), (_, _) => Task.FromResult<UnitGuidePhoto?>(null));
        const string motor = "001224", sensor = "001225", latch = "LP-65";
        library.Data.SaveParts([
            new() { Id = "motor", PartNumber = motor, Description = "Gate motor" },
            new() { Id = "sensor", PartNumber = sensor, Description = "Safety sensor", ReportedQuantity = 88 },
            new() { Id = "latch", PartNumber = latch, Description = "Door latch", ReportedQuantity = 77 }
        ]);
        var first = library.Units[0];
        var second = library.Units[1];
        first.ReplacementPartIds = ["motor", "sensor"];
        second.ReplacementPartIds = ["latch"];
        library.Data.SaveSystems([first, second]);
        var state = new OperatorPublishingState { PublishAll = true, StockroomPlantId = "02" };
        await publisher.RunAsync(library.Data, library.Root, state, false, null, default);
        string firstFolder = state.Units[first.Id].Folder!.Id, firstGuide = state.Units[first.Id].Guide!.Id;
        var initial = new DateTimeOffset(new DateTime(2026, 11, 22, 13, 0, 0, DateTimeKind.Local));
        var nextDay = initial.AddHours(12);
        int scans = 0;
        Task<StockroomService.Snapshot> Read(string url, IReadOnlyCollection<string> ids, CancellationToken token)
        {
            Require(url == StockroomPlant.Find("02")!.Url && ids.ToHashSet().SetEquals([motor, sensor, latch]),
                "The published units should only scan their Item IDs at the selected stockroom.");
            scans++;
            if (scans == 3) throw new HttpRequestException("Plant network unavailable.");
            return Task.FromResult(scans switch
            {
                1 => new StockroomService.Snapshot([new("1224", 7, "", "BIN-A", "Available"), new(sensor, 0, "", "BIN-B", "Available")], initial),
                _ => new([new(motor, 4, "", "BIN-A", "Available"),
                    new(sensor, 90, "", "BIN-C", "Available"), new(sensor, 95, "", "BIN-D", "Available")], nextDay)
            });
        }
        await publisher.PublishSparePartsAsync(library.Data, library.Root, state, Read, initial, default);
        string firstParts = state.Units[first.Id].SpareParts!.Id, secondParts = state.Units[second.Id].SpareParts!.Id;
        string PdfText(string id)
        {
            using var pdf = PdfDocument.Open(drive.Items[id].Bytes!);
            return pdf.GetPage(1).Text;
        }
        string left = PdfText(firstParts), right = PdfText(secondParts);
        Require(left.Contains("Last successful stockroom scan") && left.Contains(OperatorSpareParts.ScanTime(initial)) &&
            left.Contains("Huntingdon stockroom") && left.Contains(motor) && left.Contains("BIN-A") && left.Contains("7 in stock") &&
            left.Contains("0 in stock") && !left.Contains(latch), "The PDF should contain the assigned IDs, confirmed bins, zero stock and scan time.");
        Require(right.Contains(latch) && right.Contains("Quantity unknown") && right.Contains("No confirmed count yet") &&
            !right.Contains(motor) && !right.Contains("77 in stock"), "An unconfirmed catalog snapshot was shown as stock or leaked to the other unit.");

        // Reload from Q to exercise the same persisted state used by the scheduled process.
        state = OperatorPublishingState.Load(library.Root);
        await publisher.PublishSparePartsAsync(library.Data, library.Root, state, Read, initial.AddHours(6), default);
        Require(scans == 1, "Routine publishing should reuse the same day's stockroom scan.");
        await publisher.PublishSparePartsAsync(library.Data, library.Root, state, Read, nextDay, default);
        left = PdfText(firstParts);
        Require(left.Contains("4 in stock") && left.Contains("0 in stock") && left.Contains(OperatorSpareParts.ScanTime(nextDay)) &&
            left.Contains(OperatorSpareParts.ScanTime(initial)) && !left.Contains("90 in stock") && state.Units[first.Id].SpareParts!.Id == firstParts &&
            state.Units[first.Id].Folder!.Id == firstFolder && state.Units[first.Id].Guide!.Id == firstGuide,
            "A partial scan should update the matched count, date older counts individually and preserve the Drive IDs.");

        int requests = drive.Requests;
        await publisher.PublishSparePartsAsync(library.Data, library.Root, state, Read, nextDay.AddHours(1), default, forceScan: true);
        Require(scans == 3 && drive.Requests == requests && PdfText(firstParts) == left &&
            state.StockroomScans["02"].Message?.Contains("Plant network unavailable") == true,
            "A same-day manual retry should run and keep the previous PDF and its dates if the scan fails.");
    }

    private static async Task ResumablePdfAsync()
    {
        using var drive = new FakeDrive();
        using var http = new HttpClient(drive);
        var client = new OperatorDriveClient(http, _ => Task.FromResult("fixture"));
        var payload = new byte[6 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        var created = await client.CreatePdfAsync("large-pdf", "root", "Large drawing.pdf", payload, default);
        Require(created.Id == "large-pdf" && drive.Items[created.Id].Bytes!.SequenceEqual(payload),
            "A large PDF should send the complete payload through a Drive upload session.");
        payload[^1] ^= 1;
        var updated = await client.UpdatePdfAsync(created.Id, payload, created.ETag, default);
        Require(updated.Id == created.Id && drive.Items[created.Id].Bytes!.SequenceEqual(payload),
            "Resumable updates must upload to the original ID.");
    }

    private static void CheckQrLabels()
    {
        var labels = Enumerable.Range(1, 11).Select(i => new UnitQrLabel("Asset 1066", $"Unit {i}", "Mérida",
            $"https://drive.google.com/drive/folders/stable{i}")).ToArray();
        byte[] bytes = OperatorPublishingPdf.QrLabels(labels);
        using var pdf = PdfDocument.Open(bytes);
        Require(pdf.NumberOfPages == 2 && pdf.GetPage(1).Width == 612 && pdf.GetPage(1).Height == 792,
            "All labels should fit across two US Letter print sheets.");
        Require(pdf.GetPage(1).GetAnnotations().Count() == 8 && pdf.GetPage(2).GetAnnotations().Count() == 3 &&
            pdf.GetPage(2).Text.Contains("Unit 11") && pdf.GetPage(2).Text.Contains("Mérida"), "Each label needs a clickable URL and explicit unit name.");
        var destinations = new List<string>();
        foreach (var page in pdf.GetPages())
        {
            foreach (var annotation in page.GetAnnotations())
            {
                string url = ((UriAction)annotation.Action!).Uri;
                destinations.Add(url);
                Require(DecodePrintedQr(page, annotation.Rectangle) == url, "The vector QR must scan to its unit folder.");
            }
        }
        Require(destinations.SequenceEqual(labels.Select(label => label.Url)), "The sheets should contain each unit URL once, in order.");
    }

    private static string? DecodePrintedQr(Page page, PdfRectangle bounds)
    {
        const double scale = 3;
        int size = (int)Math.Ceiling(bounds.Width * scale);
        byte[] raster = Enumerable.Repeat((byte)255, size * size).ToArray();
        foreach (var path in page.Paths)
        {
            if (!path.IsFilled || path.FillColor is null || path.FillColor.ToRGBValues() != (0d, 0d, 0d) ||
                path.GetBoundingRectangle() is not { } black || black.Left < bounds.Left || black.Right > bounds.Right ||
                black.Bottom < bounds.Bottom || black.Top > bounds.Top) continue;
            int left = (int)Math.Round((black.Left - bounds.Left) * scale);
            int right = (int)Math.Round((black.Right - bounds.Left) * scale);
            int top = (int)Math.Round((bounds.Top - black.Top) * scale);
            int bottom = (int)Math.Round((bounds.Top - black.Bottom) * scale);
            for (int y = top; y < bottom; y++) Array.Fill(raster, (byte)0, y * size + left, right - left);
        }
        var decoder = new BarcodeReaderGeneric { Options = new() { PossibleFormats = [BarcodeFormat.QR_CODE], PureBarcode = true } };
        return decoder.Decode(new RGBLuminanceSource(raster, size, size, RGBLuminanceSource.BitmapFormat.Gray8))?.Text;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Fixture : IDisposable
    {
        internal string DirectoryPath { get; } = Directory.CreateTempSubdirectory("operator-publishing-").FullName;
        internal string Root => Data.SharedRoot;
        internal DataService Data { get; }
        internal SafetyGate Gate { get; }
        internal SafetySystem[] Units { get; }

        internal Fixture()
        {
            Data = new(false, Path.Combine(DirectoryPath, "settings"));
            Data.CreateBlankPlantLibrary(Path.Combine(DirectoryPath, "share"), "Assembly");
            Gate = new() { Id = "gate1", AssetNumber = "1066", AssetFolder = "Assets/Asset 1066", Category = "Assembly", Location = "West bay", Manufacturer = "Gränges" };
            Units = [new() { Id = "unit2", GateId = Gate.Id, UnitNumber = "2" }, new() { Id = "unit3", GateId = Gate.Id, UnitNumber = "3" }];
            Data.SaveGates([Gate]);
            Data.SaveSystems([.. Units]);
        }

        internal DataService.PairUploadResult AddPair(SafetySystem unit, string filename, int rotation, string? previous = null,
            IReadOnlyCollection<string>? unitIds = null, IReadOnlyCollection<string>? expectedReplacementUnits = null)
        {
            string pdf = Path.Combine(DirectoryPath, filename + ".pdf"), dwg = Path.Combine(DirectoryPath, filename + ".dwg");
            File.WriteAllBytes(pdf, DrawingPdfFixture.TextPage(rotation));
            File.WriteAllText(dwg, filename);
            var drawing = Data.LoadResources().FirstOrDefault(r => r.GateId == Gate.Id && r.StorageCategory == "Electrical Drawings")
                ?? AssetFileLayout.Create(Gate, "Electrical Drawings");
            return Data.SaveDrawingPair(drawing.ForUnit(unit.Id), Gate, unit, [pdf, dwg], previous, unitIds, expectedReplacementUnits);
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    private sealed class FakeDrive : HttpMessageHandler
    {
        internal const string Folder = "application/vnd.google-apps.folder";
        internal const string Doc = "application/vnd.google-apps.document";
        internal sealed record Item(string Id, string Name, string MimeType, string? Parent, string? DriveId)
        {
            internal byte[]? Bytes { get; set; }
            internal string Notes { get; set; } = "";
            internal int Revision { get; set; } = 1;
            internal object Fields => new
            {
                id = Id, mimeType = MimeType, driveId = DriveId, parents = Parent is null ? null : new[] { Parent },
                md5Checksum = Bytes is null ? null : Convert.ToHexStringLower(MD5.HashData(Bytes)),
                version = Revision.ToString()
            };
        }
        internal Dictionary<string, Item> Items { get; } = new();
        internal bool LoseNextFolderReply { get; set; }
        internal bool LoseNextPdfReply { get; set; }
        internal bool LoseNextNotesReply { get; set; }
        internal bool NotesApiEnabled { get; set; } = true;

        internal void EditNotes(string id, string text)
        {
            var item = Items[id];
            item.Notes = text;
            item.Revision++;
        }
        internal int Requests { get; private set; }
        private int _nextId;
        private Item? _upload;

        internal FakeDrive() => Create("root", "My Drive", Folder, null);

        internal Item FolderWithName(string name) => Items.Values.Single(item => item.MimeType == Folder && item.Name == name);

        internal Item Create(string id, string name, string mime, string? parent, byte[]? bytes = null, string? driveId = null)
        {
            var item = new Item(id, name, mime, parent, driveId) { Bytes = bytes };
            Items.Add(id, item);
            return item;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            Require(request.Headers.Authorization?.Parameter == "fixture", "Drive calls should carry publisher authentication.");
            string endpoint = request.RequestUri!.Segments[^1], query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (request.RequestUri.Host == "docs.googleapis.com") return await NotesRequestAsync(request, endpoint, token);
            if (request.Method == HttpMethod.Get && endpoint == "generateIds")
                return Json(new { ids = new[] { "generated-" + ++_nextId } });
            if (request.Method == HttpMethod.Get && endpoint == "files")
            {
                var parents = Items.Values.Where(item => query.Contains($"'{item.Id}' in parents")).ToArray();
                string? driveId = parents.Select(parent => parent.DriveId).Distinct().Single();
                Require(driveId is null ? query.Contains("corpora=user") && !query.Contains("driveId=")
                    : query.Contains("corpora=drive") && query.Contains("driveId=" + driveId), "Use the correct Drive search corpus.");
                var results = Items.Values.Where(item => parents.Any(parent => item.Parent == parent.Id) &&
                    (query.Contains($"name = '{item.Name}'") || query.Contains($"mimeType = '{item.MimeType}'")));
                return Json(new { files = results.Select(item => item.Fields) });
            }
            if (request.Method == HttpMethod.Get) return Items.TryGetValue(endpoint, out var remote)
                ? Json(remote.Fields) : new(HttpStatusCode.NotFound);

            if (request.Method == HttpMethod.Put && query == "?upload_id=fixture")
            {
                Require(_upload is not null && request.Content!.Headers.ContentType?.MediaType == "application/pdf",
                    "A resumable upload should send PDF bytes to the returned session URL.");
                var upload = _upload!;
                upload.Bytes = await request.Content!.ReadAsByteArrayAsync(token);
                _upload = null;
                return Json(upload.Fields);
            }
            bool resumable = query.Contains("uploadType=resumable");
            if (resumable)
                Require(request.Headers.Contains("X-Upload-Content-Length") && request.Headers.Contains("X-Upload-Content-Type"),
                    "Drive upload session requests should include the PDF size and MIME type.");
            if (request.Method == HttpMethod.Patch)
            {
                Require(request.Headers.IfMatch.Any(), "Use the available Drive revision validator when updating a PDF.");
                if (resumable) return StartUpload(Items[endpoint]);
                var updated = Items[endpoint];
                updated.Bytes = await request.Content!.ReadAsByteArrayAsync(token);
                if (LoseNextPdfReply)
                {
                    LoseNextPdfReply = false;
                    return new(HttpStatusCode.ServiceUnavailable);
                }
                return Json(updated.Fields);
            }

            var content = request.Content!;
            var metadata = content;
            byte[]? bytes = null;
            if (content is MultipartContent parts)
            {
                metadata = parts.First();
                bytes = await parts.Last().ReadAsByteArrayAsync(token);
            }
            using var document = JsonDocument.Parse(await metadata.ReadAsStringAsync(token));
            var entry = document.RootElement;
            string name = entry.GetProperty("name").GetString()!;
            string mime = entry.GetProperty("mimeType").GetString()!;
            var target = Items[entry.GetProperty("parents")[0].GetString()!];
            string id = entry.TryGetProperty("id", out var value) ? value.GetString()! : "generated-" + ++_nextId;
            var created = Create(id, name, mime, target.Id, bytes, target.DriveId);
            if (resumable) return StartUpload(created);
            if (mime == Folder && LoseNextFolderReply)
            {
                LoseNextFolderReply = false;
                return new(HttpStatusCode.ServiceUnavailable);
            }
            return Json(created.Fields);
        }

        private async Task<HttpResponseMessage> NotesRequestAsync(HttpRequestMessage request, string endpoint, CancellationToken token)
        {
            if (!NotesApiEnabled) return new(HttpStatusCode.Forbidden)
            {
                Content = JsonContent.Create(new { error = new { reason = "SERVICE_DISABLED" } })
            };
            string id = endpoint.Split(':')[0];
            var item = Items[id];
            Require(item.MimeType == Doc, "The Docs API should only edit the linked notes document.");
            if (request.Method == HttpMethod.Get)
                return Json(new
                {
                    documentId = id, revisionId = item.Revision.ToString(),
                    body = new
                    {
                        content = new[]
                        {
                            new { paragraph = new { elements = new[] { new { textRun = new { content = item.Notes + "\n" } } } } }
                        }
                    }
                });

            using var content = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
            var body = content.RootElement;
            Require(body.GetProperty("writeControl").GetProperty("requiredRevisionId").GetString() == item.Revision.ToString(),
                "Assume the revision is current only if Google checks it.");
            string edited = item.Notes;
            foreach (var edit in body.GetProperty("requests").EnumerateArray())
            {
                if (edit.TryGetProperty("deleteContentRange", out var deletion))
                {
                    var range = deletion.GetProperty("range");
                    int start = range.GetProperty("startIndex").GetInt32(), end = range.GetProperty("endIndex").GetInt32();
                    edited = edited.Remove(start - 1, end - start);
                }
                else
                {
                    var insertion = edit.GetProperty("insertText");
                    edited = edited.Insert(insertion.GetProperty("location").GetProperty("index").GetInt32() - 1,
                        insertion.GetProperty("text").GetString()!);
                }
            }
            EditNotes(id, edited);
            if (LoseNextNotesReply)
            {
                LoseNextNotesReply = false;
                return new(HttpStatusCode.ServiceUnavailable);
            }
            return Json(new { documentId = id });
        }

        private HttpResponseMessage StartUpload(Item item)
        {
            _upload = item;
            return new(HttpStatusCode.OK)
            {
                Headers = { Location = new("https://www.googleapis.com/upload/drive/v3/files?upload_id=fixture") }
            };
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(body),
            Headers = { ETag = new EntityTagHeaderValue("\"fixture-revision\"") }
        };
    }
}
