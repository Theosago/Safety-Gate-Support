using System.IO;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using HtmlAgilityPack;
using SafetyGateSupport.Models;
namespace SafetyGateSupport.Services;

public static class StockroomService
{
    public sealed record Row(string Stock, decimal Quantity, string Site, string Location, string QuantityLabel);
    public sealed record Snapshot(IReadOnlyList<Row> Rows, DateTimeOffset CheckedUtc);
    public static string StockKey(string text) => text.Trim().TrimStart('0');
    private static string Text(HtmlNode node) => WebUtility.HtmlDecode(node.InnerText).Replace('\u00a0', ' ').Trim();
    private static string Header(string text) => new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    public static Snapshot Parse(string html)
    {
        if (Encoding.UTF8.GetByteCount(html) > 4 * 1024 * 1024) throw new InvalidDataException("The stockroom page is larger than the supported 4 MB response.");
        var document = new HtmlDocument(); document.LoadHtml(html); var rows = new List<Row>();
        foreach (var table in document.DocumentNode.Descendants("table"))
        {
            int stock = -1, quantity = -1, site = -1, location = -1; string label = "";
            foreach (var row in table.Descendants("tr").Where(r => r.Ancestors("table").FirstOrDefault() == table))
            {
                var cells = row.ChildNodes.Where(n => n.Name is "td" or "th").ToArray(); if (cells.Length == 0) continue;
                var names = cells.Select(c => Header(Text(c))).ToArray();
                int stockHeader = Array.FindIndex(names, n => new[] { "stocknumber", "stockno", "stock", "itemnumber", "itemno", "materialnumber", "material", "partnumber", "partno" }.Contains(n));
                int qtyHeader = Array.FindIndex(names, n => new[] { "quantityonhand", "qtyonhand", "onhand", "currentquantity" }.Contains(n));
                if (qtyHeader < 0) qtyHeader = Array.FindIndex(names, n => new[] { "quantity", "qty", "availablequantity", "qtyavailable", "quantityavailable" }.Contains(n));
                if (stockHeader >= 0 && qtyHeader >= 0)
                {
                    // Colspans/multiple header rows need site-specific mapping, not positional guessing.
                    if (cells.Any(c => c.GetAttributeValue("colspan", "1") != "1" || c.GetAttributeValue("rowspan", "1") != "1")) { stock = quantity = -1; continue; }
                    stock = stockHeader; quantity = qtyHeader; label = Text(cells[quantity]);
                    site = Array.FindIndex(names, n => new[] { "site", "plant", "warehouse" }.Contains(n));
                    location = Array.FindIndex(names, n => new[] { "location", "bin", "binlocation" }.Contains(n)); continue;
                }
                if (stock < 0 || quantity < 0 || cells.Length <= Math.Max(stock, quantity) || cells.Any(c => c.GetAttributeValue("colspan", "1") != "1" || c.GetAttributeValue("rowspan", "1") != "1")) continue;
                string number = Text(cells[stock]);
                if (number.Length is < 5 or > 12 || !number.All(char.IsAsciiDigit)) continue;
                string amount = Text(cells[quantity]);
                if (!Regex.IsMatch(amount, @"^(?:\d+|\d{1,3}(?:,\d{3})+)(?:\.\d+)?$", RegexOptions.None, TimeSpan.FromMilliseconds(100))) continue;
                if (!decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var count) || count < 0) continue;
                rows.Add(new(number, count, site >= 0 && site < cells.Length ? Text(cells[site]) : "", location >= 0 && location < cells.Length ? Text(cells[location]) : "", label));
            }
        }
        return new(rows, DateTimeOffset.UtcNow);
    }
    public static Row? Find(Snapshot snapshot, PartRecord part)
    {
        var matches = snapshot.Rows.Where(r => StockKey(r.Stock) == StockKey(part.PartNumber)).ToList();
        string site = part.SiteValue;
        if (site.Length > 0 && !site.Equals("Not supplied", StringComparison.OrdinalIgnoreCase) && matches.Any(r => r.Site.Length > 0)) matches = matches.Where(r => r.Site.Equals(site, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1 && part.LocationValue.Length > 0 && matches.Any(r => r.Location.Length > 0)) matches = matches.Where(r => r.Location.Equals(part.LocationValue, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
    public static async Task<Snapshot> FetchAsync(string url, CancellationToken token, HttpMessageHandler? transport = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Configure a valid stockroom HTTP/HTTPS address in Setup.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8)); token = timeout.Token;
        using var handler = transport ?? new HttpClientHandler { UseDefaultCredentials = true, AllowAutoRedirect = false };
        using var client = new HttpClient(handler, false) { Timeout = TimeSpan.FromSeconds(8) };
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidDataException("The stockroom page redirected. Configure the final inventory page address in Setup.");
        if (response.Content.Headers.ContentLength is > 4 * 1024 * 1024) throw new InvalidDataException("The stockroom response exceeds 4 MB.");
        await using var stream = await response.Content.ReadAsStreamAsync(token); using var buffer = new MemoryStream();
        var block = new byte[16384]; int read;
        while ((read = await stream.ReadAsync(block, token)) > 0) { if (buffer.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("The stockroom response exceeds 4 MB."); buffer.Write(block, 0, read); }
        token.ThrowIfCancellationRequested();
        return await Task.Run(() => Parse(Encoding.UTF8.GetString(buffer.ToArray())), token);
    }
}
