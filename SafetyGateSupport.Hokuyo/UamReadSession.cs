using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SafetyGateSupport.Hokuyo;

public static class UamReadSession
{
    public static UamReadResult Run(UamUsbPort port, string reportPath, Func<CancellationToken, Stream> connect, CancellationToken token, Action<string> progress)
    {
        // Reserve the report before touching hardware; never overwrite old evidence.
        using var file = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var report = new ZipArchive(file, ZipArchiveMode.Create);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        var ct = deadline.Token;
        Stream? stream = null;
        bool success = false;
        string summary = "Scanner condition not verified.";
        string? failure = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            progress("Opening selected test scanner…");
            stream = connect(ct);
            progress("Reading scanner identity with the vendor startup query…");
            var identity = Exchange(stream, UamWire.IdentityRequest(), "VR42", report, ct);
            // The real VR42 reply already contains product, firmware and unit ID.
            // Do not issue a redundant older query after a complete primary identity.
            if (identity.Status == "90" && identity.Data.Length == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress("Startup identity query rejected; reading legacy identity once…");
                identity = Exchange(stream, UamWire.LegacyIdentityRequest(), "VR00", report, ct);
            }
            if (identity.Status != "00") RejectIdentity(identity);
            var deviceIdentity = UamIdentity.Parse(identity.Data);
            Write(report, "identity.json", JsonSerializer.SerializeToUtf8Bytes(deviceIdentity));
            ct.ThrowIfCancellationRequested();
            progress("Reading one status snapshot…");
            var status = Exchange(stream, UamWire.StatusRequest(), "AR00", report, ct);
            if (status.Status != "00") throw new InvalidDataException("Status request rejected with protocol status " + status.Status + ".");
            summary = deviceIdentity.Describe() + UamStatus.Parse(status.Data).Describe();
            ct.ThrowIfCancellationRequested();
            success = true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failure = error.GetType().Name + ": " + error.Message;
            summary = "Scanner condition NOT VERIFIED. " + (error is OperationCanceledException ? "Read canceled or session deadline reached." : error.Message);
        }
        finally
        {
            try { stream?.Dispose(); }
            catch (Exception error) { success = false; failure = "USB close failed: " + error.Message; summary += "\n" + failure + "\nScanner condition not verified."; }
        }
        Write(report, "result.json", JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 2, timestampUtc = DateTimeOffset.UtcNow, port, readCompleted = success, hardwareValidated = false, summary, failure, allowedRequests = new[] { "VR42", "VR00", "AR00" } }, new JsonSerializerOptions { WriteIndented = true }));
        Write(report, "README.txt", Encoding.UTF8.GetBytes("First hardware validation. Compare this snapshot with Project Designer and the scanner front display on the same test unit. Commands are startup identity VR42, legacy VR00 only after an empty status-90 startup rejection, and one-shot measurement/status AR00. No login, configuration, reset, streaming or mode-changing commands are sent. No repeated commands or automatic reset. A completed read is not a passed scanner functional test. Do not use this report as authorization to operate equipment.\n\n" + summary));
        progress("USB closed; finishing local report…");
        return new UamReadResult(success, summary, reportPath);
    }
    private static void RejectIdentity(UamReply identity) =>
        throw new InvalidDataException($"USB communication received a valid reply, but the identity read returned protocol status {identity.Status}. This is not a scanner display fault code; scanner health cannot be determined. No status request was sent.");

    private static UamReply Exchange(Stream stream, byte[] request, string command, ZipArchive report, CancellationToken token)
    {
        Write(report, command + "-request.bin", request);
        using var raw = new MemoryStream();
        try
        {
            token.ThrowIfCancellationRequested();
            stream.Write(request, 0, request.Length);
            var prefix = new byte[5];
            ReadExact(prefix);
            if (prefix[0] != 2) throw new InvalidDataException("Unexpected data before UAM frame; no resynchronization or retry performed.");
            int length = UamWire.Hex(prefix.AsSpan(1, 4));
            if (length < 16 || length > UamWire.MaxFrameLength) throw new InvalidDataException("Unsupported UAM frame length.");
            var rest = new byte[length - 5]; ReadExact(rest);
            token.ThrowIfCancellationRequested();
            return UamWire.Decode(raw.ToArray(), command);
        }
        finally { Write(report, command + "-response.bin", raw.ToArray()); }
        void ReadExact(byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                token.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0) throw new EndOfStreamException("UAM reply ended before the declared length.");
                raw.Write(buffer, offset, read); offset += read;
            }
        }
    }
    private static void Write(ZipArchive report, string name, byte[] bytes)
    {
        using var output = report.CreateEntry(name, CompressionLevel.Optimal).Open(); output.Write(bytes);
    }
}
