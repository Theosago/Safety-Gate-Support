using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

public static class SearchService
{
    public static List<GlobalSearchResult> Search(IReadOnlyList<SearchIndexEntry> entries, string query, int limit = 40)
    {
        if (limit <= 0 || entries.Count == 0) return [];

        // Preserve the original phrase and space-separated token matching rules,
        // but split once per search rather than once for each candidate field.
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var capacity = Math.Min(limit, entries.Count);
        var selected = new PriorityQueue<ScoredEntry, ScoredEntry>(capacity, WorstFirstComparer.Instance);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var score = Score(entry, query, tokens);
            if (score < 0) continue;
            var candidate = new ScoredEntry(entry, score, index);
            if (selected.Count < capacity) selected.Enqueue(candidate, candidate);
            else selected.EnqueueDequeue(candidate, candidate);
        }

        // Only the visible results need sorting and result/snippet allocation.
        var ordered = new ScoredEntry[selected.Count];
        for (var index = 0; index < ordered.Length; index++) ordered[index] = selected.Dequeue();
        Array.Sort(ordered, BestFirstComparer.Instance);
        var results = new List<GlobalSearchResult>(ordered.Length);
        foreach (var item in ordered) results.Add(ToResult(item.Entry, query, tokens));
        return results;
    }

    private static int Score(SearchIndexEntry entry, string query, string[] tokens)
    {
        if (entry.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) return 5;
        if (!string.IsNullOrWhiteSpace(entry.NotesText) && Matches(entry.NotesText, query, tokens)) return 10;
        if (entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)) return 15;
        return Matches(entry.SearchText, query, tokens) ? 25 : -1;
    }

    private static bool Matches(string? value, string query, string[] tokens)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (tokens.Length <= 1) return false;
        foreach (var token in tokens)
            if (!value.Contains(token, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static GlobalSearchResult ToResult(SearchIndexEntry entry, string query, string[] tokens)
    {
        var notesMatch = entry.Kind == "System" && !string.IsNullOrWhiteSpace(entry.NotesText) && Matches(entry.NotesText, query, tokens);
        return new GlobalSearchResult
        {
            Title = entry.Title,
            Subtitle = notesMatch ? $"Shared notes • {entry.BaseSubtitle} • {BuildSnippet(entry.NotesText, query)}" : entry.BaseSubtitle,
            Kind = entry.Kind,
            Target = entry.Target,
            SystemId = entry.SystemId,
            Body = entry.Body,
            MatchSource = notesMatch ? "SharedNotes" : "",
            MatchText = notesMatch ? query : ""
        };
    }

    private static string BuildSnippet(string text, string query)
    {
        var clean = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length <= 110) return clean;
        var matchIndex = clean.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (matchIndex < 0) matchIndex = 0;
        var start = Math.Max(0, matchIndex - 35);
        var length = Math.Min(110, clean.Length - start);
        var snippet = clean.Substring(start, length);
        return (start > 0 ? "…" : "") + snippet + (start + length < clean.Length ? "…" : "");
    }

    private readonly record struct ScoredEntry(SearchIndexEntry Entry, int Score, int OriginalIndex);

    private sealed class BestFirstComparer : IComparer<ScoredEntry>
    {
        public static readonly BestFirstComparer Instance = new();

        public int Compare(ScoredEntry left, ScoredEntry right)
        {
            var comparison = left.Score.CompareTo(right.Score);
            if (comparison != 0) return comparison;
            comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Entry.Title, right.Entry.Title);
            // LINQ's original OrderBy/ThenBy was stable when both keys tied.
            return comparison != 0 ? comparison : left.OriginalIndex.CompareTo(right.OriginalIndex);
        }
    }

    private sealed class WorstFirstComparer : IComparer<ScoredEntry>
    {
        public static readonly WorstFirstComparer Instance = new();
        public int Compare(ScoredEntry left, ScoredEntry right) => BestFirstComparer.Instance.Compare(right, left);
    }
}
