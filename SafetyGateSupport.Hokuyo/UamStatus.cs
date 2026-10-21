using System.Text;

namespace SafetyGateSupport.Hokuyo;

/// <summary>Common LP status prefix found in the supplied native USB monitor decoders.
/// This first implementation still requires a real USB capture comparison.</summary>
public sealed record UamStatus(bool ErrorActive, string LastErrorCode, bool Lockout, int AreaNumber)
{
    public static UamStatus Parse(string data)
    {
        // Mode(1), area(2), error-active(1), last error(2), lockout(1).
        // Never interpret missing characters or future enum values as zero/healthy.
        if (data.Length < 13 || data[0] is not ('0' or '1') || data[3] is not ('0' or '1') || data[6] is not ('0' or '1'))
            throw new InvalidDataException("Unrecognized UAM monitor status prefix; scanner condition is not verified.");
        int area = UamWire.Hex(Encoding.ASCII.GetBytes(data.Substring(1, 2)));
        if (area > 128) throw new InvalidDataException("Unrecognized UAM area number.");
        _ = UamWire.Hex(Encoding.ASCII.GetBytes(data.Substring(4, 2)));
        return new UamStatus(data[3] == '1', data.Substring(4, 2), data[6] == '1', area);
    }
    public string Describe()
    {
        string text = "PRELIMINARY USB SNAPSHOT — awaiting comparison with Project Designer on the test scanner.\n\n";
        text += ErrorActive ? "Scanner error flag: ACTIVE.\n" : "Scanner error flag: not active in this snapshot. This does not establish that the scanner is functioning correctly.\n";
        text += "Lockout flag: " + (Lockout ? "ACTIVE" : "not active") + $"\nReported area number: {AreaNumber}\n";
        var error = UamErrorCatalog.Lookup(LastErrorCode);
        text += $"Last recorded error code: {LastErrorCode} — {error.Title}\n";
        text += ErrorActive ? "The last recorded code may help explain the active error; compare with the scanner display.\n" : "A stored code is not being classified as a current fault.\n";
        if (error.IsListed) text += error.Checks + "\n" + error.Source + "\n";
        return text + "\nProtective-field, interlock and output interpretation is not yet hardware-validated. An output being OFF alone would not prove a scanner fault. This is one reading, not continuous monitoring or a functional safety test.";
    }
}
