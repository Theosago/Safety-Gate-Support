using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;

namespace SafetyGateSupport.Siemens;

/// <summary>A diagnostic summary only; no device commands or regenerated configuration.</summary>
public sealed record SignalArchiveInfo(
    int SchemaVersion,
    int HardwareVariant,
    int SegmentCount,
    bool HasSound,
    int StateCount,
    IReadOnlyList<int> ActiveStates);

/// <summary>
/// Inspects the supported schema-5 .signal ZIP format without extracting files.
/// The caller's original archive bytes are never modified or regenerated.
/// </summary>
public static class SignalArchive
{
    public const int MaxArchiveBytes = 16 * 1024 * 1024;
    private const int MaxEntryBytes = 1024 * 1024;
    private const int MaxEntries = 128;
    private const int ExpectedStates = 64;
    private static readonly HashSet<int> LightEffects = [0, 10, 50, 51, 52, 90, 91, 92, 130];
    private static readonly HashSet<int> Volumes = [0, 5, 10, 15, 25, 50, 255];
    private static readonly string[] SoundNumbers =
        ["frequency01", "frequency02", "frequency03", "repeatCount", "repeatPause", "cyclePause", "holdTime"];

    public static SignalArchiveInfo Inspect(byte[] archiveBytes)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        if (archiveBytes.Length > MaxArchiveBytes)
            throw Invalid("The configuration archive exceeds the 16 MiB diagnostic limit.");
        CheckZipEnvelope(archiveBytes);

