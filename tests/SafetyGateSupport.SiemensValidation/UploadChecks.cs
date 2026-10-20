using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SafetyGateSupport.Siemens;

namespace SafetyGateSupport.SiemensValidation;

internal static class UploadChecks
{
    private static readonly byte[] GetInfo = SiemensWire.GetInfoRequest();
    private static readonly byte[] GetConfig = SiemensWire.GetConfigRequest();
    private static readonly byte[] Reset = SiemensUploadWire.ApplicationRestartRequest();
    private static readonly SiemensUploadTarget Target = new("COM99", "synthetic-usb-instance");
    public static IEnumerable<(string Name, Action Check)> Cases =>
    [
        ("upload: exact binary PUT framing", ExactFraming),
        ("upload: only normal application restart framing", RestartFraming),
        ("upload: prepared selection is immutable", ImmutableSelection),
        ("upload: other configuration hardware rejected", OtherFileVariant),
        ("upload: oversized selection rejected", () => Throws<InvalidDataException>(() => PreparedSignalUpload.FromBytes(new byte[PreparedSignalUpload.MaxUploadBytes + 1]))),
        ("upload: corrupt selection rejected", () => Throws<InvalidDataException>(() => PreparedSignalUpload.FromBytes([1, 2, 3]))),
        ("upload: report ZIP cannot be selected", WrongExtension),
        ("upload: matching settings need no write or restart", AlreadyMatches),
        ("upload: successful backup PUT restart and reconnected verification", SuccessfulUpload),
        ("upload: durable backup exists before first PUT byte", BackupBeforeWrite),
        ("upload: model mismatch sends no config or write", () => RejectedIdentity(Info(article: 65711155))),
        ("upload: variant mismatch sends no config or write", () => RejectedIdentity(Info(variant: 20))),
        ("upload: unsupported firmware sends no config or write", () => RejectedIdentity(Info(firmware: "unknown"))),
        ("upload: absent hardware address sends no config or write", () => RejectedIdentity(Info(mac: null))),
        ("upload: invalid backup prevents PUT", InvalidBackup),
        ("upload: backup disk failure prevents PUT", BackupFailure),
        ("upload: report checkpoint failure prevents PUT", CheckpointFailure),
        ("upload: report failure after PUT preserves the standalone backup", PostWriteReportFailure),
        ("upload: reconnect report failure cannot stop read-only verification", ReconnectReportFailure),
        ("upload: transient reconnect report failure recovers without another PUT", TransientReconnectReportFailure),
        ("upload: verified settings with a close failure are not a clean success", CloseFailureAfterReadback),
        ("upload: partial driver write failure is never retried", PartialWriteFailure),
        ("upload: HTTP upload rejection is not retried or reset", UploadRejected),
        ("upload: lost acknowledgement is not retried or reset", LostAcknowledgement),
        ("upload: incorrect pre-restart readback prevents reset", PreRestartMismatch),
        ("upload: different hardware after reconnect stops verification", DifferentHardware),
        ("upload: incorrect post-restart readback is not success", PostRestartMismatch),
        ("upload: cancellation before connecting sends nothing", CancelBeforeConnect),
        ("upload: cancellation after backup prevents PUT", CancelAfterBackup),
        ("upload: cancellation after PUT preserves original backup", CancelAfterWrite),
        ("upload: existing report is never overwritten", ExistingReport),
        ("upload: existing backup is never overwritten", ExistingBackup),
        ("upload: ZIP timestamps compression and JSON object order ignored", EquivalentPackaging),
        ("upload: changed state or editor metadata fails comparison", ChangedSettings)
    ];

    private static byte[] Original() => SignalArchiveChecks.Archive();
    private static byte[] Desired() => SignalArchiveChecks.Archive(files =>
    {
        var state = JsonNode.Parse(files["state_config_7.json"])!;
        state["Segments"]![0]!["r"] = 50;
        state["Segments"]![0]!["g"] = 100;
        files["state_config_7.json"] = state.ToJsonString();
    });

