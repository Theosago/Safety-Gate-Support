using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SafetyGateSupport.Services;

internal sealed class MaintainXWorkOrderClient(HttpClient http)
{
    private static readonly Uri ApiRoot = new("https://api.getmaintainx.com/v1/");
    private static readonly string[] CurrentStatuses = ["OPEN", "IN_PROGRESS", "ON_HOLD"];

    internal async Task<MaintainXAsset> GetAssetAsync(long assetId, string key, CancellationToken token)
    {
        using var response = await ReadAsync($"assets/{assetId.ToString(CultureInfo.InvariantCulture)}", key, token);
        var root = response.RootElement;
        if (root.TryGetProperty("asset", out var asset)) root = asset;
        var result = root.Deserialize<MaintainXAsset>();
        if (result is null || result.Id != assetId || string.IsNullOrWhiteSpace(result.Name))
            throw new JsonException("MaintainX returned an invalid asset.");
        return result;
    }

    internal async Task<IReadOnlyList<MaintainXWorkOrder>> ListCurrentAsync(long assetId, string key, CancellationToken token)
    {
        string query = $"workorders?limit=100&assets={assetId.ToString(CultureInfo.InvariantCulture)}"
            + string.Concat(CurrentStatuses.Select(status => $"&statuses={status}"));
        var orders = new Dictionary<long, MaintainXWorkOrder>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (int page = 0; page < 30; page++)
        {
            string url = cursor is null ? query : $"{query}&cursor={Uri.EscapeDataString(cursor)}";
            using var response = await ReadAsync(url, key, token);
            var batch = response.RootElement.Deserialize<WorkOrderPage>();
            if (batch?.WorkOrders is null) throw new JsonException("MaintainX returned an invalid work-order list.");

            // Check the asset and status on every record, even with server-side filters.
            foreach (var order in batch.WorkOrders)
            {
                if (order.Id is long id && id > 0 && order.AssetId == assetId && CurrentStatuses.Contains(order.Status) && order.CompletedAt is null)
                    orders[id] = order;
            }

            cursor = batch.NextCursor;
            if (string.IsNullOrEmpty(cursor))
                return orders.Values.OrderBy(order => order.DueDate ?? DateTimeOffset.MaxValue)
                    .ThenByDescending(order => order.Id).ToList();
            if (!cursors.Add(cursor)) break;
        }

        throw new InvalidDataException("MaintainX did not finish returning work orders. Try refreshing.");
    }

    private async Task<JsonDocument> ReadAsync(string path, string key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoot, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
        {
            string message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "MaintainX rejected the API key. Enter a current key in MaintainX settings.",
                HttpStatusCode.Forbidden => "This API key cannot read the selected asset or its work orders.",
                HttpStatusCode.NotFound => "MaintainX could not find this asset. Check the asset ID and the key’s organization.",
                HttpStatusCode.TooManyRequests => "MaintainX is limiting requests. Wait briefly, then refresh.",
                _ => $"MaintainX returned an error (HTTP {(int)response.StatusCode}). Try refreshing."
            };
            throw new MaintainXRequestException(message);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }

    private sealed record WorkOrderPage(
        [property: JsonPropertyName("workOrders")] List<MaintainXWorkOrder>? WorkOrders,
        [property: JsonPropertyName("nextCursor")] string? NextCursor);
}

internal sealed class MaintainXRequestException(string message) : Exception(message);

internal sealed record MaintainXAsset(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name);

internal sealed record MaintainXWorkOrder(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("assetId")] long? AssetId,
    [property: JsonPropertyName("sequentialId")] long? SequentialId,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("priority")] string? Priority,
    [property: JsonPropertyName("dueDate")] DateTimeOffset? DueDate,
    [property: JsonPropertyName("completedAt")] DateTimeOffset? CompletedAt)
{
    public string NumberText => $"#{SequentialId ?? Id}";
    public string TitleText => string.IsNullOrWhiteSpace(Title) ? "Untitled work order" : Title;
    public string StatusText => Status switch
    {
        "OPEN" => "Open",
        "IN_PROGRESS" => "In progress",
        "ON_HOLD" => "On hold",
        _ => "Unknown"
    };
    public string PriorityText => Priority switch
    {
        "NONE" or null or "" => "—",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Priority.ToLowerInvariant())
    };
    public string DueText => DueDate?.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture) ?? "—";
}
