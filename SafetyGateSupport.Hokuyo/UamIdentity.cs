namespace SafetyGateSupport.Hokuyo;

/// <summary>Identity fields are comma-separated and space-padded on the real USB interface.
/// The scanner reports UAM-05LP rather than the complete label/suffix UAM-05LP-T301.</summary>
public sealed record UamIdentity(string Family, string Firmware, string ReportedIdentifier)
{
    public static UamIdentity Parse(string data)
    {
        string[] fields = data.Split(',').Select(s => s.Trim()).ToArray();
        // Match the first product field exactly. A suffix, a substring, or a mention
        // elsewhere in an unrelated identity must never authorize a status query.
        if (fields.Length < 2 || fields[0] is not ("UAM-05LP" or "UAM-05LP-T301"))
            throw new InvalidDataException("USB identity does not identify the supported UAM-05LP family. Status read withheld; keep the report.");
        string firmware = fields[1];
        if (firmware.Length == 0 || firmware.Length > 32 || !firmware.All(c => char.IsAsciiLetterOrDigit(c) || c == '.'))
            throw new InvalidDataException("USB identity has an unrecognized firmware field. Status read withheld; keep the report.");
        return new UamIdentity(fields[0], firmware, fields.Length > 5 ? fields[5] : "not reported");
    }
    public string Describe() => $"USB identity: {Family}\nFirmware: {Firmware}\nReported unit identifier: {ReportedIdentifier}\n" +
        (Family == "UAM-05LP" ? "USB reports the family name; confirm the physical label is UAM-05LP-T301.\n\n" : "\n");
}
