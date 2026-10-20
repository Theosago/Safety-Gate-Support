using System.IO;
using SafetyGateSupport.Siemens;

namespace SiemensUsbCheck.Services;

public static class UsbUploadSession
{
    public static SignalUploadResult Run(UsbColumnPort port, PreparedSignalUpload selected, string reportPath,
        CancellationToken token, Action<string> progress) => SignalUploadSession.Run(selected,
            new SiemensUploadTarget(port.PortName, port.DeviceInstanceId), reportPath, Factory(port), token, progress);

    // A normal restart can change the COM name. Reopen only the original USB
    // instance, then the session verifies the actual hardware MAC before readback.
    private static Stream OpenSameInstance(UsbColumnPort port, CancellationToken token)
    {
        var current = WindowsColumnConnection.DetectPorts().SingleOrDefault(candidate =>
            string.Equals(candidate.DeviceInstanceId, port.DeviceInstanceId, StringComparison.OrdinalIgnoreCase));
        if (current is null) throw new IOException("The selected column has not reconnected yet.");
        return WindowsColumnConnection.Open(current, token);
    }

    private static Func<CancellationToken, Stream> Factory(UsbColumnPort port) => token => OpenSameInstance(port, token);
}
