using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SafetyGateSupport.Siemens;

namespace SafetyGateSupport.SiemensValidation;

internal static class SignalArchiveChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases =>
    [
        ("signal: schema-5 summary and active states", ValidSummary),
        ("signal: preserves caller's archive bytes", PreservesBytes),
        ("signal: all four known hardware variants", KnownHardware),
        ("signal: deflated ZIP is supported", DeflatedArchive),
        ("signal: unknown editor metadata is ignored", UnknownEditorMetadata),
        ("signal: missing configurator rejected", () => Reject(files => files.Remove("configurator.conf"))),
        ("signal: missing expanded state rejected", () => Reject(files => files.Remove("state_config_63.json"))),
        ("signal: extra expanded state rejected", () => Reject(files => files["state_config_64.json"] = "{}")),
        ("signal: unexpected entry rejected", () => Reject(files => files["unexpected.txt"] = "data")),
        ("signal: traversal path rejected", () => Reject(files => files["../configurator.conf"] = "{}")),
        ("signal: absolute path rejected", () => Reject(files => files["C:\\configurator.conf"] = "{}")),
        ("signal: duplicate ZIP entry rejected", DuplicateZipEntry),
        ("signal: noncanonical state name rejected", () => Reject(files => files["state_config_01.json"] = "{}")),
        ("signal: malformed JSON rejected", () => Reject(files => files["state_config_0.json"] = "{")),
        ("signal: duplicate JSON property rejected", DuplicateJsonProperty),
        ("signal: future schema explicitly unsupported", () => RejectMetadata(root => root["version"] = 6, unsupported: true)),
        ("signal: future hardware explicitly unsupported", () => RejectMetadata(root => root["device"]!["Variant"] = 99, unsupported: true)),
        ("signal: inconsistent hardware metadata rejected", () => RejectMetadata(root => root["device"]!["SegmentCount"] = 15)),
        ("signal: boolean type required", () => RejectMetadata(root => root["device"]!["HasSound"] = "true")),
        ("signal: missing schema field rejected", () => RejectMetadata(root => root.Remove("version"))),
        ("signal: incorrect segment count rejected", () => RejectState(root => root["Segments"]!.AsArray().RemoveAt(0))),
        ("signal: out-of-range color rejected", () => RejectState(root => root["Segments"]![0]!["r"] = 256)),
        ("signal: negative brightness rejected", () => RejectState(root => root["Segments"]![0]!["brightness"] = -1)),
        ("signal: fractional color rejected", () => RejectState(root => root["Segments"]![0]!["g"] = 0.5)),
        ("signal: missing sound field rejected", () => RejectState(root => root["Sound"]!.AsObject().Remove("holdTime"))),
        ("signal: negative sound numeric rejected", () => RejectState(root => root["Sound"]!["frequency01"] = -1)),
        ("signal: undocumented sound magnitude preserved", LargeSoundNumbers),
        ("signal: unknown light effect unsupported", () => RejectState(root => root["Segments"]![0]!["lightEffect"] = 999, unsupported: true)),
        ("signal: unknown sound mode unsupported", () => RejectState(root => root["Sound"]!["mode"] = 99, unsupported: true)),
        ("signal: sound request on silent variant rejected", AudibleSilentVariant),
        ("signal: oversized archive rejected", OversizedArchive),
        ("signal: oversized expanded entry rejected", OversizedEntry),
        ("signal: excessive ZIP count rejected", ExcessiveEntryCount),
        ("signal: encrypted ZIP entry rejected", EncryptedEntry),
        ("signal: CRC corruption rejected before JSON parsing", CorruptCrc),
        ("signal: truncated ZIP rejected", TruncatedArchive)
    ];

    private static void ValidSummary()
    {
        var result = SignalArchive.Inspect(Archive());
        Check(result.SchemaVersion == 5 && result.HardwareVariant == 21 && result.SegmentCount == 9
            && result.HasSound && result.StateCount == 64, "Incorrect archive summary.");
        Check(result.ActiveStates.SequenceEqual([7, 11]), "Expected a visible state and an audible-only state.");
    }

    private static void PreservesBytes()
    {
        var bytes = Archive();
        var original = bytes.ToArray();
        _ = SignalArchive.Inspect(bytes);
        Check(bytes.SequenceEqual(original), "Diagnostic inspection modified the caller's bytes.");
    }

    private static void KnownHardware()
    {
        foreach (var (variant, segments, sound) in new[] { (20, 9, false), (21, 9, true), (50, 15, false), (51, 15, true) })
        {
            var result = SignalArchive.Inspect(Zip(Files(variant, segments, sound)));
            Check(result.HardwareVariant == variant && result.SegmentCount == segments && result.HasSound == sound,
                "Known hardware capabilities were incorrectly summarized.");
        }
    }

    private static void DeflatedArchive() => Check(SignalArchive.Inspect(Zip(Files(), CompressionLevel.Optimal)).StateCount == 64,
        "A normal deflated archive was rejected.");

    private static void UnknownEditorMetadata()
    {
        var bytes = Archive(files => Change(files, "configurator.conf", root => root["additional_editor_metadata"] = "unchanged"));
        Check(SignalArchive.Inspect(bytes).StateCount == 64, "Irrelevant editor metadata should not prevent diagnostics.");
    }

    private static void DuplicateZipEntry()
    {
        var files = Files().ToList();
        files.Add(files.First());
        Throws<InvalidDataException>(() => SignalArchive.Inspect(Zip(files)));
    }

    private static void DuplicateJsonProperty() => Reject(files =>
        files["configurator.conf"] = files["configurator.conf"].Replace("\"version\":5", "\"version\":5,\"version\":5", StringComparison.Ordinal));

    private static void LargeSoundNumbers()
    {
        var bytes = Archive(files => Change(files, "state_config_0.json", root => root["Sound"]!["holdTime"] = int.MaxValue));
        Check(SignalArchive.Inspect(bytes).StateCount == 64, "Diagnostics invented an undocumented sound upper bound.");
    }

    private static void AudibleSilentVariant()
    {
        var files = Files(20, 9, false);
        Change(files, "state_config_0.json", root => { root["Sound"]!["mode"] = 1; root["Sound"]!["volume"] = 5; });
        Throws<InvalidDataException>(() => SignalArchive.Inspect(Zip(files)));
    }

    private static void OversizedArchive() => Throws<InvalidDataException>(() =>
        SignalArchive.Inspect(new byte[SignalArchive.MaxArchiveBytes + 1]));

    private static void OversizedEntry() => Reject(files => files["configurator.conf"] = new string(' ', 1024 * 1024 + 1));

    private static void ExcessiveEntryCount()
    {
        var files = Enumerable.Range(0, 129).Select(i => new KeyValuePair<string, string>($"file{i}", "data"));
        Throws<InvalidDataException>(() => SignalArchive.Inspect(Zip(files)));
    }

    private static void EncryptedEntry()
    {
        var bytes = Archive();
        // Set the encryption bit in the first local and first central headers.
        int central = FindCentralHeader(bytes);
        bytes[6] |= 1;
        bytes[central + 8] |= 1;
        Throws<InvalidDataException>(() => SignalArchive.Inspect(bytes));
    }

    private static void CorruptCrc()
    {
        var bytes = Archive();
        int central = FindCentralHeader(bytes);
        bytes[central + 16] ^= 1;
        var exception = Throws<InvalidDataException>(() => SignalArchive.Inspect(bytes));
        Check(exception.Message.Contains("CRC", StringComparison.OrdinalIgnoreCase),
            "Corruption should be detected by the independent CRC check on otherwise valid JSON.");
    }

    private static int FindCentralHeader(byte[] bytes)
    {
        // Archives generated here have no ZIP comment; use EOCD's directory offset.
        return checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6, 4)));
    }

    private static void TruncatedArchive()
    {
        var bytes = Archive();
        Throws<InvalidDataException>(() => SignalArchive.Inspect(bytes[..^8]));
    }

    private static void Reject(Action<Dictionary<string, string>> mutation) =>
        Throws<InvalidDataException>(() => SignalArchive.Inspect(Archive(mutation)));

    private static void RejectMetadata(Action<JsonObject> mutation, bool unsupported = false) =>
        RejectJson("configurator.conf", mutation, unsupported);

    private static void RejectState(Action<JsonObject> mutation, bool unsupported = false) =>
        RejectJson("state_config_0.json", mutation, unsupported);

    private static void RejectJson(string name, Action<JsonObject> mutation, bool unsupported)
    {
        var bytes = Archive(files => Change(files, name, mutation));
        if (unsupported) Throws<NotSupportedException>(() => SignalArchive.Inspect(bytes));
        else Throws<InvalidDataException>(() => SignalArchive.Inspect(bytes));
    }

    private static void Change(Dictionary<string, string> files, string name, Action<JsonObject> mutation)
    {
        var root = JsonNode.Parse(files[name])!.AsObject();
        mutation(root);
        files[name] = root.ToJsonString();
    }

    private static byte[] Archive(Action<Dictionary<string, string>>? mutation = null)
    {
        var files = Files();
        mutation?.Invoke(files);
        return Zip(files);
    }

    private static Dictionary<string, string> Files(int variant = 21, int segments = 9, bool sound = true)
    {
        var files = new Dictionary<string, string>
        {
            ["configurator.conf"] = JsonSerializer.Serialize(new
            {
                version = 5,
                device = new { Variant = variant, SegmentCount = segments, HasSound = sound },
                mode = 4,
                stages = Array.Empty<object>(),
                states = Array.Empty<object>()
            })
        };
        for (int state = 0; state < 64; state++)
        {
            bool audible = sound && state == 11;
            files[$"state_config_{state}.json"] = JsonSerializer.Serialize(new
            {
                Segments = Enumerable.Range(0, segments).Select(segment => new
                {
                    r = state == 7 && segment == 0 ? 255 : 0,
                    g = 0,
                    b = 0,
                    brightness = state == 7 && segment == 0 ? 255 : 0,
                    lightEffect = 10
                }).ToArray(),
                Sound = new
                {
                    mode = audible ? 1 : 0,
                    volume = audible ? 5 : 0,
                    frequency01 = audible ? 1000 : 0,
                    frequency02 = 0,
                    frequency03 = 0,
                    repeatCount = 0,
                    repeatPause = 0,
                    cyclePause = 0,
                    holdTime = 0
                }
            });
        }
        return files;
    }

    private static byte[] Zip(IEnumerable<KeyValuePair<string, string>> files, CompressionLevel compression = CompressionLevel.NoCompression)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name, compression);
                using var output = entry.Open();
                output.Write(Encoding.UTF8.GetBytes(content));
            }
        return stream.ToArray();
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
