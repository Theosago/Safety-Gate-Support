using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SafetyGateSupport.Siemens;

public sealed record SiemensUploadTarget(string PortName, string DeviceInstanceId);
public sealed record SignalUploadResult(bool Success, bool UploadAttempted, bool ReadbackVerified,
    string Summary, string BackupPath, string ReportPath);

/// <summary>
/// Backup before write; exact archive transfer; verified readback before and after
/// requesting a normal application restart. Never retries a write or rolls back.
/// The connection factory must open only the user-selected normal USB device.
/// </summary>
public static class SignalUploadSession
{
    public static SignalUploadResult Run(PreparedSignalUpload selected, SiemensUploadTarget target,
        string reportPath, Func<CancellationToken, Stream> connect, CancellationToken token = default,
        Action<string>? progress = null, Action<TimeSpan, CancellationToken>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(connect);
        progress ??= _ => { };
        delay ??= Wait;
        string report = Path.GetFullPath(reportPath);
        string backup = Path.ChangeExtension(report, ".before.signal");
        if (File.Exists(backup))
            throw new IOException("A backup with that name already exists. Choose a new report filename.");
        // Reserve a new report before opening hardware. Never overwrite an existing
        // report, selected configuration, or prior recovery file.
        using (new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        var sessionToken = deadline.Token;
        var selectedBytes = selected.GetBytes();
        var files = new Dictionary<string, byte[]> { ["selected.signal"] = selectedBytes };
        var responses = new List<object>();
        var events = new List<object>();
        var reportWarnings = new List<object>();
        using var prefix = new MemoryStream();
        var started = DateTimeOffset.UtcNow;
        SiemensDeviceInfo? device = null;
        string? expectedMac = null, error = null;
        string stage = "Prepared";
        string operation = "Preparing the report";
        string summary = "Upload has not started.";
        bool success = false, uploadAttempted = false, writeIntent = false, preResetVerified = false;
        bool restartSent = false, readbackVerified = false, backupSaved = false;
        Checkpoint();

        try
        {
            sessionToken.ThrowIfCancellationRequested();
            Phase("Identifying", "Reading the selected column's identity…");
            operation = "Opening the selected USB port";
            using (var stream = new CaptureStream(connect(sessionToken), prefix))
            {
                (device, expectedMac) = Identity(stream, "device-info.json", sessionToken);
                Phase("BackingUp", "Reading and saving the current configuration…");
                byte[] before = Exchange(stream, "GET /api/v1/config", SiemensWire.GetConfigRequest(),
                    "backup-response.bin", SignalArchive.MaxArchiveBytes, sessionToken).Body;
                var original = SignalArchive.Inspect(before);
                if (original.HardwareVariant != selected.Archive.HardwareVariant)
                    throw new InvalidDataException("The selected file and existing device configuration have different hardware variants.");
                using (var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    output.Write(before);
                    output.Flush(flushToDisk: true);
                }
                backupSaved = true;
                files["before.signal"] = before;
                Phase("BackupSaved", "Original configuration saved. Checking the selected settings…");
                Checkpoint();
                sessionToken.ThrowIfCancellationRequested();

                if (SignalConfiguration.Equivalent(before, selectedBytes))
                {
                    success = readbackVerified = true;
                    summary = "The column already matches the selected configuration. Backup saved; no upload or restart was needed.";
                }
                else
                {
                    Phase("UploadStarting", "Uploading the selected .signal file…");
                    // A durable pre-write checkpoint records that a write may be
                    // attempted. A lost acknowledgement never causes retransmission.
                    writeIntent = true;
                    summary = "A write may start after this checkpoint. If interrupted, do not assume the original settings remain active.";
                    Checkpoint();
                    sessionToken.ThrowIfCancellationRequested();
                    uploadAttempted = true;
                    Exchange(stream, "PUT /api/v1/config", SiemensUploadWire.ConfigurationRequest(selectedBytes),
                        "upload-response.bin", 64 * 1024, sessionToken);
                    Phase("UploadAcknowledged", "Checking the stored configuration before restart…");
                    // Allow the vendor's observed settling interval before our
                    // additional readback request, as well as before restart.
                    delay(TimeSpan.FromSeconds(1), sessionToken);
                    byte[] stored = Exchange(stream, "GET /api/v1/config", SiemensWire.GetConfigRequest(),
                        "pre-restart-response.bin", SignalArchive.MaxArchiveBytes, sessionToken).Body;
                    if (!SignalConfiguration.Equivalent(selectedBytes, stored))
                        throw new InvalidDataException("The stored configuration does not match the selected file. No restart was requested.");
                    preResetVerified = true;
                    files["pre-restart-verified.signal"] = stored;
                    Phase("PreRestartVerified", "Stored settings verified. Requesting the column restart…");
                    Checkpoint();
                    Send(stream, "PUT /api/v1/system/reset/app", SiemensUploadWire.ApplicationRestartRequest(), sessionToken);
                    restartSent = true;
                    stream.Flush();
                    delay(TimeSpan.FromMilliseconds(300), sessionToken);
                }
                operation = "Closing the original USB connection";
            }

            if (restartSent)
            {
                Phase("Reconnecting", "Waiting for the column to reconnect…");
                // All device writes are complete. A report/share lock must not
                // prevent the remaining, read-only identity/settings verification.
                Checkpoint(required: false);
                delay(TimeSpan.FromSeconds(2), sessionToken);
                using var stream = new CaptureStream(Reconnect(), prefix);
                var (after, afterMac) = Identity(stream, "post-restart-info.json", sessionToken);
                if (!string.Equals(expectedMac, afterMac, StringComparison.OrdinalIgnoreCase) ||
                    after.Model != device!.Model || after.HardwareVariant != device.HardwareVariant)
                    throw new InvalidDataException("A different column answered after reconnect. Verification stopped.");
                Phase("VerifyingAfterRestart", "Checking the configuration after reconnect…");
                byte[] final = Exchange(stream, "GET /api/v1/config", SiemensWire.GetConfigRequest(),
                    "post-restart-response.bin", SignalArchive.MaxArchiveBytes, sessionToken).Body;
                if (!SignalConfiguration.Equivalent(selectedBytes, final))
                    throw new InvalidDataException("Configuration after reconnect does not match the selected file.");
                files["verified.signal"] = final;
                success = readbackVerified = true;
                summary = "Upload verified: settings match before restart and after reconnect. Check the spare column's light and sound behavior.";
                operation = "Closing the verified USB connection";
            }
            stage = success ? "Verified" : stage;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or
            TimeoutException or JsonException or OperationCanceledException or System.Security.SecurityException)
        {
            success = false;
            if (!uploadAttempted) writeIntent = false;
            error = exception is OperationCanceledException
                ? token.IsCancellationRequested ? "Cancelled by the user." : "The 120-second session deadline expired."
                : $"{operation}: {exception.GetType().Name}: {exception.Message}";
            events.Add(new { utc = DateTimeOffset.UtcNow, failureStage = stage, failureOperation = operation,
                exceptionType = exception.GetType().Name, exception.HResult, exception.StackTrace });
            summary = readbackVerified ? "Stored settings matched, but the session did not finish cleanly. " + error :
                preResetVerified ? "Selected settings were read back successfully; the final check did not finish. " + error : uploadAttempted
                ? "Upload is not fully verified; the column may already contain changed settings. " + error
                : "No upload was sent. " + error;
            if (backupSaved) summary += " The original backup is saved.";
        }

        try { Checkpoint(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            success = false;
            summary += " The report could not be updated: " + exception.Message;
        }
        return new SignalUploadResult(success, uploadAttempted, readbackVerified, summary,
            backupSaved ? backup : "", report);

        void Phase(string name, string message)
        {
            stage = name;
            events.Add(new { utc = DateTimeOffset.UtcNow, stage });
            progress(message);
        }

        (SiemensDeviceInfo Device, string Mac) Identity(Stream stream, string filename, CancellationToken cancellation)
        {
            var response = Exchange(stream, "GET /api/v1/info", SiemensWire.GetInfoRequest(), filename, 64 * 1024, cancellation);
            var identity = SiemensDeviceInfo.Parse(response.Body);
            if (!identity.IsSupportedTestColumn || identity.FirmwareVersion != "v1.0.3")
                throw new NotSupportedException("Upload currently supports only 8WD4613-5JH37, hardware variant 21, firmware v1.0.3.");
            using var json = JsonDocument.Parse(response.Body);
            var value = json.RootElement.GetProperty("hardware");
            if (!value.TryGetProperty("mac", out var macValue) || macValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("The device did not supply a stable hardware address for reconnect verification.");
            var mac = macValue.GetString()!;
            if (mac.Length != 17 || mac.Where((c, i) => i % 3 == 2 ? c != ':' : !char.IsAsciiHexDigit(c)).Any())
                throw new InvalidDataException("The device hardware address has an unsupported format.");
            return (identity, mac);
        }

        SiemensHttpResponse Exchange(Stream stream, string requestName, byte[] request, string filename,
            int maximum, CancellationToken cancellation)
        {
            Send(stream, requestName, request, cancellation);
            operation = "Reading the response to " + requestName;
            var response = SiemensWire.ReadResponse(stream, cancellation, maximum);
            files[filename] = response.Body;
            responses.Add(new { request = requestName, response.StatusCode, response.ReasonPhrase,
                response.Headers, bodyBytes = response.Body.Length,
                bodySha256 = Convert.ToHexStringLower(SHA256.HashData(response.Body)) });
            if (response.StatusCode is < 200 or >= 300)
                throw new InvalidDataException($"{requestName} returned HTTP {response.StatusCode} {response.ReasonPhrase}.");
            return response;
        }

        void Send(Stream stream, string name, byte[] bytes, CancellationToken cancellation)
        {
            operation = "Sending " + name;
            events.Add(new { utc = DateTimeOffset.UtcNow, requestAttempted = name });
            for (int offset = 0; offset < bytes.Length; offset += 4096)
            {
                cancellation.ThrowIfCancellationRequested();
                stream.Write(bytes, offset, Math.Min(4096, bytes.Length - offset));
            }
            cancellation.ThrowIfCancellationRequested();
        }

        Stream Reconnect()
        {
            operation = "Reopening the selected USB port";
            var watch = Stopwatch.StartNew();
            while (true)
            {
                sessionToken.ThrowIfCancellationRequested();
                try { return connect(sessionToken); }
                catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or TimeoutException) &&
                    watch.Elapsed < TimeSpan.FromSeconds(20))
                {
                    delay(TimeSpan.FromMilliseconds(250), sessionToken);
                }
            }
        }

        void Checkpoint(bool required = true)
        {
            string previousOperation = operation;
            operation = "Saving the report during " + stage;
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                files["report.json"] = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    tool = "SiemensUsbCheck", version = "0.3.0", operation = "Configuration upload",
                    startedUtc = started, updatedUtc = DateTimeOffset.UtcNow, stage, success, summary, error,
                    uploadAttempted, writeMayHaveStarted = writeIntent || uploadAttempted,
                    backupSaved, preResetVerified, restartSent, readbackVerified,
                    selectedFile = selected.FileName, selected.Sha256, devicePort = target, device,
                    recoveryFile = backupSaved ? Path.GetFileName(backup) : null, responses, events, reportWarnings,
                    hardwareValidation = "Experimental upload. Software tests do not verify physical light/sound behavior. No automatic write retry or rollback."
                }, new JsonSerializerOptions { WriteIndented = true });
                if (!success && prefix.Length > 0) files["received-prefix.bin"] = prefix.ToArray();
                else files.Remove("received-prefix.bin");
                string temporary = report + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                            foreach (var item in files)
                            {
                                using var entry = zip.CreateEntry(item.Key, CompressionLevel.Optimal).Open();
                                entry.Write(item.Value);
                            }
                        file.Flush(flushToDisk: true);
                    }
                    File.Move(temporary, report, overwrite: true);
                    operation = previousOperation;
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    reportWarnings.Add(new { utc = DateTimeOffset.UtcNow, stage, attempt,
                        operation = "Save report", exceptionType = exception.GetType().Name, exception.Message, exception.HResult });
                    if (attempt == 4)
                    {
                        if (required)
                            throw new IOException($"Cannot save the upload report '{report}' during {stage}: {exception.Message}", exception);
                        operation = previousOperation;
                        return;
                    }
                    // Retrying only a report snapshot, never a USB write. Complete
                    // recovery reporting even when the user cancelled the session.
                    delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }

    private static void Wait(TimeSpan duration, CancellationToken cancellation)
    {
        if (cancellation.WaitHandle.WaitOne(duration)) cancellation.ThrowIfCancellationRequested();
    }

    private sealed class CaptureStream(Stream inner, MemoryStream prefix) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            int record = Math.Min(read, 65536 - (int)prefix.Length);
            if (record > 0) prefix.Write(buffer, offset, record);
            return read;
        }
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
