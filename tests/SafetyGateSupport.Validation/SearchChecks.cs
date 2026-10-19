using System.Diagnostics;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport.Validation;

internal static class SearchChecks
{
    internal static IEnumerable<(string Name, Action Check)> Cases =>
    [
        ("Search preserves phrase, notes, token scores, and result payloads", RankingAndPayloads),
        ("Search preserves shared-note snippets and whitespace/case matching", NotesAndWhitespace),
        ("Search preserves stable ties and result limits", StableTiesAndLimits),
        ("Search agrees with the previous implementation on randomized indexes", RandomizedEquivalence)
    ];

    private static void RankingAndPayloads()
    {
        SearchIndexEntry[] entries =
        [
            Entry("tokens", "Zulu", "sensor then door"),
            Entry("phrase", "Another", "before door sensor after"),
            Entry("notes", "Later", "", "sensor after door"),
            Entry("contains", "A door sensor", ""),
            Entry("prefix", "Door Sensor Guide", ""),
            Entry("excluded", "Unrelated", "door")
        ];
        var results = SearchService.Search(entries, "door sensor");
        Require(results.Select(result => result.Target).SequenceEqual(new[] { "prefix", "contains", "notes", "phrase", "tokens" }),
            "Prefix, title, notes, phrase, and token-only results must retain the previous ranking.");
        Require(results.Single(result => result.Target == "notes").MatchSource == "SharedNotes", "A shared-note match must retain its navigation source.");
        AssertSame(entries, "door sensor");

        // Notes rank even non-system entries, but only a System displays a note snippet.
        entries[2].Kind = "Part";
        AssertSame(entries, "door sensor");
        Require(SearchService.Search(entries, "door sensor")[2].MatchSource == "", "Non-system matches must not gain notes navigation.");
    }

    private static void NotesAndWhitespace()
    {
        SearchIndexEntry[] entries =
        [
            Entry("short", "Door Sensor", "", "\t Door\r\nsensor   replaced ✓  "),
            Entry("middle", "System B", "", new string('x', 100) + "  DOOR SENSOR  " + new string('y', 150)),
            Entry("tokens", "System C", "", "sensor " + new string('z', 160) + " door"),
            Entry("unicode", "ÅNGSTRÖM Équipement", "Ångström équipement I İ ı i"),
            Entry("tab", "Tab", "door\tsensor")
        ];
        foreach (var query in new[] { "door sensor", "DOOR SENSOR", "door   sensor", " sensor door ", "door\tsensor", "ångström", "ÉQUIPEMENT", "I", "İ", "ı", "", " ", "\t" })
            AssertSame(entries, query);

        var shortResult = SearchService.Search(entries, "door sensor").Single(result => result.Target == "short");
        Require(shortResult.Subtitle == "Shared notes • subtitle-short • Door sensor replaced ✓", "Short snippets should normalize whitespace without ellipses.");
        var longResult = SearchService.Search(entries, "door sensor").Single(result => result.Target == "middle");
        Require(longResult.Subtitle.Contains("…") && longResult.Subtitle.Contains("DOOR SENSOR"), "Long phrase snippets must retain the surrounding match and ellipses.");
    }

    private static void StableTiesAndLimits()
    {
        var entries = Enumerable.Range(0, 160)
            .Select(index => Entry(index.ToString(), index % 2 == 0 ? "Same Title" : "SAME TITLE", "find"))
            .ToArray();
        foreach (var limit in new[] { -1, 0, 1, 3, 40, 159, 160, 161, int.MaxValue })
            AssertSame(entries, "find", limit);
        Require(SearchService.Search(entries, "find").Select(result => result.Target).SequenceEqual(Enumerable.Range(0, 40).Select(index => index.ToString())),
            "Equal score/title matches should preserve input order, including the boundary at 40.");
        AssertSame([], "find");
    }

    private static void RandomizedEquivalence()
    {
        var random = new Random(641);
        string[] words = ["door", "SENSOR", "gate", "repair", "Manual", "part", "42", "Ångström", "closed", "torque"];
        string[] separators = [" ", "  ", "\t", "\r\n", " • "];
        string RandomText(int count) => string.Join(separators[random.Next(separators.Length)], Enumerable.Range(0, count).Select(_ => words[random.Next(words.Length)]));

        for (var fixture = 0; fixture < 20; fixture++)
        {
            var entries = Enumerable.Range(0, 300).Select(index =>
            {
                var entry = Entry($"{fixture}-{index}", RandomText(random.Next(0, 5)), RandomText(random.Next(0, 20)), RandomText(random.Next(0, 35)));
                entry.Kind = index % 3 == 0 ? "Part" : "System";
                return entry;
            }).ToArray();
            for (var queryIndex = 0; queryIndex < 30; queryIndex++)
                AssertSame(entries, RandomText(random.Next(0, 4)), random.Next(0, 3) == 0 ? random.Next(0, 100) : 40);
        }
    }

