using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using SafetyGateSupport.Services;

namespace SafetyGateSupport.Validation;

internal static class OperatorDriveChecks
{
    internal static void Check() => CheckAsync().GetAwaiter().GetResult();

    private static async Task CheckAsync()
    {
        using var handler = new PilotDrive();
        using var http = new HttpClient(handler);
        var client = new OperatorDriveClient(http, _ => Task.FromResult("fixture-token"));
        string root = Directory.CreateTempSubdirectory("drive-pilot-").FullName;
        string local = Path.Combine(root, "operator.pdf");
        string state = Path.Combine(root, "pilot.json");
        var pilot = new OperatorDrivePilot(root, "gate", "unit", "Asset A", "Unit 1", "operator.pdf", "root", null);
        try
        {
            byte[] first = Pdf("approved");
            await File.WriteAllBytesAsync(local, first);
            try
            {
                await pilot.SyncAsync(client, state, default);
                throw new Exception("Expected the simulated lost acknowledgement.");
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.ServiceUnavailable) { }
            pilot = OperatorDrivePilot.Load(state)!;
            Require(pilot.PendingFileId == "pdf-id", "Persist the reserved ID before uploading.");
            await pilot.SyncAsync(client, state, default);
            pilot = OperatorDrivePilot.Load(state)!;
            Require(pilot.FileId == "pdf-id" && pilot.Link == PilotDrive.Link && handler.Creates == 1, "First publish should save the Drive ID and link.");

            byte[] second = Pdf("desktop revision");
            await File.WriteAllBytesAsync(local, second);
            await pilot.SyncAsync(client, state, default);
            pilot = OperatorDrivePilot.Load(state)!;
            Require(handler.Patches == 1 && handler.Creates == 1 && handler.Bytes!.SequenceEqual(second)
                && pilot.FileId == "pdf-id" && pilot.Link == PilotDrive.Link, "Local changes should patch the same Drive file.");
            Require(MobileSupportExporter.QrSvg(pilot.Link!).Contains("<svg"), "The stable link should produce a QR label.");

            byte[] third = Pdf("Drive revision");
            handler.Change(third);
            await pilot.SyncAsync(client, state, default);
            pilot = OperatorDrivePilot.Load(state)!;
            string backup = Directory.GetFiles(Path.Combine(root, ".operator-pdf-history")).Single();
            Require((await File.ReadAllBytesAsync(local)).SequenceEqual(third)
                && (await File.ReadAllBytesAsync(backup)).SequenceEqual(second), "Drive changes should update the local PDF and preserve its previous bytes.");

            byte[] conflicting = Pdf("local conflict");
            await File.WriteAllBytesAsync(local, conflicting);
            handler.Change(Pdf("remote conflict"));
            try
            {
                await pilot.SyncAsync(client, state, default);
                throw new Exception("Expected a conflict.");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("both copies")) { }
            Require(handler.Patches == 1 && (await File.ReadAllBytesAsync(local)).SequenceEqual(conflicting)
                && OperatorDrivePilot.Load(state) == pilot, "Conflicts should retain both sides and the prior sync state.");

            // Bind a legacy PDF using its ID even when its Drive name differs.
            await File.WriteAllBytesAsync(local, handler.Bytes!);
            var legacy = pilot with { LastHash = null, Link = null };
            await legacy.SyncAsync(client, state, default);
            Require(OperatorDrivePilot.Load(state)!.FileId == "pdf-id" && handler.Creates == 1,
                "An existing Drive ID should be adopted in place.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] Pdf(string text) => Encoding.ASCII.GetBytes("%PDF-1.7\n" + text);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class PilotDrive : HttpMessageHandler
    {
        internal const string Link = "https://drive.google.com/file/d/pdf-id/view";
        internal byte[]? Bytes { get; private set; }
        internal int Creates { get; private set; }
        internal int Patches { get; private set; }
        private int _version;
        private bool _assetCreated;
        private bool _unitCreated;

        internal void Change(byte[] bytes) { Bytes = bytes; _version++; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Require(request.Headers.Authorization?.Parameter == "fixture-token", "Authorization was missing.");
            var uri = request.RequestUri!;
            string query = Uri.UnescapeDataString(uri.Query);
            string last = uri.Segments[^1];
            if (last == "generateIds") return Json(new { ids = new[] { "pdf-id" } });
            if (request.Method == HttpMethod.Post && last == "files")
            {
                if (query.Contains("uploadType=multipart"))
                {
                    var parts = (MultipartContent)request.Content!;
                    string metadata = await parts.First().ReadAsStringAsync(ct);
                    Require(metadata.Contains("pdf-id") && metadata.Contains("unit-folder"), "New PDF should use its reserved ID and Unit folder.");
                    Change(await parts.Last().ReadAsByteArrayAsync(ct));
                    Creates++;
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }
                string folder = await request.Content!.ReadAsStringAsync(ct);
                if (folder.Contains("Asset A")) { _assetCreated = true; return Json(Folder("asset", "root")); }
                Require(folder.Contains("Unit 1") && folder.Contains("asset"), "The Unit folder should be nested under the Asset folder.");
                _unitCreated = true;
                return Json(Folder("unit-folder", "asset"));
            }
            if (request.Method == HttpMethod.Patch)
            {
                Require(last == "pdf-id" && request.Headers.IfMatch.Any(), "Upload should use the original file ID and validator.");
                Change(await request.Content!.ReadAsByteArrayAsync(ct));
                Patches++;
                return Json(PdfItem());
            }
            if (last == "files")
            {
                object[] files = query.Contains("name = 'Asset A'") && _assetCreated ? [Folder("asset", "root")]
                    : query.Contains("name = 'Unit 1'") && _unitCreated ? [Folder("unit-folder", "asset")] : [];
                return Json(new { files });
            }
            return last switch
            {
                "root" => Json(Folder("root")),
                "asset" => Json(Folder("asset", "root")),
                "unit-folder" => Json(Folder("unit-folder", "asset")),
                "pdf-id" when query.Contains("alt=media") => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes!) },
                "pdf-id" => Json(PdfItem()),
                _ => throw new InvalidOperationException("Unexpected fixture request.")
            };
        }

        private object PdfItem() => new
        {
            id = "pdf-id", parents = new[] { "unit-folder" }, driveId = "shared-drive",
            mimeType = "application/pdf", version = _version.ToString(), webViewLink = Link,
            md5Checksum = Convert.ToHexStringLower(MD5.HashData(Bytes!))
        };

        private static object Folder(string id, string? parent = null) =>
            new { id, driveId = "shared-drive", mimeType = OperatorDriveClient.FolderMime, parents = parent is null ? null : new[] { parent } };

        private HttpResponseMessage Json(object body)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
            response.Headers.ETag = new($"\"{_version}\"");
            return response;
        }
    }
}
