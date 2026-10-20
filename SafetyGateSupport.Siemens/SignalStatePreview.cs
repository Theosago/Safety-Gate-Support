using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SafetyGateSupport.Siemens;

/// <summary>Displays stored expanded values; never sends commands or changes an archive.</summary>
public static class SignalStatePreview
{
    public static string Describe(PreparedSignalUpload selected, int state)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (state is < 0 or > 63) throw new ArgumentOutOfRangeException(nameof(state));
        using var stream = new MemoryStream(selected.GetBytes(), false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var entry = zip.GetEntry($"state_config_{state}.json")!.Open();
        using var document = JsonDocument.Parse(entry);
        var text = new StringBuilder();
        text.AppendLine($"State {state}: active inputs {string.Join(", ", Enumerable.Range(1, 6).Where(n => (state & (1 << (n - 1))) != 0))} (none if blank).");
        text.AppendLine("Stored segment order 1–9; verify physical orientation against the drawing. RGB values are device values.");
        int index = 0;
        foreach (var segment in document.RootElement.GetProperty("Segments").EnumerateArray())
        {
            int r = segment.GetProperty("r").GetInt32(), g = segment.GetProperty("g").GetInt32(), b = segment.GetProperty("b").GetInt32();
            int brightness = segment.GetProperty("brightness").GetInt32(), effect = segment.GetProperty("lightEffect").GetInt32();
            bool off = brightness == 0 || effect == 0 || (r == 0 && g == 0 && b == 0);
            string effectName = effect switch { 0 => "off", 10 => "continuous", 50 => "blink 1 Hz", 51 => "blink 2 Hz", 52 => "blink 3 Hz", 90 => "flash once", 91 => "flash twice", 92 => "flash three times", 130 => "rotate", _ => $"effect {effect}" };
            text.AppendLine($"Segment {++index}: {(off ? "off" : $"RGB {r}, {g}, {b}; brightness {brightness}; {effectName}")}");
        }
        var sound = document.RootElement.GetProperty("Sound");
        text.AppendLine($"Sound: mode {sound.GetProperty("mode")}, volume {sound.GetProperty("volume")} (stored codes; 0 = off).");
        text.AppendLine("Sound parameters: " + sound.GetRawText());
        text.AppendLine("This preview does not read input voltages, drive outputs, or prove physical operation. Compare only with a safely observed input state.");
        return text.ToString();
    }
}
