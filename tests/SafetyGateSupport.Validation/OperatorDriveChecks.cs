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
        ("Batch QR sheets contain every stable unit URL across multiple print pages", CheckQrLabels),
        ("Larger electrical PDFs open a resumable upload and preserve their file ID on update", () => ResumablePdfAsync().GetAwaiter().GetResult())
    ];

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
        var labels = Enumerable.Range(1, 11).Select(i => new UnitQrLabel("Asset 1066", $"Unit {i}", "West bay",
            $"https://drive.google.com/drive/folders/stable{i}")).ToArray();
        byte[] bytes = OperatorPublishingPdf.QrLabels(labels);
        using var pdf = PdfDocument.Open(bytes);
        Require(pdf.NumberOfPages == 2 && pdf.GetPage(1).Width == 612 && pdf.GetPage(1).Height == 792,
            "All labels should fit across two US Letter print sheets.");
        Require(pdf.GetPage(1).GetAnnotations().Count() == 8 && pdf.GetPage(2).GetAnnotations().Count() == 3 &&
            pdf.GetPage(2).Text.Contains("Unit 11"), "Each label needs a clickable URL and explicit unit name.");
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
            Gate = new() { Id = "gate1", AssetNumber = "1066", AssetFolder = "Assets/Asset 1066", Category = "Assembly", Location = "West bay" };
            Units = [new() { Id = "unit2", GateId = Gate.Id, UnitNumber = "2" }, new() { Id = "unit3", GateId = Gate.Id, UnitNumber = "3" }];
            Data.SaveGates([Gate]);
            Data.SaveSystems([.. Units]);
        }

        internal DataService.PairUploadResult AddPair(SafetySystem unit, string filename, int rotation, string? previous = null)
        {
            string pdf = Path.Combine(DirectoryPath, filename + ".pdf"), dwg = Path.Combine(DirectoryPath, filename + ".dwg");
            File.WriteAllBytes(pdf, DrawingPdfFixture.TextPage(rotation));
            File.WriteAllText(dwg, filename);
            var drawing = Data.LoadResources().FirstOrDefault(r => r.GateId == Gate.Id && r.StorageCategory == "Electrical Drawings")
                ?? AssetFileLayout.Create(Gate, "Electrical Drawings");
            return Data.SaveDrawingPair(drawing.ForUnit(unit.Id), Gate, unit, [pdf, dwg], previous);
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
            internal object Fields => new
            {
                id = Id, mimeType = MimeType, driveId = DriveId, parents = Parent is null ? null : new[] { Parent },
                md5Checksum = Bytes is null ? null : Convert.ToHexStringLower(MD5.HashData(Bytes))
            };
        }
        internal Dictionary<string, Item> Items { get; } = new();
        internal bool LoseNextFolderReply { get; set; }
        internal bool LoseNextPdfReply { get; set; }
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
            if (request.Method == HttpMethod.Get && endpoint == "generateIds")
                return Json(new { ids = new[] { "generated-" + ++_nextId } });
            if (request.Method == HttpMethod.Get && endpoint == "files")
            {
                var parent = Items.Values.Single(item => query.Contains($"'{item.Id}' in parents"));
                Require(parent.DriveId is null ? query.Contains("corpora=user") && !query.Contains("driveId=")
                    : query.Contains("corpora=drive") && query.Contains("driveId=" + parent.DriveId), "Use the correct Drive search corpus.");
                return Json(new { files = Items.Values.Where(item => item.Parent == parent.Id && query.Contains($"name = '{item.Name}'")).Select(item => item.Fields) });
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
