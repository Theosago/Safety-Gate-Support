using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SafetyGateSupport.Siemens;

/// <summary>An independently validated, immutable snapshot of the chosen file.</summary>
public sealed class PreparedSignalUpload
{
    // A diagnostic-tool limit, not an inferred limit of the firmware. Observed
    // schema-5 configurations are under 20 KiB; bound transfer time and memory.
    public const int MaxUploadBytes = 1024 * 1024;
    private readonly byte[] _bytes;
    public string FileName { get; }
    public string Sha256 { get; }
    public SignalArchiveInfo Archive { get; }

    private PreparedSignalUpload(byte[] bytes, string fileName)
    {
        if (bytes.Length > MaxUploadBytes)
            throw new InvalidDataException("This upload tool accepts .signal files up to 1 MiB.");
        _bytes = (byte[])bytes.Clone();
        Archive = SignalArchive.Inspect(_bytes);
        if (Archive.HardwareVariant != 21 || Archive.SegmentCount != 9 || !Archive.HasSound)
            throw new NotSupportedException("Choose a nine-segment, sound-capable variant-21 configuration for 8WD4613-5JH37.");
        FileName = Path.GetFileName(fileName);
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(_bytes));
    }

    public static PreparedSignalUpload Load(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".signal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select the .signal configuration itself, not a report ZIP.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxUploadBytes)
            throw new InvalidDataException("This upload tool accepts .signal files up to 1 MiB.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return new PreparedSignalUpload(bytes, path);
    }

    public static PreparedSignalUpload FromBytes(byte[] bytes, string fileName = "selected.signal") => new(bytes, fileName);
    public byte[] GetBytes() => (byte[])_bytes.Clone();
}

public static class SignalConfiguration
{
    /// <summary>ZIP timestamps/compression and JSON object ordering do not change settings.</summary>
    public static bool Equivalent(byte[] left, byte[] right)
    {
        SignalArchive.Inspect(left);
        SignalArchive.Inspect(right);
        using var a = new ZipArchive(new MemoryStream(left, writable: false), ZipArchiveMode.Read);
        using var b = new ZipArchive(new MemoryStream(right, writable: false), ZipArchiveMode.Read);
        foreach (var entry in a.Entries)
        {
            using var firstStream = entry.Open();
            using var secondStream = b.GetEntry(entry.FullName)!.Open();
            using var first = JsonDocument.Parse(firstStream, new JsonDocumentOptions { MaxDepth = 32 });
            using var second = JsonDocument.Parse(secondStream, new JsonDocumentOptions { MaxDepth = 32 });
            if (!JsonElement.DeepEquals(first.RootElement, second.RootElement))
                return false;
        }
        return true;
    }
}
