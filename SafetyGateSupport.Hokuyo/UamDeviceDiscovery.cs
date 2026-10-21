namespace SafetyGateSupport.Hokuyo;

/// <summary>Passive discovery evidence. Identity still has to be read from the selected device.</summary>
public sealed record UamSerialDevice(string PortName, string InstanceId, string UsbInstanceId, string Description, string Manufacturer);

public static class UamDeviceDiscovery
{
    public static IReadOnlyList<UamUsbPort> FindCandidates(IEnumerable<UamSerialDevice> devices)
    {
        return devices.Where(d => IsComPort(d.PortName) && (IsUsbDevice(d.UsbInstanceId) || HasHokuyoName(d)))
            .GroupBy(d => d.PortName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(IsHokuyo).ThenBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase).First())
            .OrderByDescending(IsHokuyo).ThenBy(d => d.PortName, StringComparer.OrdinalIgnoreCase)
            .Select(d => new UamUsbPort(d.PortName, d.InstanceId,
                $"{(IsHokuyo(d) ? "Hokuyo USB candidate" : "USB serial device — scanner identity not checked")}: {d.Description} [{d.PortName}]")
                { IsRecognizedCandidate = IsHokuyo(d) }).ToArray();
    }

    public static UamUsbPort? AutoSelect(IReadOnlyList<UamUsbPort> devices, UamUsbPort? previous)
    {
        // Preserve a selection only while the exact device remains present.
        var retained = devices.FirstOrDefault(d => d.PortName.Equals(previous?.PortName, StringComparison.OrdinalIgnoreCase) &&
            d.DeviceInstanceId.Equals(previous?.DeviceInstanceId, StringComparison.OrdinalIgnoreCase));
        if (retained is not null) return retained;
        var recognized = devices.Where(d => d.IsRecognizedCandidate).ToArray();
        if (recognized.Length == 1) return recognized[0];
        return devices.Count == 1 ? devices[0] : null;
    }

    private static bool IsComPort(string port) => port.Length > 3 && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
        port[3] is >= '1' and <= '9' && port.AsSpan(3).IndexOfAnyExceptInRange('0', '9') < 0;
    private static bool IsUsbDevice(string instance) => instance.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase);
    private static bool IsHokuyo(UamSerialDevice device)
    {
        string usb = IsUsbDevice(device.UsbInstanceId) ? device.UsbInstanceId.Split('\\')[1] : "";
        var tokens = usb.Split('&');
        bool knownId = (tokens.Contains("VID_143F", StringComparer.OrdinalIgnoreCase) && tokens.Contains("PID_0021", StringComparer.OrdinalIgnoreCase)) ||
            (tokens.Contains("VID_15D1", StringComparer.OrdinalIgnoreCase) && tokens.Contains("PID_0000", StringComparer.OrdinalIgnoreCase));
        return knownId || HasHokuyoName(device);
    }
    private static bool HasHokuyoName(UamSerialDevice device)
    {
        string label = device.Description + " " + device.Manufacturer;
        return label.Contains("HOKUYO", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("UAM-", StringComparison.OrdinalIgnoreCase) || label.Contains("SLS USB", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("URG Series", StringComparison.OrdinalIgnoreCase);
    }
}
