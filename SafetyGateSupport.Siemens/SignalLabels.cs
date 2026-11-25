using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SafetyGateSupport.Siemens;

/// <summary>
/// Names the Siemens configurator stores alongside the device values in configurator.conf:
/// each input pin's name, wire colour and description, and each state's name.
/// Read-only and best effort: anything missing falls back to "Input n".
/// </summary>
public sealed partial record SignalLabels(IReadOnlyList<SignalLabels.Input> Inputs, IReadOnlyDictionary<int, string> StateNames)
{
    /// <summary>The configurator's "upside down" mounting option: base at the top.</summary>
    public bool UpsideDown { get; init; }

    public sealed record Input(int Number, string Name, string WireCode, string Description)
    {
        public string WireName => WireNames.TryGetValue(WireCode, out var name) ? name.Name : WireCode.ToLowerInvariant();
        public (byte R, byte G, byte B)? WireColor => WireNames.TryGetValue(WireCode, out var name) ? name.Color : null;
        /// <summary>"Input 2 (brown)", or the configurator's own name when it was renamed.</summary>
        public string Label => (Name.Length == 0 || Name.Equals($"Input {Number}", StringComparison.OrdinalIgnoreCase) ? $"Input {Number}" : $"{Number} {Name}")
            + (WireCode.Length == 0 ? "" : $" ({WireName})");
    }

    // IEC 60757 wire colour codes used by the configurator's pin list.
    private static readonly Dictionary<string, (string Name, (byte, byte, byte) Color)> WireNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WH"] = ("white", (245, 245, 245)), ["BN"] = ("brown", (121, 85, 61)), ["GN"] = ("green", (46, 139, 87)),
        ["YE"] = ("yellow", (240, 200, 0)), ["GY"] = ("grey", (140, 140, 140)), ["PK"] = ("pink", (240, 140, 170)),
        ["BU"] = ("blue", (30, 100, 200)), ["RD"] = ("red", (210, 40, 40)), ["BK"] = ("black", (30, 30, 30)),
        ["VT"] = ("violet", (128, 0, 160)), ["OG"] = ("orange", (240, 130, 0)), ["GNYE"] = ("green-yellow", (120, 170, 40))
    };

    public static SignalLabels Default { get; } = new(Enumerable.Range(1, 6).Select(n => new Input(n, $"Input {n}", "", "")).ToArray(), new Dictionary<int, string>());

    /// <summary>The state's own name, unless it is one of the configurator's automatic names such as "State (Copy 2)".</summary>
    public string? NameFor(int state) => StateNames.TryGetValue(state, out var name) && !GenericName().IsMatch(name) ? name : null;

    public static SignalLabels Read(PreparedSignalUpload selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(selected.GetBytes(), false), ZipArchiveMode.Read);
            using var entry = zip.GetEntry("configurator.conf")!.Open();
            using var document = JsonDocument.Parse(entry, new JsonDocumentOptions { MaxDepth = 32 });
            return Read(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or NullReferenceException or FormatException) { return Default; }
    }

    internal static SignalLabels Read(JsonElement root)
    {
        var inputs = Default.Inputs.ToArray();
        if (Property(root, "Pins") is { ValueKind: JsonValueKind.Array } pins)
            foreach (var pin in pins.EnumerateArray())
            {
                if (pin.ValueKind != JsonValueKind.Object || Property(pin, "Number") is not { ValueKind: JsonValueKind.Number } number || !number.TryGetInt32(out int n) || n is < 1 or > 6) continue;
                inputs[n - 1] = new Input(n, Text(pin, "Name") ?? $"Input {n}", Text(pin, "Color") ?? "", Text(pin, "Description") ?? "");
            }
        var names = new Dictionary<int, string>();
        if (Property(root, "states") is { ValueKind: JsonValueKind.Array } states)
            foreach (var state in states.EnumerateArray())
            {
                if (state.ValueKind != JsonValueKind.Object || Property(state, "PinCombination") is not { ValueKind: JsonValueKind.Object } combination
                    || Property(combination, "state") is not { ValueKind: JsonValueKind.Array } bits || bits.GetArrayLength() != 6) continue;
                int index = 0, bit = 0;
                foreach (var value in bits.EnumerateArray()) { if (value.ValueKind == JsonValueKind.True) index |= 1 << bit; bit++; }
                if (Text(state, "Name") is { Length: > 0 } name) names.TryAdd(index, name.Trim());
            }
        return new SignalLabels(inputs, names) { UpsideDown = Property(root, "IsUpsideDown") is { ValueKind: JsonValueKind.True } };
    }

    // The configurator serialises auto-properties as "<Name>k__BackingField".
    private static JsonElement? Property(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) || parent.TryGetProperty($"<{name}>k__BackingField", out value) ? value : null;
    private static string? Text(JsonElement parent, string name) => Property(parent, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    [GeneratedRegex(@"^State( \(Copy( \d+)?\))*$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericName();
}
