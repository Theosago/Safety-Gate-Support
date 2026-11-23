using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Net.Http.Json;

namespace SafetyGateSupport.Services;

internal sealed class OperatorDriveClient(HttpClient http, Func<CancellationToken, Task<string>> authorize)
{
    private const int MultipartLimit = 5 * 1024 * 1024;
    internal const string FolderMime = "application/vnd.google-apps.folder";
    internal const string DocMime = "application/vnd.google-apps.document";
    private const string Api = "https://www.googleapis.com/drive/v3/";
    private const string Upload = "https://www.googleapis.com/upload/drive/v3/";
    private const string Fields = "id,parents,driveId,mimeType,trashed,md5Checksum,version,webViewLink,resourceKey";
    private const string FileQuery = "?supportsAllDrives=true&fields=" + Fields;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal sealed record Item(string Id, string[]? Parents, string? DriveId, string MimeType,
        bool Trashed, string? Md5Checksum, string? Version, string? WebViewLink)
    {
        public string? ETag { get; init; }
        public string? ResourceKey { get; init; }
    }

    private sealed record FileList(Item[] Files, string? NextPageToken, bool IncompleteSearch);
    private sealed record GeneratedIds(string[] Ids);

    internal Task<Item> GetAsync(string id, CancellationToken token) =>
        ReadAsync<Item>(HttpMethod.Get, Api + "files/" + Uri.EscapeDataString(id) + FileQuery, null, token);

    internal async Task<Item?> GetPendingAsync(string id, CancellationToken token)
    {
        try { return await GetAsync(id, token); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    internal async Task CheckUnchangedAsync(Item previous, CancellationToken token)
    {
        var current = await GetAsync(previous.Id, token);
        if (current.Version != previous.Version || current.Md5Checksum != previous.Md5Checksum ||
            current.Trashed != previous.Trashed || current.DriveId != previous.DriveId ||
            !(current.Parents ?? []).SequenceEqual(previous.Parents ?? []))
            throw new IOException("The Drive file changed during sync. Sync will retry.");
    }

    internal Task<Item> CreateFolderAsync(string id, Item parent, string name, CancellationToken token) =>
        ReadAsync<Item>(HttpMethod.Post, Api + "files" + FileQuery,
            JsonContent.Create(new { id, name, mimeType = FolderMime, parents = new[] { parent.Id } }), token);

    internal static string FolderLink(Item folder) => folder.WebViewLink ??
        $"https://drive.google.com/drive/folders/{folder.Id}" +
        (folder.ResourceKey is null ? "" : "?resourcekey=" + Uri.EscapeDataString(folder.ResourceKey));

    internal static string PdfLink(Item file) => file.WebViewLink ??
        $"https://drive.google.com/file/d/{file.Id}/view" +
        (file.ResourceKey is null ? "" : "?resourcekey=" + Uri.EscapeDataString(file.ResourceKey));

    internal async Task<Item?> FindAsync(Item parent, string name, CancellationToken token)
    {
        string query = $"'{QueryLiteral(parent.Id)}' in parents and name = '{QueryLiteral(name)}' and trashed = false";
        var matches = await ListAsync(parent.DriveId, query, token);
        if (matches.Count > 1) throw new InvalidOperationException($"Multiple Drive items named {name}. Resolve the duplicate names to resume.");
        return matches.Count == 0 ? null : await GetAsync(matches[0].Id, token);
    }

    internal async Task<IReadOnlyDictionary<string, Item>> ListNotesAsync(Item root, IEnumerable<string> folders,
        CancellationToken token)
    {
        var notes = new Dictionary<string, Item>(StringComparer.Ordinal);
        foreach (var batch in folders.Distinct(StringComparer.Ordinal).Chunk(20))
        {
            var parents = string.Join(" or ", batch.Select(id => $"'{QueryLiteral(id)}' in parents"));
            string query = $"trashed = false and mimeType = '{DocMime}' and ({parents})";
            foreach (var item in await ListAsync(root.DriveId, query, token)) notes[item.Id] = item;
        }
        return notes;
    }

    private async Task<List<Item>> ListAsync(string? driveId, string query, CancellationToken token)
    {
        string corpus = driveId is null
            ? "corpora=user"
            : "corpora=drive&includeItemsFromAllDrives=true&driveId=" + Uri.EscapeDataString(driveId);
        string url = Api + "files?" + corpus + "&supportsAllDrives=true&pageSize=100"
            + "&q=" + Uri.EscapeDataString(query)
            + "&fields=nextPageToken,incompleteSearch,files(" + Fields + ")";
        var files = new List<Item>();
        string? page = null;
        do
        {
            var results = await ReadAsync<FileList>(HttpMethod.Get,
                url + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page)), null, token);
            if (results.IncompleteSearch) throw new IOException("Drive search was incomplete. Sync will retry.");
            files.AddRange(results.Files);
            page = results.NextPageToken;
        } while (!string.IsNullOrEmpty(page));
        return files;
    }

