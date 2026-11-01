using System.Net;
using System.Text;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;
namespace SafetyGateSupport.Validation;

internal static class PartsPageChecks
{
    public static IEnumerable<(string Name, Action Check)> Cases =>
    [
        ("Legacy part notes expose location and whole-number snapshot without re-entry", LegacyParts),
        ("Part photos copy into the shared library, preserve edits and archive replacements", PhotoStorage),
        ("Stockroom tables require exact stock identity, clear quantity headers and a unique site/bin match", StockroomTables),
        ("Stockroom reader submits the Safe2Cross search form with its hidden fields", SearchForms),
        ("Stockroom HTTP fetch stays bounded, rejects failures and honors cancellation", StockroomFetch)
    ];
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
            if (Requests == 1) return new(HttpStatusCode.OK) { Content = new StringContent("<form action='/inventory/search?site=HRM' method='" + (post ? "post" : "get") + "'><input type='hidden' name='token' value='abc&amp;123'><input name='keyword' type='search'><input type='submit' name='find' value='Search'></form>") };
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
