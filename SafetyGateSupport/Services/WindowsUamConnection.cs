using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Runtime.Versioning;

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

    private readonly SerialPort _serialPort;
    private readonly CancellationToken _sessionCancellation;
    private bool _disposed;

    private WindowsUamConnection(SerialPort serialPort, CancellationToken sessionCancellation)
    {
        _serialPort = serialPort;
        _sessionCancellation = sessionCancellation;
    }

    /// <summary>Lists present USB serial endpoints without opening any COM port.</summary>
    public static IReadOnlyList<UamUsbPort> DetectPorts() =>
        UamDeviceDiscovery.FindCandidates(WindowsUsbSerialDiscovery.Enumerate());

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
            throw new IOException("The selected USB device is no longer present. Refresh the device list.");

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

}