    internal async Task<string> GenerateIdAsync(CancellationToken token) =>
        (await ReadAsync<GeneratedIds>(HttpMethod.Get, Api + "files/generateIds?count=1&space=drive", null, token)).Ids.Single();

    internal async Task<Item> CreatePdfAsync(string id, string parent, string name, byte[] bytes, CancellationToken token)
    {
        using var metadata = JsonContent.Create(new { id, name, mimeType = "application/pdf", parents = new[] { parent } });
        if (bytes.Length > MultipartLimit)
            return await ResumablePdfAsync(HttpMethod.Post, Upload + "files", metadata, bytes, token);
        using var body = new MultipartContent("related");
        body.Add(metadata);
        body.Add(PdfContent(bytes));
        return await ReadAsync<Item>(HttpMethod.Post, Upload + "files" + FileQuery + "&uploadType=multipart", body, token);
    }

    internal Task<Item> UpdatePdfAsync(string id, byte[] bytes, string? etag, CancellationToken token)
    {
        string url = Upload + "files/" + Uri.EscapeDataString(id);
        return bytes.Length > MultipartLimit
            ? ResumablePdfAsync(HttpMethod.Patch, url, JsonContent.Create(new { }), bytes, token, etag)
            : ReadAsync<Item>(HttpMethod.Patch, url + FileQuery + "&uploadType=media", PdfContent(bytes), token, etag);
    }

    private async Task<Item> ResumablePdfAsync(HttpMethod method, string url, HttpContent metadata, byte[] bytes,
        CancellationToken token, string? etag = null)
    {
        using var response = await SendAsync(method, url + FileQuery + "&uploadType=resumable", metadata, token, etag,
            request =>
            {
                request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/pdf");
                request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            });
        if (response.Headers.Location is not { IsAbsoluteUri: true } session ||
            session.Scheme != Uri.UriSchemeHttps || session.Host != "www.googleapis.com")
            throw new IOException("Drive did not provide a usable upload session.");
        return await ReadAsync<Item>(HttpMethod.Put, session.AbsoluteUri, PdfContent(bytes), token);
    }

    internal async Task<Item> NotesAsync(Item unit, string? id, CancellationToken token)
    {
        const string name = "Operator notes";
        const string mimeType = DocMime;
        var notes = id is null ? await FindAsync(unit, name, token) : await GetAsync(id, token);
        notes ??= await ReadAsync<Item>(HttpMethod.Post, Api + "files" + FileQuery,
            JsonContent.Create(new { name, mimeType, parents = new[] { unit.Id } }), token);
        if (notes.Trashed || notes.MimeType != mimeType || notes.DriveId != unit.DriveId ||
            notes.Parents?.Contains(unit.Id) != true)
            throw new InvalidOperationException("Expected an Operator notes Google Doc in the unit folder.");
        return notes;
    }

    private async Task<T> ReadAsync<T>(HttpMethod method, string url, HttpContent? content, CancellationToken token, string? etag = null)
    {
        using var response = await SendAsync(method, url, content, token, etag);
        var result = await response.Content.ReadFromJsonAsync<T>(Json, token)
            ?? throw new IOException("Drive returned an empty response.");
        return result is Item item ? (T)(object)(item with { ETag = response.Headers.ETag?.ToString() }) : result;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content,
        CancellationToken token, string? etag = null, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await authorize(token));
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        configure?.Invoke(request);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException($"Google Drive returned HTTP {(int)status} for {method}. Check the linked item and folder access for the signed-in account.", null, status);
    }

    private static ByteArrayContent PdfContent(byte[] bytes) =>
        new(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } };

    private static string QueryLiteral(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}
