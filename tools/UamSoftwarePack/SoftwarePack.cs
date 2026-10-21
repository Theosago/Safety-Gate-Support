using System.IO;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UamSoftwarePack;

public sealed record PackResult(string Path, long Bytes, int IncludedFiles, int TotalFiles);

/// <summary>Packages bounded software files for static inspection. No vendor code or device handles are opened.</summary>
public static class SoftwarePack
{
    public const long MaxSelectedBytes = 26L * 1024 * 1024;
    public const long MaxPackBytes = 30L * 1024 * 1024;
    private const int MaxEntries = 20000;
    private const int MaxInventoryBytes = 4 * 1024 * 1024;
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    { uint value = (uint)n; for (int i = 0; i < 8; i++) value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1; return value; }).ToArray();
    private sealed record EntryInfo(string Name, long Bytes, long CompressedBytes, string Decision);
    private sealed record IncludedFile(string SourceEntry, string PackEntry, long Bytes, string Sha256, string Format);

    public static PackResult Create(string sourceZip, string destinationZip, CancellationToken token = default, Action<string>? progress = null)
    {
        string source = Path.GetFullPath(sourceZip), destination = Path.GetFullPath(destinationZip);
        if (!Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Select the original UAM software ZIP.");
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) || File.Exists(destination)) throw new IOException("Choose a new output ZIP filename. Existing files are preserved.");
        if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new DirectoryNotFoundException("The output folder does not exist.");
        token.ThrowIfCancellationRequested();
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".uam-pack-{Guid.NewGuid():N}.tmp");
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("The source ZIP exceeds the 2 GiB collector limit.");
            using var sourceArchive = new ZipArchive(input, ZipArchiveMode.Read, true);
            if (sourceArchive.Entries.Count > MaxEntries) throw new InvalidDataException("The ZIP exceeds the 20,000-entry inventory limit.");
            var files = new List<ZipArchiveEntry>(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in sourceArchive.Entries)
            {
                token.ThrowIfCancellationRequested();
                string path = SafeName(entry.FullName);
                if (!paths.Add(path)) throw new InvalidDataException("The source ZIP contains duplicate or case-colliding paths.");
                if (entry.IsEncrypted || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("Encrypted entries and symbolic links are unsupported.");
                if (!entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\')) files.Add(entry);
            }
            if (files.Count == 0) throw new InvalidDataException("The ZIP contains no files.");
            var decisions = files.ToDictionary(e => e.FullName, _ => "Inventory only: not a selected program, driver, resource, or manual file.");
            var selected = new List<ZipArchiveEntry>(); long budget = 0;
            foreach (var entry in files.Where(e => Priority(e.FullName) < 100).OrderBy(e => Priority(e.FullName)).ThenBy(e => e.Length).ThenBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
            {
                if (entry.Length > MaxSelectedBytes - budget) { decisions[entry.FullName] = "Inventory only: file does not fit the 26 MiB selected-file budget."; continue; }
                budget += entry.Length; selected.Add(entry); decisions[entry.FullName] = "Included for static inspection.";
            }
            byte[] inventory = JsonSerializer.SerializeToUtf8Bytes(files.Select(e => new EntryInfo(e.FullName, e.Length, e.CompressedLength, decisions[e.FullName])), new JsonSerializerOptions { WriteIndented = true });
            if (inventory.Length > MaxInventoryBytes) throw new InvalidDataException("The file inventory exceeds the 4 MiB limit.");
            var included = new List<IncludedFile>();
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var pack = new ZipArchive(output, ZipArchiveMode.Create))
            {
                Add("inventory.json", inventory);
                foreach (var entry in selected)
                {
                    token.ThrowIfCancellationRequested(); progress?.Invoke(entry.FullName);
                    using var content = entry.Open(); using var bytes = new MemoryStream(checked((int)entry.Length));
                    byte[] buffer = new byte[64 * 1024]; long total = 0; uint crc = uint.MaxValue; int count;
                    while ((count = content.Read(buffer)) > 0)
                    {
                        token.ThrowIfCancellationRequested(); total += count;
                        if (total > entry.Length || total > MaxSelectedBytes) throw new InvalidDataException("An entry expanded beyond its declared size.");
                        bytes.Write(buffer, 0, count);
                        for (int i = 0; i < count; i++) crc = CrcTable[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
                    }
                    if (total != entry.Length || ~crc != entry.Crc32) throw new InvalidDataException($"ZIP integrity check failed for {entry.FullName}.");
                    byte[] data = bytes.ToArray(); string name = "vendor-files/" + SafeName(entry.FullName);
                    Add(name, data);
                    included.Add(new(entry.FullName, name, total, Convert.ToHexStringLower(SHA256.HashData(data)), DescribePe(data)));
                }
                token.ThrowIfCancellationRequested();
                Add("pack.json", JsonSerializer.SerializeToUtf8Bytes(new
                {
                    tool = "UamSoftwarePack", version = "0.1.0", createdUtc = DateTimeOffset.UtcNow,
                    sourceName = Path.GetFileName(source), sourceBytes = input.Length,
                    totalFiles = files.Count, selectedBytes = budget, included,
                    purpose = "Static USB-protocol investigation for UAM-05LP-T301. No scanner status was read and no vendor code was executed.",
                    limits = "26 MiB selected bytes, 30 MiB output; oversized/framework/private project files may be inventory-only. Consult inventory.json for omissions."
                }, new JsonSerializerOptions { WriteIndented = true }));
                Add("README.txt", Encoding.UTF8.GetBytes("Upload this UAM-analysis ZIP in the existing chat.\r\nIt contains vendor program files for static inspection and a full filename/size inventory.\r\nIt does not contain an actual scanner status check. No USB connection was opened and no vendor program was run.\r\nSome files are inventory-only to fit the attachment limit. No .hux scanner projects, user logs, databases or license files are selected.\r\nIf includedFiles is empty, send the ZIP anyway: the inventory identifies which installer or large application file is needed next.\r\n"));
                void Add(string name, byte[] bytes) { using var entry = pack.CreateEntry(name, CompressionLevel.Optimal).Open(); entry.Write(bytes); }
            }
            token.ThrowIfCancellationRequested();
            long length = new FileInfo(temporary).Length;
            if (length > MaxPackBytes) throw new InvalidDataException("The analysis ZIP exceeded the 30 MiB output limit; it was not published.");
            File.Move(temporary, destination);
            return new(destination, length, included.Count, files.Count);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static int Priority(string path)
    {
        string name = path.Replace('\\', '/').Split('/').Last(), extension = Path.GetExtension(name).ToLowerInvariant();
        if (Regex.IsMatch(name, "license|activation|credential|password|account|user[._-]?settings", RegexOptions.IgnoreCase)) return 100;
        if (Regex.IsMatch(name, "^(setup|unins|install|vcredist|vc_redist|dotnet|update)", RegexOptions.IgnoreCase)) return 100;
        if (Regex.IsMatch(name, "^(Qt[56]|System\\.|Microsoft\\.|WindowsBase\\.|Presentation|msvcp|vcruntime|api-ms-|ucrtbase|libcef|chrome|ffmpeg|icudt|Newtonsoft|Xceed)", RegexOptions.IgnoreCase)) return 100;
        if (extension is ".exe" or ".dll" or ".sys")
        {
            if (Regex.IsMatch(name, "uam|hokuyo", RegexOptions.IgnoreCase)) return 0;
            if (Regex.IsMatch(name, "usb|serial|protocol|comm|device|transport|monitor|status|sensor|error", RegexOptions.IgnoreCase)) return 1;
            return extension == ".exe" ? 2 : 5;
        }
        if (extension == ".inf" || name.EndsWith(".exe.config", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll.config", StringComparison.OrdinalIgnoreCase)) return 3;
        if (extension is ".resources" or ".resx") return 6;
        if (Regex.IsMatch(name, "error|status|message|alarm|english|lang", RegexOptions.IgnoreCase) && extension is ".xml" or ".csv" or ".ini" or ".json" or ".txt") return 7;
        if (extension == ".pdf" && Regex.IsMatch(name, "uam|manual|usb|communication|protocol", RegexOptions.IgnoreCase)) return 8;
        return 100;
    }

    private static string SafeName(string name)
    {
        string path = name.Replace('\\', '/').TrimEnd('/');
        if (path.Length is 0 or > 2000 || path.StartsWith('/')) throw new InvalidDataException("Invalid archive path.");
        foreach (string part in path.Split('/'))
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => c < 32 || ":<>\"|?*".Contains(c)) || Regex.IsMatch(part.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
                throw new InvalidDataException("Unsafe or nonportable archive path.");
        return path;
    }

    private static string DescribePe(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z') return "Not a PE executable (or text/resource file).";
        try
        {
            using var stream = new MemoryStream(bytes, false); using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return $"Native PE; machine {pe.PEHeaders.CoffHeader.Machine}.";
            var metadata = pe.GetMetadataReader();
            return metadata.IsAssembly ? $"Managed .NET assembly: {metadata.GetString(metadata.GetAssemblyDefinition().Name)}, version {metadata.GetAssemblyDefinition().Version}." : "Managed .NET module.";
        }
        catch (BadImageFormatException) { return "MZ header; not a readable PE image."; }
    }
}
