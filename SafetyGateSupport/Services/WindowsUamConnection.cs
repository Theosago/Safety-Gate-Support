using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

using SafetyGateSupport.Hokuyo;

namespace SafetyGateSupport.Services;

/// <summary>
/// A serial transport for the SLS USB serial interface. This class never
/// constructs, retries, or sends a device command on its own. The caller must
/// serialize access and supply only the read-only protocol requests it allows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUamConnection : Stream
{
    private const int SerialReadTimeoutMilliseconds = 500;
    private const int InactivityTimeoutMilliseconds = 10_000;
    private const int SerialWriteTimeoutMilliseconds = 10_000;
    private const uint ConfigurationManagerSuccess = 0;
    private const uint LocatePresentDevice = 0;

    private readonly SerialPort _serialPort;
    private readonly CancellationToken _sessionCancellation;
    private bool _disposed;

    private WindowsUamConnection(SerialPort serialPort, CancellationToken sessionCancellation)
    {
        _serialPort = serialPort;
        _sessionCancellation = sessionCancellation;
    }

    /// <summary>
    /// Finds present normal-mode USB devices only. Windows retains registry
    /// entries after unplugging hardware, so both a live PnP node and a currently
    /// enumerated COM port are required. Other Hokuyo/URG USB identifiers are not selected.
    /// </summary>
    public static IReadOnlyList<UamUsbPort> DetectPorts()
    {
        var actualPorts = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        var ports = new List<UamUsbPort>();
        var seenInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is null || actualPorts.Count == 0)
            return ports;

        foreach (var deviceKeyName in usb.GetSubKeyNames())
        {
            // Token matching also permits the composite-device &MI_XX suffix.
            var identifiers = deviceKeyName.Split('&');
            if (!identifiers.Contains("VID_143F", StringComparer.OrdinalIgnoreCase) ||
                !identifiers.Contains("PID_0021", StringComparer.OrdinalIgnoreCase))
                continue;

            try
            {
                using var device = usb.OpenSubKey(deviceKeyName);
                if (device is null)
                    continue;

                foreach (var instanceKeyName in device.GetSubKeyNames())
                {
                    var instanceId = $@"USB\{deviceKeyName}\{instanceKeyName}";
                    if (!IsPresentDevice(instanceId))
                        continue;

                    using var instance = device.OpenSubKey(instanceKeyName);
                    using var parameters = instance?.OpenSubKey("Device Parameters");
                    var portName = parameters?.GetValue("PortName") as string;
                    if (string.IsNullOrWhiteSpace(portName) || !actualPorts.Contains(portName) ||
                        !seenInstances.Add(instanceId))
                        continue;

                    var friendlyName = instance?.GetValue("FriendlyName") as string;
                    var description = instance?.GetValue("DeviceDesc") as string;
                    var label = ReadableDeviceDescription(friendlyName, description);
                    ports.Add(new UamUsbPort(portName, instanceId,
                        $"{label} [{portName}; {instanceKeyName}]"));
                }
            }
            catch (IOException)
            {
                // A device can disappear between enumerating and opening its
                // registry key. The next explicit refresh can rediscover it.
            }
        }

        return ports.OrderBy(port => port.PortName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(port => port.DeviceInstanceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Revalidates the exact USB instance before opening. Opening a Windows COM
    /// port can toggle DTR/RTS and can reset USB firmware; this is not a hardware
    /// guarantee that a read-only command has no electrical side effects.
    /// </summary>
    public static WindowsUamConnection Open(UamUsbPort port, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(port);
        token.ThrowIfCancellationRequested();

        var current = DetectPorts().FirstOrDefault(candidate =>
            string.Equals(candidate.PortName, port.PortName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.DeviceInstanceId, port.DeviceInstanceId, StringComparison.OrdinalIgnoreCase));
        if (current is null)
            throw new IOException("The selected Hokuyo SLS USB device is no longer present. Refresh the device list.");

        token.ThrowIfCancellationRequested();
        var serialPort = new SerialPort(current.PortName, 115_200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = SerialReadTimeoutMilliseconds,
            WriteTimeout = SerialWriteTimeoutMilliseconds
        };

        try
        {
            serialPort.Open();
            token.ThrowIfCancellationRequested();
            return new WindowsUamConnection(serialPort, token);
        }
        catch
        {
            serialPort.Dispose();
            throw;
        }
    }

    public override bool CanRead => !_disposed && _serialPort.IsOpen;
    public override bool CanWrite => !_disposed && _serialPort.IsOpen;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        ThrowIfUnavailable();
        if (count == 0)
            return 0;

        // Returning any bytes ends this wait. A subsequent Read starts a fresh
        // inactivity interval, so a slow but progressing response can complete.
        var inactivity = Stopwatch.StartNew();
        while (true)
        {
            _sessionCancellation.ThrowIfCancellationRequested();
            try
            {
                var received = _serialPort.Read(buffer, offset, count);
                _sessionCancellation.ThrowIfCancellationRequested();
                if (received > 0)
                    return received;

                throw new EndOfStreamException("The USB serial stream ended before more data was received.");
            }
            catch (TimeoutException error)
            {
                _sessionCancellation.ThrowIfCancellationRequested();
                if (inactivity.ElapsedMilliseconds >= InactivityTimeoutMilliseconds)
                    throw new TimeoutException("The scanner sent no data for 10 seconds.", error);
            }
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        ThrowIfUnavailable();
        if (count == 0)
            return;

        // SerialPort.Write is synchronous and bounded by its driver timeout.
        // Cancellation cannot preempt an OS driver call; check again afterward.
        _serialPort.Write(buffer, offset, count);
        _sessionCancellation.ThrowIfCancellationRequested();
    }

    public override void Flush()
    {
        ThrowIfUnavailable();
        // There is no additional managed output buffer in this transport.
        // In particular, do not discard incoming device data when flushing.
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
                _serialPort.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sessionCancellation.ThrowIfCancellationRequested();
        if (!_serialPort.IsOpen)
            throw new IOException("The USB serial port is closed.");
    }

    private static string ReadableDeviceDescription(string? friendlyName, string? description)
    {
        if (!string.IsNullOrWhiteSpace(friendlyName))
            return friendlyName;
        if (!string.IsNullOrWhiteSpace(description))
        {
            // DeviceDesc can be an indirect resource string ending in ;label.
            var separator = description.LastIndexOf(';');
            return separator >= 0 && separator < description.Length - 1
                ? description[(separator + 1)..]
                : description;
        }
        return "Siemens 8WD46 USB scanner";
    }

    private static bool IsPresentDevice(string instanceId) =>
        CM_Locate_DevNodeW(out _, instanceId, LocatePresentDevice) == ConfigurationManagerSuccess;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Locate_DevNodeW(out uint deviceInstance, string deviceId, uint flags);
}
