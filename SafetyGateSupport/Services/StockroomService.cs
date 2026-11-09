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
    public sealed record Row(string Stock, decimal Quantity, string Site, string Location, string QuantityLabel)
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string Manufacturer { get; init; } = "";
        public string Photo { get; init; } = "";
    }
    public sealed record Snapshot(IReadOnlyList<Row> Rows, DateTimeOffset CheckedUtc)
    {
        public string[][] TableSample { get; init; } = [];
        public string Reader { get; init; } = "HTML";
        // Diagnostic rows must remain exportable without becoming current quantities.
        public bool QuantitiesConfirmed { get; init; } = true;
    }
    public static bool IsItemId(string? text) => text is { Length: > 0 and <= 64 } && char.IsAsciiLetterOrDigit(text[0]) && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/');
    public static string StockKey(string? text)
    {
        string value = (text ?? "").Trim();
        if (value.Length > 0 && value.All(char.IsAsciiDigit)) { string key = value.TrimStart('0'); return key.Length == 0 ? "0" : key; }
        return value.ToUpperInvariant();
    }
    private static string Text(HtmlNode node) => WebUtility.HtmlDecode(node.InnerText).Replace('\u00a0', ' ').Trim();
    private static string Header(string text) => new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    public static Snapshot Parse(string html)
    {
        if (Encoding.UTF8.GetByteCount(html) > 4 * 1024 * 1024) throw new InvalidDataException("The stockroom page is larger than the supported 4 MB response.");
        var document = new HtmlDocument(); document.LoadHtml(html); var rows = new List<Row>(); var seenRows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in document.DocumentNode.Descendants("table"))
        {
            int stock = -1, quantity = -1, site = -1, location = -1, name = -1, description = -1, manufacturer = -1; string label = "";
            foreach (var row in table.Descendants("tr").Where(r => r.Ancestors("table").FirstOrDefault() == table))
            {
                var cells = row.ChildNodes.Where(n => n.Name is "td" or "th").ToArray(); if (cells.Length == 0) continue;
                var names = cells.Select(c => Header(Text(c))).ToArray();
                int stockHeader = Array.FindIndex(names, n => new[] { "stocknumber", "stockno", "stock", "itemnumber", "itemno", "materialnumber", "material", "partnumber", "partno", "part", "item", "itemid", "itemcode", "stockcode", "inventorynumber" }.Contains(n));
                int qtyHeader = Array.FindIndex(names, n => new[] { "availablequantity", "qtyavailable", "quantityavailable", "available" }.Contains(n));
                if (qtyHeader < 0) qtyHeader = Array.FindIndex(names, n => new[] { "quantityonhand", "qtyonhand", "onhand", "currentquantity", "onhandqty", "qoh" }.Contains(n));
                if (qtyHeader < 0) qtyHeader = Array.FindIndex(names, n => new[] { "quantity", "qty", "availablequantity", "qtyavailable", "quantityavailable", "available", "qtyinstock", "quantityinstock" }.Contains(n));
                if (stockHeader >= 0 && qtyHeader >= 0)
                {
                    // Colspans/multiple header rows need site-specific mapping, not positional guessing.
                    if (cells.Any(c => c.GetAttributeValue("colspan", "1") != "1" || c.GetAttributeValue("rowspan", "1") != "1")) { stock = quantity = -1; continue; }
                    stock = stockHeader; quantity = qtyHeader; label = Text(cells[quantity]);
                    site = Array.FindIndex(names, n => new[] { "site", "plant", "warehouse" }.Contains(n));
                    name = Array.FindIndex(names, n => n is "itemname" or "name" or "partname");
                    description = Array.FindIndex(names, n => n is "description" or "itemdescription" or "partdescription");
                    manufacturer = Array.FindIndex(names, n => n is "manufacturer" or "brand" or "make");
                    location = Array.FindIndex(names, n => new[] { "location", "bin", "binlocation", "storeroomlocation", "storagebin" }.Contains(n)); continue;
                }
                if (stock < 0 || quantity < 0 || cells.Length <= Math.Max(stock, quantity) || cells.Any(c => c.GetAttributeValue("colspan", "1") != "1" || c.GetAttributeValue("rowspan", "1") != "1")) continue;
                string number = Text(cells[stock]);
                if (!IsItemId(number)) continue;
                string amount = Text(cells[quantity]);
                if (!Regex.IsMatch(amount, @"^(?:\d+|\d{1,3}(?:,\d{3})+)(?:\.\d+)?$", RegexOptions.None, TimeSpan.FromMilliseconds(100))) continue;
                if (!decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var count) || count < 0) continue;
                string identity = System.Text.Json.JsonSerializer.Serialize(new { stock, quantity, site, location, Cells = cells.Select(Text).ToArray() });
                if (!seenRows.Add(identity)) continue;
                string Value(int index) => index >= 0 && index < cells.Length ? Text(cells[index]) : "";
                rows.Add(new(number, count, Value(site), Value(location), label)
                {
                    Name = Value(name), Description = Value(description), Manufacturer = Value(manufacturer),
                    Photo = cells.SelectMany(c => c.Descendants("img")).Select(i => i.GetAttributeValue("src", "")).FirstOrDefault() ?? ""
                });
            }
        }
        return new(rows, DateTimeOffset.UtcNow)
        {
            TableSample = document.DocumentNode.Descendants("tr").Where(r => r.Ancestors("table").Any()).Take(250).Select(r => r.ChildNodes.Where(c => c.Name is "th" or "td").Take(16).Select(c => { string value = Text(c); return value[..Math.Min(value.Length, 500)]; }).ToArray()).ToArray()
        };
    }
    public static Row? Find(Snapshot snapshot, PartRecord part, bool plantScoped = false)
    {
        if (!snapshot.QuantitiesConfirmed) return null;
        var matches = snapshot.Rows.Where(r => StockKey(r.Stock) == StockKey(part.PartNumber)).ToList();
        // A plant inventory endpoint is already scoped. Legacy snapshot site/bin values
        // must never select a different plant or hide a new plant's location.
        if (plantScoped) return matches.Count == 1 ? matches[0] : null;
        string site = part.SiteValue;
        if (site.Length > 0 && !site.Equals("Not supplied", StringComparison.OrdinalIgnoreCase) && matches.Any(r => r.Site.Length > 0)) matches = matches.Where(r => r.Site.Equals(site, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1 && part.LocationValue.Length > 0 && matches.Any(r => r.Location.Length > 0)) matches = matches.Where(r => r.Location.Equals(part.LocationValue, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
    public static PartRecord ImportItem(Snapshot snapshot, string itemId)
    {
        if (!IsItemId(itemId)) throw new InvalidOperationException("Enter an Item ID using letters, digits or - _ . / (up to 64 characters).");
        var row = Find(snapshot, new PartRecord { PartNumber = itemId }, true)
            ?? throw new InvalidOperationException("No unique, confirmed stockroom match for Item ID " + itemId + ". Nothing was imported.");
        string title = row.Name.Length > 0 ? row.Name : row.Description;
        if (title.Length == 0) throw new InvalidOperationException("The stockroom did not return an item name or description. Nothing was imported.");
        return new PartRecord { PartNumber = row.Stock, Description = title, Manufacturer = row.Manufacturer,
            StockroomDescription = row.Description, BinLocation = row.Location, ReportedQuantity = row.Quantity, Site = row.Site,
            Notes = row.Name.Length > 0 && row.Description != row.Name ? row.Description : "" };
    }
    public static IReadOnlyList<string> ParseItemIds(string text)
    {
        var ids = text.Split(['\r', '\n', '\t', ' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0) throw new InvalidOperationException("Enter at least one Item ID.");
        var invalid = ids.FirstOrDefault(id => !IsItemId(id));
        if (invalid is not null) throw new InvalidOperationException("Invalid Item ID: " + invalid);
        var unique = ids.DistinctBy(StockKey).ToArray();
        if (unique.Length > 100) throw new InvalidOperationException("Import at most 100 unique Item IDs at a time.");
        return unique;
    }
    public static async Task<Snapshot> FetchCatalogAsync(IReadOnlyCollection<string> ids, Func<CancellationToken, Task<Snapshot>> commonSearch,
        Func<string, CancellationToken, Task<Snapshot>> itemSearch, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(120));
        var initial = await commonSearch(deadline.Token);
        var rows = initial.QuantitiesConfirmed ? initial.Rows.ToList() : new List<Row>();
        var samples = initial.TableSample.ToList(); var failures = new List<string>(); bool confirmed = initial.QuantitiesConfirmed;
        foreach (string id in ids.Where(IsItemId).DistinctBy(StockKey))
        {
            token.ThrowIfCancellationRequested();
            // An ambiguous returned item stays ambiguous. A targeted query must never hide a second bin/lot.
            if (rows.Any(r => StockKey(r.Stock) == StockKey(id))) continue;
            try
            {
                var result = await itemSearch(id, deadline.Token);
                if (!result.QuantitiesConfirmed) { failures.Add(id + ": unconfirmed search"); continue; }
                confirmed = true;
                var matches = result.Rows.Where(r => StockKey(r.Stock) == StockKey(id)).ToList();
                rows.AddRange(matches); samples.AddRange(result.TableSample);
                if (matches.Count != 1) failures.Add(id + (matches.Count == 0 ? ": not found" : ": ambiguous"));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
            { failures.Add("Catalog check reached its two-minute limit; remaining IDs were not checked."); break; }
            catch (Exception error) when (!token.IsCancellationRequested && error is IOException or TimeoutException or InvalidOperationException or HttpRequestException)
            { failures.Add(id + ": " + error.Message); }
        }
        token.ThrowIfCancellationRequested();
        if (!confirmed) return initial;
        return new(rows, initial.CheckedUtc) { QuantitiesConfirmed = true, TableSample = samples.Take(250).ToArray(),
            Reader = initial.Reader + "; targeted Item ID checks" + (failures.Count > 0 ? "; " + string.Join("; ", failures) : "") };
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
    public static async Task<Snapshot> FetchAsync(string url, CancellationToken token, HttpMessageHandler? transport = null, IReadOnlyCollection<string>? requested = null, Func<string, CancellationToken, Task<Snapshot>>? interactiveReader = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Configure a valid stockroom HTTP/HTTPS address in Setup.");
        CancellationToken originalToken = token;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8)); token = timeout.Token;
        using var handler = transport ?? new HttpClientHandler { UseDefaultCredentials = true, AllowAutoRedirect = false };
        using var client = new HttpClient(handler, false) { Timeout = TimeSpan.FromSeconds(8) };
        var landing = await ReadPageAsync(client, new HttpRequestMessage(HttpMethod.Get, uri), uri, token);
        var snapshot = await Task.Run(() => Parse(landing.Html), token);
        var landingDocument = new HtmlDocument(); landingDocument.LoadHtml(landing.Html);
        bool serverSearchRequired = landingDocument.DocumentNode.Descendants("input").Any(n =>
            n.GetClasses().Contains("search-input") && n.Ancestors().Any(a => a.GetClasses().Contains("inventory-container")));
        bool Complete(Snapshot value) => !serverSearchRequired && value.Rows.Count > 0 && (requested is null || requested.All(id => Find(value, new PartRecord { PartNumber = id }, true) is not null));
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
            if (Complete(snapshot)) return snapshot;
        }
        if (Complete(snapshot)) return snapshot;
        // A search which does not change the address often populates its table with JavaScript.
        // Use the actual page in an isolated browser only when the lightweight reader cannot read it.
        if (interactiveReader is not null || transport is null && OperatingSystem.IsWindows())
        {
            try { return await (interactiveReader ?? StockroomBrowserReader.FetchAsync)(url, originalToken); }
            catch (Exception error) when (!originalToken.IsCancellationRequested && snapshot.TableSample.Length > 0 && error is IOException or TimeoutException or InvalidOperationException)
            { return snapshot with { QuantitiesConfirmed = false, Reader = "Incomplete HTML result; browser search failed: " + error.Message }; }
        }
        return serverSearchRequired ? snapshot with { QuantitiesConfirmed = false, Reader = "Incomplete HTML result; server-confirmed search requires the Windows stockroom reader." } : snapshot;
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