    internal static void RunBenchmark()
    {
        var entries = Enumerable.Range(0, 16_000).Select(index => Entry(
            index.ToString(),
            index % 7 == 0 ? $"Gate {index:D5} door sensor" : $"Unit {index:D5}",
            $"Gate {index} model safe equipment manual drawing resource part {index % 53} repair torque sensor door maintenance",
            index % 3 == 0 ? $"Maintenance {index}: inspect the door and sensor. " + new string('x', 150) + $" Record repair {index % 53}, torque checks, gate reset and replacement parts." : ""))
            .ToArray();
        string[] queries = ["gate", "door sensor", "repair 42", "torque", "no such gate part", "sensor gate"];
        foreach (var query in queries) AssertSame(entries, query);

        // Warm both implementations, then report median synchronous search time
        // and thread allocations. These measurements are informative, not CI gates.
        for (var iteration = 0; iteration < 3; iteration++)
            foreach (var query in queries)
            {
                _ = BaselineSearch(entries, query, 40);
                _ = SearchService.Search(entries, query);
            }

        (double Milliseconds, long Bytes) Measure(bool optimized)
        {
            var allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var checksum = 0;
            foreach (var query in queries)
            {
                var results = optimized ? SearchService.Search(entries, query) : BaselineSearch(entries, query, 40);
                checksum += results.Count;
                foreach (var result in results) checksum += result.Title.Length;
            }
            var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            GC.KeepAlive(checksum);
            return (milliseconds, bytes);
        }

        var previous = new List<(double Milliseconds, long Bytes)>();
        var current = new List<(double Milliseconds, long Bytes)>();
        for (var iteration = 0; iteration < 7; iteration++)
        {
            // Alternate execution order to reduce systematic warmup/GC bias.
            if (iteration % 2 == 0) { previous.Add(Measure(false)); current.Add(Measure(true)); }
            else { current.Add(Measure(true)); previous.Add(Measure(false)); }
        }
        static double MedianTime(List<(double Milliseconds, long Bytes)> samples) => samples.Select(sample => sample.Milliseconds).Order().ElementAt(samples.Count / 2);
        static long MedianBytes(List<(double Milliseconds, long Bytes)> samples) => samples.Select(sample => sample.Bytes).Order().ElementAt(samples.Count / 2);
        Console.WriteLine($"Search benchmark: {entries.Length:N0} entries, {queries.Length} queries/sweep, median of 7 sweeps, identical ordered results verified.");
        Console.WriteLine($"  Previous: {MedianTime(previous):F2} ms/sweep, {MedianBytes(previous):N0} bytes/sweep");
        Console.WriteLine($"  Current:  {MedianTime(current):F2} ms/sweep, {MedianBytes(current):N0} bytes/sweep");
    }

    private static SearchIndexEntry Entry(string target, string title, string text, string notes = "") => new()
    {
        Title = title, Target = target, SearchText = text, NotesText = notes,
        Kind = "System", BaseSubtitle = "subtitle-" + target, SystemId = "system-" + target, Body = "body-" + target
    };

    private static void AssertSame(IReadOnlyList<SearchIndexEntry> entries, string query, int limit = 40)
    {
        var previous = BaselineSearch(entries, query, limit);
        var current = SearchService.Search(entries, query, limit);
        Require(previous.Count == current.Count, $"Result counts differ for query '{query}', limit {limit}.");
        for (var index = 0; index < previous.Count; index++)
        {
            var expected = previous[index];
            var actual = current[index];
            Require((expected.Title, expected.Subtitle, expected.Kind, expected.Target, expected.SystemId, expected.Body, expected.MatchSource, expected.MatchText) ==
                (actual.Title, actual.Subtitle, actual.Kind, actual.Target, actual.SystemId, actual.Body, actual.MatchSource, actual.MatchText),
                $"Search result {index} differs for query '{query}', limit {limit}; expected target {expected.Target}, actual {actual.Target}.");
        }
    }

    // Independent copy of the original MainWindow search logic. Keep this
    // baseline unchanged when adjusting the production optimization.
    private static List<GlobalSearchResult> BaselineSearch(IReadOnlyList<SearchIndexEntry> entries, string query, int limit) =>
        entries.Select(entry => new { Entry = entry, Score = BaselineScore(entry, query) }).Where(item => item.Score >= 0)
            .OrderBy(item => item.Score).ThenBy(item => item.Entry.Title, StringComparer.OrdinalIgnoreCase).Take(limit)
            .Select(item => BaselineResult(item.Entry, query)).ToList();

    private static int BaselineScore(SearchIndexEntry entry, string query)
    {
        if (entry.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) return 5;
        if (!string.IsNullOrWhiteSpace(entry.NotesText) && BaselineMatches(entry.NotesText, query)) return 10;
        if (entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)) return 15;
        return BaselineMatches(entry.SearchText, query) ? 25 : -1;
    }

    private static bool BaselineMatches(string? value, string query)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length > 1 && tokens.All(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static GlobalSearchResult BaselineResult(SearchIndexEntry entry, string query)
    {
        var notesMatch = entry.Kind == "System" && !string.IsNullOrWhiteSpace(entry.NotesText) && BaselineMatches(entry.NotesText, query);
        return new GlobalSearchResult
        {
            Title = entry.Title, Subtitle = notesMatch ? $"Shared notes • {entry.BaseSubtitle} • {BaselineSnippet(entry.NotesText, query)}" : entry.BaseSubtitle,
            Kind = entry.Kind, Target = entry.Target, SystemId = entry.SystemId, Body = entry.Body,
            MatchSource = notesMatch ? "SharedNotes" : "", MatchText = notesMatch ? query : ""
        };
    }

    private static string BaselineSnippet(string text, string query)
    {
        var clean = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length <= 110) return clean;
        var index = clean.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = 0;
        var start = Math.Max(0, index - 35);
        var length = Math.Min(110, clean.Length - start);
        var snippet = clean.Substring(start, length);
        return (start > 0 ? "…" : "") + snippet + (start + length < clean.Length ? "…" : "");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
