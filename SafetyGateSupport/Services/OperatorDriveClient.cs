using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Net.Http.Json;

namespace SafetyGateSupport.Services;

internal sealed class OperatorDriveClient(HttpClient http, Func<CancellationToken, Task<string>> authorize)
{
    internal const string FolderMime = "application/vnd.google-apps.folder";
    private const string Api = "https://www.googleapis.com/drive/v3/";
    private const string Upload = "https://www.googleapis.com/upload/drive/v3/";
    private const string Fields = "id,parents,driveId,mimeType,trashed,md5Checksum,version,webViewLink";
    private const string FileQuery = "?supportsAllDrives=true&fields=" + Fields;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal sealed record Item(string Id, string[]? Parents, string? DriveId, string MimeType,
        bool Trashed, string? Md5Checksum, string? Version, string? WebViewLink)
    {
        public string? ETag { get; init; }
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

    internal async Task<Item> FolderAsync(Item parent, string name, bool create, CancellationToken token)
    {
        var folder = await FindAsync(parent, name, token);
        if (folder is null && create)
            folder = await ReadAsync<Item>(HttpMethod.Post, Api + "files" + FileQuery,
                JsonContent.Create(new { name, mimeType = FolderMime, parents = new[] { parent.Id } }), token);
        if (folder?.MimeType != FolderMime || folder.Trashed || folder.DriveId != parent.DriveId ||
            folder.Parents?.Contains(parent.Id) != true)
            throw new InvalidOperationException($"Expected a folder named {name} under the configured Drive path.");
        return folder;
    }

    internal async Task<Item?> FindAsync(Item parent, string name, CancellationToken token)
    {
        string q = $"'{QueryLiteral(parent.Id)}' in parents and name = '{QueryLiteral(name)}' and trashed = false";
        string url = Api + "files?corpora=drive&includeItemsFromAllDrives=true&supportsAllDrives=true&pageSize=100"
            + "&driveId=" + Uri.EscapeDataString(parent.DriveId!) + "&q=" + Uri.EscapeDataString(q)
            + "&fields=nextPageToken,incompleteSearch,files(" + Fields + ")";
        Item? item = null;
        string? page = null;
        do
        {
            var results = await ReadAsync<FileList>(HttpMethod.Get,
                url + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page)), null, token);
            if (results.IncompleteSearch) throw new IOException("Drive search was incomplete. Sync will retry.");
            foreach (var match in results.Files)
            {
                if (item is not null) throw new InvalidOperationException($"Multiple Drive items named {name}. Resolve the duplicate names to resume.");
                item = match;
            }
            page = results.NextPageToken;
        } while (!string.IsNullOrEmpty(page));
        return item is null ? null : await GetAsync(item.Id, token);
    }

    internal async Task<string> GenerateIdAsync(CancellationToken token) =>
        (await ReadAsync<GeneratedIds>(HttpMethod.Get, Api + "files/generateIds?count=1&space=drive", null, token)).Ids.Single();

    internal async Task<Item> CreatePdfAsync(string id, string parent, string name, byte[] bytes, CancellationToken token)
    {
        using var body = new MultipartContent("related");
        body.Add(JsonContent.Create(new { id, name, mimeType = "application/pdf", parents = new[] { parent } }));
        body.Add(PdfContent(bytes));
        return await ReadAsync<Item>(HttpMethod.Post, Upload + "files" + FileQuery + "&uploadType=multipart", body, token);
    }

    internal Task<Item> UpdatePdfAsync(string id, byte[] bytes, string? etag, CancellationToken token) =>
        ReadAsync<Item>(HttpMethod.Patch, Upload + "files/" + Uri.EscapeDataString(id) + FileQuery + "&uploadType=media",
            PdfContent(bytes), token, etag);

    internal async Task<byte[]> DownloadAsync(string id, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Get,
            Api + "files/" + Uri.EscapeDataString(id) + "?supportsAllDrives=true&alt=media", null, token);
        using var stream = await response.Content.ReadAsStreamAsync(token);
        return await OperatorDrivePilot.ReadLimitedAsync(stream, token);
    }

    private async Task<T> ReadAsync<T>(HttpMethod method, string url, HttpContent? content, CancellationToken token, string? etag = null)
    {
        using var response = await SendAsync(method, url, content, token, etag);
        var result = await response.Content.ReadFromJsonAsync<T>(Json, token)
            ?? throw new IOException("Drive returned an empty response.");
        return result is Item item ? (T)(object)(item with { ETag = response.Headers.ETag?.ToString() }) : result;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content,
        CancellationToken token, string? etag = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await authorize(token));
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException($"Google Drive returned HTTP {(int)status} for {method}. Check the linked item and Shared Drive access.", null, status);
    }

    private static ByteArrayContent PdfContent(byte[] bytes) =>
        new(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } };

    private static string QueryLiteral(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}
