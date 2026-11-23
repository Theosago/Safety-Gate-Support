using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

internal sealed record ConfirmedStock(decimal Quantity, string Bin, DateTimeOffset CheckedUtc);
internal sealed record OperatorStockroomScan(DateTimeOffset AttemptedUtc, string[] ItemIds,
    Dictionary<string, ConfirmedStock> Counts, string? Message);
internal sealed record SparePartLine(string ItemId, string Description, string CatalogBin, ConfirmedStock? Stock);
internal sealed record UnitSpareParts(string? Plant, IReadOnlyList<SparePartLine> Parts)
{
    internal DateTimeOffset? LastConfirmedUtc => Parts.Max(part => part.Stock?.CheckedUtc);
    internal string Fingerprint(string label) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, label, Plant, Parts })));
}

internal static class OperatorSpareParts
{
    internal const string FileName = "03 Spare parts.pdf";
    internal static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(30);

    internal static async Task<OperatorStockroomScan> ScanAsync(StockroomPlant plant, IReadOnlyList<string> itemIds,
        OperatorStockroomScan? prior, Func<string, IReadOnlyCollection<string>, CancellationToken, Task<StockroomService.Snapshot>> reader,
        DateTimeOffset now, CancellationToken token)
    {
        var keys = itemIds.Select(StockroomService.StockKey).ToArray();
        // Allow for launch and network jitter between successive scheduled starts.
        if (prior is not null && now.AddSeconds(45) < prior.AttemptedUtc + ScanInterval && keys.All(prior.ItemIds.Contains))
            return prior;

        var known = prior is null ? new Dictionary<string, ConfirmedStock>() : new(prior.Counts);
        string? message;
        try
        {
            var snapshot = await reader(plant.Url, itemIds, token);
            int confirmed = 0;
            foreach (string id in itemIds)
            {
                var row = StockroomService.Find(snapshot, new PartRecord { PartNumber = id }, plantScoped: true);
                if (row is null) continue;
                string key = StockroomService.StockKey(id);
                if (!known.TryGetValue(key, out var previous) || snapshot.CheckedUtc >= previous.CheckedUtc)
                    known[key] = new(row.Quantity, row.Location, snapshot.CheckedUtc.ToUniversalTime());
                confirmed++;
            }
            message = confirmed == itemIds.Count ? null :
                $"The latest scan confirmed {confirmed} of {itemIds.Count} Item IDs. The PDF shows each item's last confirmed count.";
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not OutOfMemoryException)
        {
            message = "Stockroom scan could not finish. The PDF keeps the last confirmed counts. " + error.Message;
        }
        token.ThrowIfCancellationRequested();
        return new(now, keys, known, message);
    }

    internal static UnitSpareParts ForUnit(IReadOnlyList<PartRecord> parts, StockroomPlant? plant, OperatorStockroomScan? scan) =>
        new(plant?.Name, parts.Select(part =>
        {
            ConfirmedStock? stock = scan?.Counts.GetValueOrDefault(StockroomService.StockKey(part.PartNumber));
            string bin = part.SiteValue.Length == 0 || plant is not null &&
                (plant.Id.Equals(part.SiteValue, StringComparison.OrdinalIgnoreCase) ||
                 plant.Name.Equals(part.SiteValue, StringComparison.OrdinalIgnoreCase))
                ? part.LocationValue : "";
            string title = string.IsNullOrWhiteSpace(part.Description) ? part.StockroomDescription : part.Description;
            return new SparePartLine(part.PartNumber, title, bin, stock);
        }).ToArray());

    internal static string ScanTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("MMM d, yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
