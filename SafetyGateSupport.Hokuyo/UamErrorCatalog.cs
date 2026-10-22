using System.Globalization;

namespace SafetyGateSupport.Hokuyo;

public sealed record UamErrorInfo(string Code, string Title, string Checks, string Source, bool IsListed);

/// <summary>UAM-05LP-T301 display codes, paraphrased from the supplied manual. Not a health classifier.</summary>
public static class UamErrorCatalog
{
    public const string Source = "UAM User's Manual C-61-00003-12, Table 9-2, printed pages 138–141";
    public static UamErrorInfo Lookup(string code)
    {
        string text = code.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (text.Length != 2 || !byte.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte number))
            throw new FormatException("Enter the two hexadecimal characters shown on the scanner, for example 57, 70, or b1. Codes are not decimal numbers.");
        string normalized = number.ToString("X2", CultureInfo.InvariantCulture);
        if (number == 0x75)
            return new(normalized, "Internal system error", "UAM Project Designer calls for manufacturer intervention. Preserve the error details and contact Hokuyo or the authorized distributor. A later clear reading under different conditions does not establish that an intermittent fault is resolved.", Source + "; error 75 wording confirmed by UAM Project Designer 1.1.17 Monitor details", true);
        var item = number switch
        {
            0x45 => ("Setting error / incomplete setting", "Have qualified personnel review the approved configuration and its completeness."),
            0x56 => ("Invalid area", "Check the actual area-selection input states against the unit drawing and approved area assignments."),
            0x57 => ("Area-input connection error", "Check the area-selection wiring and input states against the unit drawing."),
            0x59 => ("Area-sequence error", "Check the actual area-switching sequence against the approved sequence."),
            0x5B or 0x5D or 0x5F => ("Encoder speed error", "Check encoder speed and the approved speed configuration; do not change limits simply to clear the fault."),
            0x5C => ("Encoder error", "Check the encoder input signals and wiring."),
            0x5E => ("Invalid area while encoder is enabled", "Check area-selection inputs during switching with the encoder enabled."),
            >= 0x60 and <= 0x63 => ("Motor error", "Check mounting, vibration and impacts against the scanner's specified limits."),
            0x64 => ("Master/slave communication error", "Check the connections between the master and slave units."),
            0x70 => ("Optical interference / highly reflective background", "Inspect for interfering light and reflective background; follow the manual's optical-interference guidance."),
            0x72 => ("Operating-temperature error", "Check scanner temperature and its environment against the specified operating limits."),
            0x74 => ("Power-supply error", "Have qualified personnel check the supply voltage and connections against the specified limits."),
            0x7C => ("OSSD excess-current error", "Check the OSSD load and connections against their rated limits."),
            0x7D or 0x7E or 0x7F => ($"Slave unit {number - 0x7C} error", "Read the error code on the indicated slave unit and diagnose that unit."),
            0x80 => ("Master-unit error", "Read the error code on the master unit and diagnose that unit."),
            0x84 or 0x85 => ("Object in limited-detection zone / optical-window contamination", "Inspect the limited-detection zone for objects and the optical window for contamination. Follow the manual's cleaning procedure."),
            0x86 => ("Mutual-interference error", "Inspect for interference from other scanners and follow the manual's countermeasures."),
            0x8F => ("SD-card detection error", "Review the SD-card specification and its files using the documented procedure."),
            >= 0x95 and <= 0x9A or 0xA8 or 0xAC => ("OSSD diagnostic error", "Have qualified personnel check the OSSD connections; do not bypass the outputs."),
            0xA6 => ("EDM1 input-connection error", "Check the EDM1 connection against the approved wiring diagram."),
            0xA7 => ("EDM2 input-connection error", "Check the EDM2 connection against the approved wiring diagram."),
            0xA9 => ("Reset-input error", "Check the RESET input and its wiring. This lookup does not reset the scanner."),
            0xAA => ("Reference-monitor error", "Inspect for displacement of the reference structure or scanner."),
            >= 0xB1 and <= 0xC0 => ("Optical-window contamination", "Inspect and clean the optical window using the manual's cleaning procedure."),
            0xC1 => ("Optical window removed or contaminated", "Inspect the optical window and follow the documented replacement/cleaning procedure."),
            0xCE => ("Optical-window adjustment incomplete", "The manual also describes CE during adjustment. Confirm the replacement/adjustment state using the documented procedure."),
            0xCF => ("Scanner not configured", "Confirm and apply the approved scanner configuration using the vendor's documented procedure."),
            >= 0xD3 and <= 0xE6 => ("Incomplete configuration", "Have qualified personnel review the approved configuration and its completeness."),
            0xF0 => ("SD-card initialization error", "Follow the documented card-removal/retry procedure under the required service conditions."),
            0xF1 => ("SD-card file-content error", "Review the setting file on the SD card."),
            _ => ("", "")
        };
        if (item.Item1.Length > 0) return new(normalized, item.Item1, item.Item2, Source, true);
        if (number is >= 0x41 and <= 0xF5)
            return new(normalized, "Device error — no individual entry in this manual's table", "The manual groups other 41–F5 errors as device errors. Review grounding, interference, mounting and the full manual; contact Hokuyo if the fault persists. Do not infer a specific cause from this code alone.", Source, true);
        return new(normalized, "Code not covered by this error table", "This value does not establish normal operation. Check the exact model, display and manual revision. Active status and a past error number are different observations.", Source, false);
    }
}
