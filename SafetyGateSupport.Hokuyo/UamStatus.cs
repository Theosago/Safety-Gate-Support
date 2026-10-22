using System.Globalization;
using System.Text;

namespace SafetyGateSupport.Hokuyo;

/// <summary>Common LP monitor prefix. Wire error numbers are offset from display codes.</summary>
public sealed record UamStatus(bool ErrorActive, string? LastErrorCode, bool Lockout, int AreaNumber)
{
    public string RawLastErrorCode { get; init; } = "";

    public static UamStatus Parse(string data)
    {
        // Mode(1), area(2 hex), error-active(1), raw last error(2 hex), lockout(1).
        // Never interpret missing characters or future enum values as zero/healthy.
        if (data.Length < 13 || data[0] is not ('0' or '1') || data[3] is not ('0' or '1') || data[6] is not ('0' or '1'))
            throw new InvalidDataException("Unrecognized UAM monitor status prefix; scanner condition is not verified.");
        int area = UamWire.Hex(Encoding.ASCII.GetBytes(data.Substring(1, 2)));
        if (area > 128) throw new InvalidDataException("Unrecognized UAM area number.");
        string raw = data.Substring(4, 2);
        int rawError = UamWire.Hex(Encoding.ASCII.GetBytes(raw));
        if (rawError >= 0xC0) throw new InvalidDataException("Unrecognized UAM wire error number; scanner condition is not verified.");
        // Native SafetyProtocol::fetchMonitorErrorNumber adds 0x40. The observed
        // obstruction capture carries raw 0x45 and the front display shows 0x85.
        string? display = rawError == 0 && data[3] == '0' ? null : (rawError + 0x40).ToString("X2", CultureInfo.InvariantCulture);
        return new UamStatus(data[3] == '1', display, data[6] == '1', area) { RawLastErrorCode = raw };
    }

    public string Describe()
    {
        string text = "USB STATUS SNAPSHOT — one reading, not continuous monitoring.\n\n";
        text += ErrorActive ? "Scanner reports an ACTIVE error.\n" : "No active fault reported in this snapshot. This does not establish that the scanner is functioning correctly.\n";
        text += "Lockout flag: " + (Lockout ? "ACTIVE" : "not active") + $"\nReported area value: {AreaNumber}\n";
        if (LastErrorCode is null)
            text += "No display error code identified in this snapshot. Error history has not been checked.\n";
        else
        {
            var error = UamErrorCatalog.Lookup(LastErrorCode);
            text += $"{(ErrorActive ? "Reported display error" : "Last reported display error")}: {LastErrorCode} — {error.Title}\n";
            text += ErrorActive ? "The reported code accompanies the active-error flag; compare with the scanner display.\n" : "This stored code is not being classified as a current fault.\n";
            if (error.IsListed) text += error.Checks + "\n" + error.Source + "\n";
        }
        return text + "\nProtective-field, interlock and output interpretation is not yet hardware-validated. An output being OFF alone would not prove a scanner fault. This reading does not replace the approved functional test or resolve an intermittent fault observed under different connections or operating conditions.";
    }
}
