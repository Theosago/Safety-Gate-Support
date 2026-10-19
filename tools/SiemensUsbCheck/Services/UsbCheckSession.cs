using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SafetyGateSupport.Siemens;

namespace SiemensUsbCheck.Services;

public sealed record UsbCheckResult(bool Success, string Summary);

/// <summary>One diagnostic session. Only the two fixed GET requests are available.</summary>
public static class UsbCheckSession
{
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };

    public static UsbCheckResult Run(UsbColumnPort port, string reportPath, CancellationToken token,
        Action<string> progress) => Execute(port, reportPath, token, progress,
            sessionToken => WindowsColumnConnection.Open(port, sessionToken), disposeStream: true);

    /// <summary>Runs the identical diagnostic flow against a supplied stream for regression tests.</summary>
    public static UsbCheckResult RunOnStream(Stream stream, UsbColumnPort port, string reportPath,
        CancellationToken token = default, Action<string>? progress = null) =>
        Execute(port, reportPath, token, progress ?? (_ => { }), _ => stream, disposeStream: false);

    private static UsbCheckResult Execute(UsbColumnPort port, string reportPath, CancellationToken token,
        Action<string> progress, Func<CancellationToken, Stream> connect, bool disposeStream)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var sessionToken = deadline.Token;
        var requests = new List<string>();
        var responses = new List<object>();
        var started = DateTimeOffset.UtcNow;
        byte[]? infoBytes = null, configBytes = null;
        byte[] receivedPrefix = [];
        SiemensDeviceInfo? device = null;
        SignalArchiveInfo? archive = null;
        string? failure = null;
        bool success = false;
        string summary = "The check could not be completed.";

        try
        {
            sessionToken.ThrowIfCancellationRequested();
            progress("Opening the selected USB column…");
            using var recording = new RecordingStream(connect(sessionToken), disposeStream);
            try
            {
                var infoResponse = Exchange(recording, "GET /api/v1/info", SiemensWire.GetInfoRequest(), 64 * 1024);
                infoBytes = infoResponse.Body;
                RequireSuccess(infoResponse, "Device information");
                device = SiemensDeviceInfo.Parse(infoBytes);
                if (!device.IsSupportedTestColumn)
                    throw new NotSupportedException($"The device reports {device.Model}, hardware variant {device.HardwareVariant}. This check supports only 8WD4613-5JH37 / variant 21. Configuration was not requested.");

                // The vendor reads configuration regardless of firstConfig;
                // retain that flag as evidence without assigning new semantics.
                var configResponse = Exchange(recording, "GET /api/v1/config", SiemensWire.GetConfigRequest(), SignalArchive.MaxArchiveBytes);
                configBytes = configResponse.Body;
                RequireSuccess(configResponse, "Configuration backup");
                archive = SignalArchive.Inspect(configBytes);
                if (archive.HardwareVariant != device.HardwareVariant)
                    throw new InvalidDataException("The backup hardware variant differs from the device identity.");
                success = true;
                summary = $"Identified {device.Model}. Saved its configuration backup: {archive.SegmentCount} segments, {archive.StateCount} states.";
            }
            finally
            {
                receivedPrefix = recording.ReceivedPrefix;
            }
        }
        catch (OperationCanceledException)
        {
            summary = token.IsCancellationRequested ? "The check was cancelled. A partial diagnostic report was saved." :
                "The check reached its 60-second limit. A partial diagnostic report was saved.";
            failure = summary;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or NotSupportedException or TimeoutException or JsonException or System.Security.SecurityException)
        {
            failure = $"{error.GetType().Name}: {error.Message}";
            summary = $"The check stopped: {error.Message}";
        }

        progress("Saving the diagnostic report…");
        WriteReport(reportPath, new
        {
            tool = "SiemensUsbCheck", version = "0.1.1",
            startedUtc = started, finishedUtc = DateTimeOffset.UtcNow,
            success, summary, error = failure,
            devicePort = port, usbVendorId = "303A", usbProductId = "80F7",
            serial = new { baudRate = 576000, dataBits = 8, parity = "None", stopBits = 1, dtr = true, rts = true },
            requests, responses, device, archive,
            operation = "Identity and backup GET requests only; no configuration, reset, or firmware requests.",
            hardwareValidation = "Unverified independent client; opening the COM port can toggle DTR/RTS and restart firmware."
        }, infoBytes, configBytes, receivedPrefix, success);
        return new UsbCheckResult(success, summary);

        SiemensHttpResponse Exchange(Stream stream, string requestName, byte[] request, int maximumBody)
        {
            sessionToken.ThrowIfCancellationRequested();
            progress(requestName.EndsWith("info", StringComparison.Ordinal) ? "Reading device identity…" : "Reading the saved configuration…");
            requests.Add(requestName);
            stream.Write(request, 0, request.Length);
            var response = SiemensWire.ReadResponse(stream, sessionToken, maximumBody);
            responses.Add(new
            {
                request = requestName, response.StatusCode, response.ReasonPhrase, response.Headers,
                bodyBytes = response.Body.Length,
                bodySha256 = Convert.ToHexStringLower(SHA256.HashData(response.Body))
            });
            return response;
        }
    }

    private static void RequireSuccess(SiemensHttpResponse response, string operation)
    {
        if (response.StatusCode is < 200 or >= 300)
            throw new InvalidDataException($"{operation} returned HTTP {response.StatusCode} {response.ReasonPhrase}.");
    }

    private static void WriteReport(string path, object report, byte[]? info, byte[]? config,
        byte[] received, bool success)
    {
        string target = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, $".siemens-report-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                Add("report.json", JsonSerializer.SerializeToUtf8Bytes(report, ReportJson));
                if (info is not null) Add("device-info.json", info);
                if (config is not null) Add(success ? "column-backup.signal" : "config-response.bin", config);
                if (!success && received.Length > 0) Add("received-prefix.bin", received);
                void Add(string name, byte[] bytes)
                {
                    using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                    entry.Write(bytes);
                }
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class RecordingStream(Stream inner, bool disposeInner) : Stream
    {
        private const int PrefixLimit = 64 * 1024;
        private readonly MemoryStream _prefix = new();
        public byte[] ReceivedPrefix => _prefix.ToArray();
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            int record = Math.Min(read, PrefixLimit - checked((int)_prefix.Length));
            if (record > 0) _prefix.Write(buffer, offset, record);
            return read;
        }
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _prefix.Dispose(); if (disposeInner) inner.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
