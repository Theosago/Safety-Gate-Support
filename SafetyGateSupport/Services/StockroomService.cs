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
    public static string StockKey(string? text) => (text ?? "").Trim().TrimStart('0');
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
                int stockHeader = Array.FindIndex(names, n => new[] { "stocknumber", "stockno", "stock", "itemnumber", "itemno", "materialnumber", "material", "partnumber", "partno", "part", "item", "itemid", "itemcode", "stockcode", "inventorynumber" }.Contains(n));
                int qtyHeader = Array.FindIndex(names, n => new[] { "quantityonhand", "qtyonhand", "onhand", "currentquantity", "onhandqty", "qoh" }.Contains(n));
                if (qtyHeader < 0) qtyHeader = Array.FindIndex(names, n => new[] { "quantity", "qty", "availablequantity", "qtyavailable", "quantityavailable", "available", "qtyinstock", "quantityinstock" }.Contains(n));
                if (stockHeader >= 0 && qtyHeader >= 0)
                {
                    // Colspans/multiple header rows need site-specific mapping, not positional guessing.
                    if (cells.Any(c => c.GetAttributeValue("colspan", "1") != "1" || c.GetAttributeValue("rowspan", "1") != "1")) { stock = quantity = -1; continue; }
                    stock = stockHeader; quantity = qtyHeader; label = Text(cells[quantity]);
                    site = Array.FindIndex(names, n => new[] { "site", "plant", "warehouse" }.Contains(n));
                    location = Array.FindIndex(names, n => new[] { "location", "bin", "binlocation", "storeroomlocation", "storagebin" }.Contains(n)); continue;
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
    private sealed record SearchForm(Uri Action, HttpMethod Method, List<KeyValuePair<string, string>> Values);
    private static SearchForm? FindSearchForm(string html, Uri page)
    {
        var document = new HtmlDocument(); document.LoadHtml(html);
        foreach (var form in document.DocumentNode.Descendants("form"))
        {
            var inputs = form.Descendants("input").ToList();
            if (inputs.Any(n => n.GetAttributeValue("type", "").Equals("password", StringComparison.OrdinalIgnoreCase))) continue;
            var candidates = inputs.Where(n => n.GetAttributeValue("type", "text").ToLowerInvariant() is "text" or "search").ToList();
            var field = candidates.FirstOrDefault(n => n.GetAttributeValue("type", "").Equals("search", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(string.Join(" ", new[] { n.GetAttributeValue("name", ""), n.GetAttributeValue("id", ""), n.GetAttributeValue("placeholder", "") }), "search|filter|query|keyword", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)));
            if (field is null && candidates.Count == 1 && Regex.IsMatch(Text(form), "search|find|filter", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))) field = candidates[0];
            if (field is null || field.GetAttributeValue("name", "").Length == 0) continue;
            if (!Uri.TryCreate(page, form.GetAttributeValue("action", ""), out var action) || !SameOrigin(page, action)) continue;
            var values = inputs.Where(n => n.GetAttributeValue("type", "").Equals("hidden", StringComparison.OrdinalIgnoreCase) && n.GetAttributeValue("name", "").Length > 0)
                .Select(n => new KeyValuePair<string, string>(n.GetAttributeValue("name", ""), WebUtility.HtmlDecode(n.GetAttributeValue("value", "")))).ToList();
            values.Add(new(field.GetAttributeValue("name", ""), "Safe2Cross"));
            var submit = inputs.FirstOrDefault(n => n.GetAttributeValue("type", "").Equals("submit", StringComparison.OrdinalIgnoreCase) && n.GetAttributeValue("name", "").Length > 0);
            if (submit is not null) values.Add(new(submit.GetAttributeValue("name", ""), submit.GetAttributeValue("value", "")));
            return new(action, form.GetAttributeValue("method", "get").Equals("post", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Post : HttpMethod.Get, values);
        }
        return null;
    }
    private static bool SameOrigin(Uri a, Uri b) => a.Scheme == b.Scheme && a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
    public static async Task<Snapshot> FetchAsync(string url, CancellationToken token, HttpMessageHandler? transport = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Configure a valid stockroom HTTP/HTTPS address in Setup.");
        CancellationToken originalToken = token;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8)); token = timeout.Token;
        using var handler = transport ?? new HttpClientHandler { UseDefaultCredentials = true, AllowAutoRedirect = false };
        using var client = new HttpClient(handler, false) { Timeout = TimeSpan.FromSeconds(8) };
        var landing = await ReadPageAsync(client, new HttpRequestMessage(HttpMethod.Get, uri), uri, token);
        var snapshot = await Task.Run(() => Parse(landing.Html), token);
        if (snapshot.Rows.Count > 0) return snapshot;
        if (FindSearchForm(landing.Html, landing.Uri) is { } form)
        {
            HttpRequestMessage request;
            if (form.Method == HttpMethod.Post) request = new(form.Method, form.Action) { Content = new FormUrlEncodedContent(form.Values) };
            else
            {
                using var encoded = new FormUrlEncodedContent(form.Values);
                var builder = new UriBuilder(form.Action) { Query = string.Join("&", new[] { form.Action.Query.TrimStart('?'), await encoded.ReadAsStringAsync(token) }.Where(s => s.Length > 0)) };
                request = new(form.Method, builder.Uri);
            }
            var result = await ReadPageAsync(client, request, uri, token); snapshot = await Task.Run(() => Parse(result.Html), token);
            if (snapshot.Rows.Count > 0) return snapshot;
        }
        // A search which does not change the address often populates its table with JavaScript.
        // Use the actual page in an isolated browser only when the lightweight reader cannot read it.
        if (transport is null && OperatingSystem.IsWindows()) return await StockroomBrowserReader.FetchAsync(url, originalToken);
        return snapshot;
    }
    private static async Task<(string Html, Uri Uri)> ReadPageAsync(HttpClient client, HttpRequestMessage request, Uri origin, CancellationToken token)
    {
        for (int redirect = 0; redirect <= 3; redirect++)
        {
            using var sent = request;
            using var response = await client.SendAsync(sent, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (response.Headers.Location is not { } location || !Uri.TryCreate(sent.RequestUri, location, out var next) || !SameOrigin(origin, next)) throw new InvalidDataException("The stockroom redirected to another site. Open it to confirm the sign-in address.");
                if (response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect && sent.Method != HttpMethod.Get) throw new InvalidDataException("The stockroom search uses a redirected form. Configure its final page address in Setup.");
                request = new(HttpMethod.Get, next); continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 4 * 1024 * 1024) throw new InvalidDataException("The stockroom response exceeds 4 MB.");
            await using var stream = await response.Content.ReadAsStreamAsync(token); using var buffer = new MemoryStream(); var block = new byte[16384]; int read;
            while ((read = await stream.ReadAsync(block, token)) > 0) { if (buffer.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("The stockroom response exceeds 4 MB."); buffer.Write(block, 0, read); }
            token.ThrowIfCancellationRequested(); return (Encoding.UTF8.GetString(buffer.ToArray()), sent.RequestUri!);
        }
        request.Dispose(); throw new InvalidDataException("The stockroom page redirected too many times.");
    }
}