        using var source = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);
        if (archive.Entries.Count is 0 or > MaxEntries)
            throw Invalid("The archive has an unsupported number of entries.");

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (!IsExpectedName(entry.FullName))
                throw Invalid("The archive contains a path or an unexpected entry.");
            if (files.ContainsKey(entry.FullName))
                throw Invalid("The archive contains a duplicate entry.");
            if (entry.IsEncrypted)
                throw Invalid("Encrypted configuration entries are unsupported.");
            if ((entry.ExternalAttributes >> 16 & 0xF000) is 0xA000 or 0x4000)
                throw Invalid("Directory and symbolic-link entries are unsupported.");
            if (entry.Length is <= 0 or > MaxEntryBytes || entry.CompressedLength > MaxArchiveBytes)
                throw Invalid("A configuration entry exceeds the diagnostic size limit.");
            totalBytes += entry.Length;
            if (totalBytes > MaxArchiveBytes)
                throw Invalid("The expanded configuration exceeds the diagnostic size limit.");

            // Read exactly the declared length, then probe for overlong expansion.
            var bytes = new byte[checked((int)entry.Length)];
            using (var stream = entry.Open())
            {
                stream.ReadExactly(bytes);
                if (stream.ReadByte() != -1)
                    throw Invalid("The expanded ZIP entry length is inconsistent.");
            }
            if (Crc32(bytes) != entry.Crc32)
                throw Invalid("A configuration entry failed its ZIP CRC-32 integrity check.");
            files.Add(entry.FullName, bytes);
        }

        if (!files.TryGetValue("configurator.conf", out var configurator))
            throw Invalid("The archive is missing configurator.conf.");
        using var metadata = Parse(configurator);
        var root = Object(metadata.RootElement, "configurator.conf");
        int version = Integer(root, "version");
        if (version != 5)
            throw new NotSupportedException($"Configuration schema {version} is not supported; only schema 5 has been inspected.");
        var device = Object(Required(root, "device"), "device");
        int variant = Integer(device, "Variant");
        var expected = variant switch
        {
            20 => (Segments: 9, Sound: false),
            21 => (Segments: 9, Sound: true),
            50 => (Segments: 15, Sound: false),
            51 => (Segments: 15, Sound: true),
            _ => throw new NotSupportedException($"Hardware variant {variant} is not supported.")
        };
        int segmentCount = Integer(device, "SegmentCount");
        bool hasSound = Boolean(device, "HasSound");
        if (segmentCount != expected.Segments || hasSound != expected.Sound)
            throw Invalid("The hardware variant, segment count, and sound capability disagree.");
        if (Integer(root, "mode") is not (0 or 1 or 2 or 4))
            throw new NotSupportedException("The editor configuration mode is unsupported.");
        CheckEditorArray(root, "stages");
        CheckEditorArray(root, "states");
        if (files.Count != ExpectedStates + 1)
            throw Invalid("Schema 5 requires configurator.conf and exactly 64 expanded states.");

        var activeStates = new List<int>();
        for (int stateIndex = 0; stateIndex < ExpectedStates; stateIndex++)
        {
            if (!files.TryGetValue($"state_config_{stateIndex}.json", out var stateBytes))
                throw Invalid($"The archive is missing expanded state {stateIndex}.");
            using var document = Parse(stateBytes);
            var state = Object(document.RootElement, "expanded state");
            var segments = Required(state, "Segments");
            if (segments.ValueKind != JsonValueKind.Array || segments.GetArrayLength() != segmentCount)
                throw Invalid("An expanded state's segment count disagrees with its hardware variant.");
            bool active = false;
            foreach (var value in segments.EnumerateArray())
            {
                var segment = Object(value, "segment");
                int red = ByteValue(segment, "r");
                int green = ByteValue(segment, "g");
                int blue = ByteValue(segment, "b");
                int brightness = ByteValue(segment, "brightness");
                int effect = Integer(segment, "lightEffect");
                if (!LightEffects.Contains(effect))
                    throw new NotSupportedException($"Light effect {effect} is unsupported.");
                active |= brightness > 0 && effect != 0 && (red > 0 || green > 0 || blue > 0);
            }
            var sound = Object(Required(state, "Sound"), "Sound");
            int mode = Integer(sound, "mode");
            int volume = Integer(sound, "volume");
            if (mode is < 0 or > 6 || !Volumes.Contains(volume))
                throw new NotSupportedException("The sound mode or volume is unsupported.");
            foreach (string field in SoundNumbers)
                if (Integer(sound, field) < 0)
                    throw Invalid($"Sound field {field} cannot be negative.");
            bool audible = mode != 0 && volume != 0;
            if (audible && !hasSound)
                throw Invalid("An expanded state requests sound on a variant without sound capability.");
            if (active || audible)
                activeStates.Add(stateIndex);
        }

        return new SignalArchiveInfo(version, variant, segmentCount, hasSound, ExpectedStates,
            Array.AsReadOnly(activeStates.ToArray()));
    }

    private static bool IsExpectedName(string name)
    {
        if (name == "configurator.conf")
            return true;
        const string prefix = "state_config_";
        const string suffix = ".json";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        var index = name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length);
        return int.TryParse(index, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int number)
            && number is >= 0 and < ExpectedStates
            && name == $"state_config_{number}.json";
    }

    private static void CheckZipEnvelope(ReadOnlySpan<byte> bytes)
    {
        // Bound the EOCD count before ZipArchive allocates its entry collection.
        // Multi-disk and ZIP64 archives are unnecessary for these small configs.
        for (int offset = bytes.Length - 22; offset >= Math.Max(0, bytes.Length - 65557); offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]) != 0x06054B50)
                continue;
            var end = bytes[offset..];
            if (22 + BinaryPrimitives.ReadUInt16LittleEndian(end[20..]) != end.Length)
                continue;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
            if (BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(end[8..]) != count
                || count is 0 or > MaxEntries || (ulong)start + size != (ulong)offset)
                throw Invalid("The configuration ZIP directory is unsupported or malformed.");
            return;
        }
        throw Invalid("The file is not a supported configuration ZIP archive.");
    }

    private static JsonDocument Parse(byte[] bytes)
    {
        try
        {
            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            try { CheckDuplicateProperties(document.RootElement); }
            catch { document.Dispose(); throw; }
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("A configuration entry contains malformed JSON.", exception);
        }
    }

    private static void CheckDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Invalid("A JSON object contains duplicate property names.");
                CheckDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                CheckDuplicateProperties(item);
    }

    private static void CheckEditorArray(JsonElement root, string name)
    {
        var array = Required(root, name);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > ExpectedStates)
            throw Invalid($"Editor field {name} must be an array with at most 64 items.");
    }

    private static JsonElement Object(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        ? value : throw Invalid($"Configuration field {name} must be an object.");

    private static JsonElement Required(JsonElement parent, string name) => parent.TryGetProperty(name, out var value)
        ? value : throw Invalid($"A required configuration field is missing: {name}.");

    private static int Integer(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number : throw Invalid($"Configuration field {name} must be a 32-bit integer.");
    }

    private static int ByteValue(JsonElement parent, string name)
    {
        int value = Integer(parent, name);
        return value is >= 0 and <= 255 ? value : throw Invalid($"Configuration field {name} must be between 0 and 255.");
    }

    private static bool Boolean(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid($"Configuration field {name} must be a boolean.")
        };
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
        }
        return ~crc;
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
