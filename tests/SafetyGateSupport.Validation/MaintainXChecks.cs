using System.Net;
using System.Text;
using SafetyGateSupport.Services;

namespace SafetyGateSupport.Validation;

internal static class MaintainXChecks
{
    internal static IEnumerable<(string Name, Action Check)> Cases =>
    [
        ("MaintainX retrieves every current page and isolates the linked asset", CurrentOrders),
        ("MaintainX separates an empty result from an API error", EmptyAndRejected),
        ("MaintainX keeps asset links separate from the shared unit records", SharedLinks)
    ];

    private static void CurrentOrders()
    {
        using var replies = new Replies(
            ("/v1/assets/42", HttpStatusCode.OK, """{"asset":{"id":42,"name":"Gate 42"}}"""),
            ("/v1/workorders?limit=100&assets=42&statuses=OPEN&statuses=IN_PROGRESS&statuses=ON_HOLD", HttpStatusCode.OK, """
                {"workOrders":[
                  {"id":5,"assetId":42,"title":"Hold this","status":"ON_HOLD"},
                  {"id":6,"assetId":77,"title":"Wrong asset","status":"OPEN"},
                  {"id":7,"assetId":42,"title":"Closed order","status":"DONE"},
                  {"id":8,"assetId":null,"title":"No assignment","status":"OPEN"},
                  {"id":9,"assetId":42,"status":"OPEN","completedAt":"2026-11-22T09:30:00Z"},
                  {"id":null,"assetId":42,"status":"OPEN"}
                ],"nextCursor":"page+2","nextPageUrl":"https://untrusted.invalid/"}
                """),
            ("/v1/workorders?limit=100&assets=42&statuses=OPEN&statuses=IN_PROGRESS&statuses=ON_HOLD&cursor=page%2B2", HttpStatusCode.OK, """
                {"workOrders":[
                  {"id":5,"assetId":42,"title":"This resumed","status":"IN_PROGRESS"},
                  {"id":22,"assetId":42,"sequentialId":3,"title":"Inspect gate","status":"OPEN","priority":"HIGH","dueDate":"2026-11-24T18:00:00Z"}
                ],"nextCursor":null}
                """));
        using var http = new HttpClient(replies);
        var client = new MaintainXWorkOrderClient(http);
        Require(client.GetAssetAsync(42, "test-key", default).GetAwaiter().GetResult() == new MaintainXAsset(42, "Gate 42"),
            "Asset identity was not confirmed.");
        var orders = client.ListCurrentAsync(42, "test-key", default).GetAwaiter().GetResult();
        Require(orders.Select(order => order.Id).SequenceEqual(new long?[] { 22, 5 }),
            "Current orders were mixed with unrelated records or the next page was missed.");
        Require(orders[0].NumberText == "#3" && orders[0].TitleText == "Inspect gate" && orders[0].PriorityText == "High"
            && orders[1].StatusText == "In progress" && replies.Remaining == 0, "Work-order details or updated statuses were lost.");
    }

    private static void EmptyAndRejected()
    {
        const string path = "/v1/workorders?limit=100&assets=42&statuses=OPEN&statuses=IN_PROGRESS&statuses=ON_HOLD";
        using var http = new HttpClient(new Replies(
            (path, HttpStatusCode.OK, """{"workOrders":[]}"""),
            (path, HttpStatusCode.Forbidden, """{"message":"Internal detail"}""")));
        var client = new MaintainXWorkOrderClient(http);
        Require(client.ListCurrentAsync(42, "test-key", default).GetAwaiter().GetResult().Count == 0,
            "An empty work-order list was not recognized.");
        try
        {
            client.ListCurrentAsync(42, "test-key", default).GetAwaiter().GetResult();
            throw new InvalidOperationException("Permission failure appeared as no work orders.");
        }
        catch (MaintainXRequestException ex)
        {
            Require(ex.Message.Contains("cannot read") && !ex.Message.Contains("Internal detail"),
                "The permission error did not give actionable feedback.");
        }
    }

    private static void SharedLinks()
    {
        var folder = Directory.CreateTempSubdirectory("maintainx-links-");
        try
        {
            string local = Path.Combine(folder.FullName, "local");
            var data = new DataService(false, local);
            data.SetSharedRoot(Path.Combine(folder.FullName, "shared"));
            data.InitializeSharedData();
            var units = data.LoadSystems();
            string unitsBefore = File.ReadAllText(data.SystemsFile);
            Require(data.LoadMaintainXLink(units[0].Id) is null, "An existing unit unexpectedly had a link.");

            var link = new MaintainXAsset(42, "Gate 42");
            data.SaveMaintainXLink(units[0].Id, null, link);
            var otherInstance = new DataService(false, local);
            Require(otherInstance.LoadMaintainXLink(units[0].Id) == link && otherInstance.LoadMaintainXLink(units[1].Id) is null,
                "The new mapping was not shared or also changed another unit.");
            var replacement = new MaintainXAsset(43, "Gate 43");
            otherInstance.SaveMaintainXLink(units[0].Id, link, replacement);
            try
            {
                data.SaveMaintainXLink(units[0].Id, link, new MaintainXAsset(44, "Gate 44"));
                throw new Exception("A stale change was allowed.");
            }
            catch (InvalidOperationException) { }
            Require(data.LoadMaintainXLink(units[0].Id) == replacement && File.ReadAllText(data.SystemsFile) == unitsBefore,
                "Link changes overwrote concurrent edits or existing units.");
        }
        finally { folder.Delete(recursive: true); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Replies(params (string Path, HttpStatusCode Status, string Body)[] replies) : HttpMessageHandler
    {
        private readonly Queue<(string Path, HttpStatusCode Status, string Body)> _replies = new(replies);
        internal int Remaining => _replies.Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (path, status, body) = _replies.Dequeue();
            Require(request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == "https://api.getmaintainx.com" + path
                && request.Headers.Authorization?.ToString() == "Bearer test-key", "Unexpected request or credential destination.");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
