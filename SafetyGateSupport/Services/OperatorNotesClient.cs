using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SafetyGateSupport.Services;

internal sealed class NotesApiSetupException() : Exception("Enable the Google Docs API in the Google project used by Safety Gate Support.");

internal sealed record OperatorNotesDocument(string Id, string Revision, string Text);

internal sealed class OperatorNotesClient(HttpClient http, Func<CancellationToken, Task<string>> authorize)
{
    private const string Api = "https://docs.googleapis.com/v1/documents/";
    private const int MaxLength = 100_000;

    internal async Task<OperatorNotesDocument> ReadAsync(string id, CancellationToken token)
    {
        string url = Api + Uri.EscapeDataString(id) + "?fields=documentId,revisionId,body(content)&suggestionsViewMode=SUGGESTIONS_INLINE";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var reply = await SendAsync(request, token);
        using var result = JsonDocument.Parse(await reply.Content.ReadAsByteArrayAsync(token));
        var root = result.RootElement;
        if (root.GetProperty("documentId").GetString() != id ||
            !root.TryGetProperty("revisionId", out var revision) || revision.GetString() is not { Length: > 0 } revisionId)
            throw new IOException("The Google publishing account needs edit access to this Operator notes document.");

        var text = new StringBuilder();
        foreach (var block in root.GetProperty("body").GetProperty("content").EnumerateArray())
        {
            if (block.TryGetProperty("sectionBreak", out _) && text.Length == 0) continue;
            if (!block.TryGetProperty("paragraph", out var paragraph))
                throw new IOException("Use text paragraphs in the first tab of Operator notes to sync with the desktop.");
            foreach (var element in paragraph.GetProperty("elements").EnumerateArray())
            {
                if (!element.TryGetProperty("textRun", out var run) || element.TryGetProperty("suggestedInsertionIds", out _) ||
                    element.TryGetProperty("suggestedDeletionIds", out _))
                    throw new IOException("Operator notes has an image, object or suggested edit. Resolve it in Google Docs before syncing.");
                text.Append(run.GetProperty("content").GetString());
                if (text.Length > MaxLength + 1) throw new IOException("Shared notes exceed 100,000 characters.");
            }
        }
        // The last paragraph's mandatory newline is excluded from the app's editable text.
        if (text.Length == 0 || text[^1] != '\n')
            throw new IOException("Google Docs returned an unexpected notes layout.");
        text.Length--;
        return new(id, revisionId, text.ToString());
    }

    internal async Task WriteAsync(OperatorNotesDocument document, string text, CancellationToken token)
    {
        if (text == document.Text) return;
        if (text.Length > MaxLength || text.Any(c => char.IsControl(c) && c is not ('\n' or '\t') ||
            c is >= '\uE000' and <= '\uF8FF'))
            throw new IOException("Shared notes must contain supported text and be under 100,000 characters.");

        // Edit the smallest changed range so formatting outside it survives.
        int start = 0, common = Math.Min(document.Text.Length, text.Length);
        while (start < common && document.Text[start] == text[start]) start++;
        if (start > 0 && char.IsHighSurrogate(text[start - 1])) start--;
        int oldEnd = document.Text.Length, newEnd = text.Length;
        while (oldEnd > start && newEnd > start && document.Text[oldEnd - 1] == text[newEnd - 1])
        {
            oldEnd--;
            newEnd--;
        }
        if (newEnd < text.Length && char.IsLowSurrogate(text[newEnd])) { oldEnd++; newEnd++; }

        var edits = new List<object>();
        if (oldEnd > start)
            edits.Add(new { deleteContentRange = new { range = new { startIndex = start + 1, endIndex = oldEnd + 1 } } });
        if (newEnd > start)
            edits.Add(new { insertText = new { location = new { index = start + 1 }, text = text[start..newEnd] } });
        using var request = new HttpRequestMessage(HttpMethod.Post, Api + Uri.EscapeDataString(document.Id) + ":batchUpdate")
        {
            Content = JsonContent.Create(new { writeControl = new { requiredRevisionId = document.Revision }, requests = edits })
        };
        using var response = await SendAsync(request, token);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await authorize(token));
        var reply = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (reply.IsSuccessStatusCode) return reply;
        try
        {
            var status = reply.StatusCode;
            if (status == HttpStatusCode.Forbidden)
            {
                string error = await reply.Content.ReadAsStringAsync(token);
                if (error.Contains("SERVICE_DISABLED", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("accessNotConfigured", StringComparison.OrdinalIgnoreCase))
                    throw new NotesApiSetupException();
            }
            if (status is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                throw new IOException("Google Docs could not apply the notes edit to that revision. The app will check again.");
            throw new HttpRequestException($"Google Docs returned HTTP {(int)status}. Check access to Operator notes.", null, status);
        }
        finally { reply.Dispose(); }
    }
}