    private static byte[] Info(int article = 65710155, int variant = 21,
        string firmware = "v1.0.3", string? mac = "00:11:22:33:44:55") => JsonSerializer.SerializeToUtf8Bytes(new
        { articleNumber = article, firmwareVersion = firmware, firstConfig = false,
          hardware = new { hardwareVariant = variant, mac } });
    private static Exchange Reply(byte[] request, byte[] body, int status = 200) => new(request,
        Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: {body.Length}\r\n\r\n").Concat(body).ToArray());
    private static Exchange[] BeforeReset(byte[] before, byte[] selected, byte[]? preReadback = null) =>
    [ Reply(GetInfo, Info()), Reply(GetConfig, before), Reply(SiemensUploadWire.ConfigurationRequest(selected), [], 204),
      Reply(GetConfig, preReadback ?? selected), new(Reset, null) ];

    private static void ExactFraming()
    {
        byte[] body = [0, 255, 128, 10, 13, 99];
        var header = Encoding.ASCII.GetBytes("PUT /api/v1/config HTTP/1.0\r\nContent-Length: 6\r\nContent-Type: application/octet-stream\r\nConnection: keep-alive\r\n\r\n");
        var request = SiemensUploadWire.ConfigurationRequest(body);
        Check(request.SequenceEqual(header.Concat(body)), "The binary body or length framing changed.");
        body[0] = 12;
        Check(request[header.Length] == 0, "The request retained mutable caller storage.");
    }

    private static void RestartFraming() => Check(Encoding.ASCII.GetString(Reset) ==
        "PUT /api/v1/system/reset/app HTTP/1.0\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n", "Incorrect restart command.");
    private static void ImmutableSelection()
    {
        var bytes = Desired(); var saved = bytes.ToArray();
        var selected = PreparedSignalUpload.FromBytes(bytes);
        bytes[0] = 0;
        selected.GetBytes()[0] = 0;
        Check(selected.GetBytes().SequenceEqual(saved), "The prepared file changed after selection.");
    }
    private static void OtherFileVariant() => Throws<NotSupportedException>(() => PreparedSignalUpload.FromBytes(
        SignalArchiveChecks.Zip(SignalArchiveChecks.Files(51, 15, true))));
    private static void WrongExtension()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "report.zip");File.WriteAllBytes(path, Desired());
        Throws<InvalidDataException>(() => PreparedSignalUpload.Load(path));
    }
    private static void AlreadyMatches()
    {
        using var f = new Fixture();var bytes = Original();
        var stream = new ScriptStream(Reply(GetInfo, Info()), Reply(GetConfig, bytes));
        var result = f.Run(bytes, [stream]);
        Check(result.Success && !result.UploadAttempted && result.ReadbackVerified && stream.Completed, "Matching settings should not upload or restart.");
        Check(File.ReadAllBytes(result.BackupPath).SequenceEqual(bytes), "Matching settings lost their backup.");
    }
    private static void SuccessfulUpload()
    {
        using var f = new Fixture();var before = Original();var selected = Desired();
        var first = new ScriptStream(BeforeReset(before, selected));
        var second = new ScriptStream(Reply(GetInfo, Info()), Reply(GetConfig, Repack(selected)));
        var result = f.Run(selected, [first, second]);
        Check(result.Success && result.UploadAttempted && result.ReadbackVerified, result.Summary);
        Check(first.Completed && second.Completed && first.Disposed && second.Disposed, "Connections were not completed and closed.");
        Check(File.ReadAllBytes(result.BackupPath).SequenceEqual(before), "Original backup bytes changed.");
        using var zip = ZipFile.OpenRead(f.Report);
        Check(Read(zip, "selected.signal").SequenceEqual(selected) && Read(zip, "before.signal").SequenceEqual(before), "Report lost original files.");
        using var json = JsonDocument.Parse(Read(zip, "report.json"));
        Check(json.RootElement.GetProperty("restartSent").GetBoolean() && json.RootElement.GetProperty("readbackVerified").GetBoolean(), "Missing final verification evidence.");
    }
    private static void BackupBeforeWrite()
    {
        using var f = new Fixture();var before = Original();var selected = Desired();bool checkedDisk = false;
        var first = new ScriptStream(BeforeReset(before, selected))
        {
            BeforeRequest = request =>
            {
                if (!request.AsSpan().StartsWith("PUT /api/v1/config "u8)) return;
                Check(File.ReadAllBytes(f.Backup).SequenceEqual(before), "PUT started without durable original backup.");
                using var zip = ZipFile.OpenRead(f.Report);
                Check(Read(zip, "before.signal").SequenceEqual(before), "PUT started before the backup report was saved.");
                using var json = JsonDocument.Parse(Read(zip, "report.json"));
                Check(json.RootElement.GetProperty("writeMayHaveStarted").GetBoolean(), "The durable checkpoint understated an interrupted write.");
                checkedDisk = true;
            }
        };
        var result = f.Run(selected, [first, new ScriptStream(Reply(GetInfo, Info()), Reply(GetConfig, selected))]);
        Check(result.Success && checkedDisk, result.Summary);
    }
    private static void RejectedIdentity(byte[] info)
    {
        using var f = new Fixture();var s = new ScriptStream(Reply(GetInfo, info));
        var result = f.Run(Desired(), [s]);
        Check(!result.Success && !result.UploadAttempted && s.Completed && !File.Exists(f.Backup), "Incompatible identity reached configuration upload.");
    }
    private static void InvalidBackup()
    {
        using var f = new Fixture();var s = new ScriptStream(Reply(GetInfo, Info()), Reply(GetConfig, [1, 2, 3]));
        var result = f.Run(Desired(), [s]);
        Check(!result.UploadAttempted && !result.Success && s.Completed && !File.Exists(f.Backup), "Invalid backup allowed upload.");
    }
    private static void BackupFailure()
    {
        using var f = new Fixture();var s = new ScriptStream(Reply(GetInfo, Info()), Reply(GetConfig, Original()));
        var result = f.Run(Desired(), [s], progress: message => { if (message.StartsWith("Reading and saving", StringComparison.Ordinal)) Directory.CreateDirectory(f.Backup); });
        Check(!result.Success && !result.UploadAttempted && s.Completed, "Backup disk failure allowed upload.");
    }
    private static void CheckpointFailure()
    {
        using var f = new Fixture();var original = Original();var s = new ScriptStream(Reply(GetInfo, Info()), Reply(GetConfig, original));
        var result = f.Run(Desired(), [s], progress: message =>
        {
            if (!message.StartsWith("Original configuration saved", StringComparison.Ordinal)) return;
            File.Delete(f.Report);Directory.CreateDirectory(f.Report);
        });
        Check(!result.Success && !result.UploadAttempted && s.Completed && File.ReadAllBytes(f.Backup).SequenceEqual(original), "Checkpoint failure allowed upload or destroyed backup.");
        Check(result.Summary.Contains(f.Report, StringComparison.Ordinal), "Report failure did not identify the affected path.");
    }
    private static void UploadRejected()
    {
        using var f = new Fixture();var before = Original();var selected = Desired();var error = "busy"u8.ToArray();
        var s = new ScriptStream(Reply(GetInfo, Info()), Reply(GetConfig, before), Reply(SiemensUploadWire.ConfigurationRequest(selected), error, 503));
        var result = f.Run(selected, [s]);
        Check(!result.Success && result.UploadAttempted && s.Completed, "HTTP rejection retried or proceeded to restart.");
        using var zip = ZipFile.OpenRead(f.Report);Check(Read(zip, "upload-response.bin").SequenceEqual(error), "HTTP error body lost.");
    }
    private static void PostWriteReportFailure()
    {
        using var f = new Fixture();var before = Original();var selected = Desired();
        var s = new ScriptStream(BeforeReset(before, selected)[..^1])
        {
            AfterResponse = index => { if (index == 2) { File.Delete(f.Report);Directory.CreateDirectory(f.Report); } }
        };
        var result = f.Run(selected, [s]);
        Check(!result.Success && result.UploadAttempted && s.Completed && File.ReadAllBytes(f.Backup).SequenceEqual(before),
            "Report failure after PUT lost the original backup or continued to restart.");
    }
    private static void PartialWriteFailure()
    {
        using var f = new Fixture();var before = Original();var selected = Desired();
        var s = new ScriptStream(Reply(GetInfo, Info()), Reply(GetConfig, before), Reply(SiemensUploadWire.ConfigurationRequest(selected), [], 204))
        { FailConfigWrite = true };
        var result = f.Run(selected, [s]);
        Check(!result.Success && result.UploadAttempted && !s.Completed && s.Disposed &&
            result.Summary.Contains("Synthetic partial driver write", StringComparison.Ordinal) && File.ReadAllBytes(f.Backup).SequenceEqual(before),
            "A partial write was retried, accepted, or lost its recovery data.");
    }
    private static void CloseFailureAfterReadback()
    {
        using var f = new Fixture();var before = Original();
        var s = new ScriptStream(Reply(GetInfo, Info()), Reply(GetConfig, before)) { FailDispose = true };
        var result = f.Run(before, [s]);
        Check(!result.Success && result.ReadbackVerified && !result.UploadAttempted && s.Completed &&
            result.Summary.Contains("Closing the original USB connection", StringComparison.Ordinal),
            "A failed serial close was reported as a clean success or lost the verified-settings distinction.");
    }
    private static void ReconnectReportFailure()
    {
        using var f = new Fixture();var before = Original();var selected = Desired();
        var first = new ScriptStream(BeforeReset(before, selected));
        var second = new ScriptStream(Reply(GetInfo, Info()), Reply(GetConfig, selected));
        var result = f.Run(selected, [first, second], progress: message =>
        {
            if (!message.StartsWith("Waiting for the column to reconnect", StringComparison.Ordinal)) return;
            File.Delete(f.Report);Directory.CreateDirectory(f.Report);
        });
        Check(!result.Success && result.ReadbackVerified && first.Completed && second.Completed,
            "A post-restart report failure stopped read-only verification or was hidden.");
        Check(File.ReadAllBytes(f.Backup).SequenceEqual(before) && result.Summary.Contains(f.Report, StringComparison.Ordinal),
            "Permanent report failure lost the recovery backup or affected-path context.");
    }
    private static void TransientReconnectReportFailure()
    {
        using var f = new Fixture();var before = Original();var selected = Desired();bool recovered = false;
        var first = new ScriptStream(BeforeReset(before, selected));
        var second = new ScriptStream(Reply(GetInfo, Info()), Reply(GetConfig, Repack(selected)));
        var result = f.Run(selected, [first, second], progress: message =>
        {
            if (!message.StartsWith("Waiting for the column to reconnect", StringComparison.Ordinal)) return;
            File.Delete(f.Report);Directory.CreateDirectory(f.Report);
        }, delay: (_, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (Directory.Exists(f.Report)) { Directory.Delete(f.Report);recovered = true; }
        });
        Check(recovered && result.Success && result.ReadbackVerified && first.Completed && second.Completed,
            "A transient report failure did not recover or triggered an extra upload.");
        using var zip = ZipFile.OpenRead(f.Report);using var json = JsonDocument.Parse(Read(zip, "report.json"));
        Check(json.RootElement.GetProperty("reportWarnings").GetArrayLength() > 0 &&
            File.ReadAllBytes(f.Backup).SequenceEqual(before), "Recovered report failure was not recorded or lost its backup.");
    }
    private static void LostAcknowledgement()
    {
        using var f = new Fixture();var selected = Desired();
        var s = new ScriptStream(Reply(GetInfo, Info()), Reply(GetConfig, Original()), new(SiemensUploadWire.ConfigurationRequest(selected), null));
        var result = f.Run(selected, [s]);
        Check(!result.Success && result.UploadAttempted && s.Completed && File.Exists(f.Backup), "Lost acknowledgement caused a retry or lost backup.");
    }
    private static void PreRestartMismatch()
    {
        using var f = new Fixture();var before = Original();var selected = Desired();
        var s = new ScriptStream(BeforeReset(before, selected, before)[..^1]);
        var result = f.Run(selected, [s]);
        Check(!result.Success && !result.ReadbackVerified && s.Completed, "Incorrect stored settings were restarted or accepted.");
    }
    private static void DifferentHardware()
    {
        using var f = new Fixture();var selected = Desired();var first = new ScriptStream(BeforeReset(Original(), selected));
        var second = new ScriptStream(Reply(GetInfo, Info(mac: "00:11:22:33:44:99")));
        var result = f.Run(selected, [first, second]);
        Check(!result.Success && !result.ReadbackVerified && first.Completed && second.Completed, "A different physical device was verified.");
    }
    private static void PostRestartMismatch()
    {
        using var f = new Fixture();var selected = Desired();var first = new ScriptStream(BeforeReset(Original(), selected));
        var second = new ScriptStream(Reply(GetInfo, Info()), Reply(GetConfig, Original()));
        var result = f.Run(selected, [first, second]);
        Check(!result.Success && !result.ReadbackVerified && first.Completed && second.Completed, "Incorrect post-restart settings were accepted.");
    }
    private static void CancelBeforeConnect()
    {
        using var f = new Fixture();using var cancel = new CancellationTokenSource();cancel.Cancel();
        var result = f.Run(Desired(), [], cancel.Token);
        Check(!result.Success && !result.UploadAttempted && !File.Exists(f.Backup), "Pre-cancelled session opened hardware or wrote settings.");
    }
    private static void CancelAfterBackup()
    {
        using var f = new Fixture();using var cancel = new CancellationTokenSource();
        var s = new ScriptStream(Reply(GetInfo, Info()), Reply(GetConfig, Original()));
        var result = f.Run(Desired(), [s], cancel.Token, message => { if (message.StartsWith("Original configuration saved", StringComparison.Ordinal)) cancel.Cancel(); });
        Check(!result.UploadAttempted && s.Completed && File.Exists(f.Backup), "Cancellation after backup allowed upload.");
    }
    private static void CancelAfterWrite()
    {
        using var f = new Fixture();using var cancel = new CancellationTokenSource();var selected = Desired();var before = Original();
        var s = new ScriptStream(Reply(GetInfo, Info()), Reply(GetConfig, before), Reply(SiemensUploadWire.ConfigurationRequest(selected), [], 204))
        { AfterResponse = index => { if (index == 2) cancel.Cancel(); } };
        var result = f.Run(selected, [s], cancel.Token);
        Check(!result.Success && result.UploadAttempted && s.Completed && File.ReadAllBytes(f.Backup).SequenceEqual(before), "Cancellation after PUT lost backup or triggered further requests.");
    }
    private static void ExistingReport()
    {
        using var f = new Fixture();File.WriteAllText(f.Report, "keep");
        Throws<IOException>(() => f.Run(Desired(), []));Check(File.ReadAllText(f.Report) == "keep", "Existing report overwritten.");
    }
    private static void ExistingBackup()
    {
        using var f = new Fixture();File.WriteAllText(f.Backup, "keep");
        Throws<IOException>(() => f.Run(Desired(), []));Check(File.ReadAllText(f.Backup) == "keep", "Existing recovery backup overwritten.");
    }
    private static void EquivalentPackaging()
    {
        var original = Original();var repacked = Repack(original);
        Check(!original.SequenceEqual(repacked) && SignalConfiguration.Equivalent(original, repacked), "ZIP packaging differences were mistaken for changed settings.");
        var files = SignalArchiveChecks.Files();var obj = JsonNode.Parse(files["configurator.conf"])!.AsObject();
        var reversed = new JsonObject();foreach (var pair in obj.Reverse()) reversed[pair.Key] = pair.Value!.DeepClone();
        files["configurator.conf"] = reversed.ToJsonString();
        Check(SignalConfiguration.Equivalent(original, SignalArchiveChecks.Zip(files)), "JSON object property order changed configuration equivalence.");
    }
    private static void ChangedSettings()
    {
        Check(!SignalConfiguration.Equivalent(Original(), Desired()), "Changed expanded state was ignored.");
        var edited = SignalArchiveChecks.Archive(files =>
        {
            var obj = JsonNode.Parse(files["configurator.conf"])!;obj["editorName"] = "changed";files["configurator.conf"] = obj.ToJsonString();
        });
        Check(!SignalConfiguration.Equivalent(Original(), edited), "Editor settings were ignored.");
    }
    private static byte[] Repack(byte[] bytes)
    {
        using var input = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var item in input.Entries.Reverse())
            {
                var entry = zip.CreateEntry(item.FullName, CompressionLevel.Optimal);entry.LastWriteTime = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var source = item.Open();using var destination = entry.Open();source.CopyTo(destination);
            }
        return output.ToArray();
    }
    private static byte[] Read(ZipArchive zip, string name)
    { using var source = zip.GetEntry(name)!.Open();using var output = new MemoryStream();source.CopyTo(output);return output.ToArray(); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action run) where T : Exception { try { run(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "siemens-upload-test-" + Guid.NewGuid().ToString("N"));
        public string Report => Path.Combine(Root, "upload.zip");
        public string Backup => Path.Combine(Root, "upload.before.signal");
        public Fixture() => Directory.CreateDirectory(Root);
        public SignalUploadResult Run(byte[] selected, ScriptStream[] streams, CancellationToken token = default,
            Action<string>? progress = null, Action<TimeSpan, CancellationToken>? delay = null)
        {
            int next = 0;
            return SignalUploadSession.Run(PreparedSignalUpload.FromBytes(selected), Target, Report, _ =>
            {
                Check(next < streams.Length, "Unexpected connection or reconnection.");return streams[next++];
            }, token, progress, delay ?? ((_, cancellation) => cancellation.ThrowIfCancellationRequested()));
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
    private sealed record Exchange(byte[] Request, byte[]? Response);
    private sealed class ScriptStream(params Exchange[] script) : Stream
    {
        private int _index, _written, _read;
        private bool _replyAvailable;
        public Action<byte[]>? BeforeRequest { get; init; }
        public Action<int>? AfterResponse { get; init; }
        public bool FailConfigWrite { get; init; }
        public bool FailDispose { get; init; }
        public bool Completed => _index == script.Length;
        public bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanWrite => !Disposed;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(!Disposed && _index < script.Length && !_replyAvailable, "Unexpected write or retry.");
            var request = script[_index].Request;
            if (FailConfigWrite && _written > 0 && request.AsSpan().StartsWith("PUT /api/v1/config "u8))
                throw new IOException("Synthetic partial driver write failed.");
            if (_written == 0) BeforeRequest?.Invoke(request);
            Check(count <= request.Length - _written && buffer.AsSpan(offset, count).SequenceEqual(request.AsSpan(_written, count)), "Request bytes differ from the required command.");
            _written += count;
            if (_written == request.Length)
            {
                if (script[_index].Response is null) { _index++;_written = 0; }
                else _replyAvailable = true;
            }
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Check(!Disposed, "Read from a closed connection.");
            if (_index == script.Length) throw new EndOfStreamException("Synthetic acknowledgement lost.");
            Check(_replyAvailable, "Read before expected request completed.");
            var response = script[_index].Response!;
            int copied = Math.Min(7, Math.Min(count, response.Length - _read));
            response.AsSpan(_read, copied).CopyTo(buffer.AsSpan(offset, copied));_read += copied;
            if (_read == response.Length)
            {
                int finished = _index++;_read = _written = 0;_replyAvailable = false;AfterResponse?.Invoke(finished);
            }
            return copied;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            Disposed = true;base.Dispose(disposing);
            if (disposing && FailDispose) throw new IOException("Synthetic serial close failed.");
        }
    }
}
