using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SafetyGateSupport.Services;

public sealed record LibraryFile(string FullPath, string RelativePath, string SourceName, string ImportId)
{
    public string Name => Path.GetFileName(RelativePath);
    public string Kind => Path.GetExtension(RelativePath).ToLowerInvariant() switch
    { ".signal" => "Light configuration", ".hux" => "Scanner configuration", ".pdf" or ".docx" or ".doc" => "Document", ".exe" => "Vendor application", _ => "File" };
    public string Display => $"{Kind} • {RelativePath} [{SourceName}]";
}
public sealed record LibraryImportResult(bool AlreadyImported, int FileCount, string SourceName);

/// <summary>Explicit local imports only. Content-addressed, no overwrite, no execution or automatic unit assignment.</summary>
public static class SupportLibraryService
{
    private const long MaxSourceBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxEntryBytes = 512L * 1024 * 1024;
    private const long MaxExpandedBytes = 4L * 1024 * 1024 * 1024;
    private const int MaxEntries = 30000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    { uint c = (uint)n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320U ^ (c >> 1) : c >> 1; return c; }).ToArray();
    private sealed record Manifest(string SourceName, string Sha256, DateTimeOffset ImportedUtc, List<string> Files);
    public static string Root(DataService data) => Path.Combine(data.SharedRoot, "Library", "Imports");

    public static LibraryImportResult Import(string root, string source, CancellationToken token = default, Action<string>? progress = null)
    {
        source = Path.GetFullPath(source); root = Path.GetFullPath(root);
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".zip" or ".pdf" or ".docx" or ".signal" or ".hux"))
            throw new NotSupportedException("Import PDF, DOCX, .signal, .hux, or ZIP files. Portable software must be supplied as a complete ZIP folder.");
        CheckPath(root); Directory.CreateDirectory(root); CheckPath(root);
        string staging = Path.Combine(root, ".import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            token.ThrowIfCancellationRequested();
            string snapshot = Path.Combine(staging, "source" + extension);
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (input.Length > MaxSourceBytes) throw new InvalidDataException("The source exceeds the 2 GiB import limit.");
                CopyBounded(input, output, input.Length, MaxSourceBytes, token);
            }
            string hash;
            using (var file = File.OpenRead(snapshot)) hash = Convert.ToHexStringLower(SHA256.HashData(file));
            string final = Path.Combine(root, hash);
            if (Directory.Exists(final))
            {
                CheckPath(final);
                var previous = ReadManifest(final);
                if (previous.Sha256 != hash) throw new InvalidDataException("Existing import metadata does not match its content ID.");
                return new(true, previous.Files.Count, previous.SourceName);
            }
            string filesRoot = Path.Combine(staging, "files"); Directory.CreateDirectory(filesRoot);
            var files = new List<string>();
            if (extension == ".zip")
            {
                using var archive = ZipFile.OpenRead(snapshot);
                if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("The ZIP exceeds the 30,000-entry limit.");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string relative = SafeRelative(entry.FullName);
                    if (!names.Add(relative)) throw new InvalidDataException("The ZIP contains duplicate paths (including case-only duplicates).");
                    if (entry.IsEncrypted || ((entry.ExternalAttributes >> 16) & 0xf000) is 0xa000)
                        throw new InvalidDataException("Encrypted ZIP entries and symbolic links are unsupported.");
                    string destination = Path.Combine(filesRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    bool directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                    if (directory) { Directory.CreateDirectory(destination); continue; }
                    if (entry.Length > MaxEntryBytes || entry.Length < 0 || (total += entry.Length) > MaxExpandedBytes)
                        throw new InvalidDataException("The ZIP exceeds the 512 MiB per-file or 4 GiB expanded limit.");
                    progress?.Invoke(entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var input = entry.Open();
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    uint crc = CopyBounded(input, output, entry.Length, MaxEntryBytes, token);
                    if (crc != entry.Crc32) throw new InvalidDataException($"ZIP integrity check failed: {entry.FullName}");
                    files.Add(relative);
                }
            }
            else
            {
                string name = SafeRelative(Path.GetFileName(source));
                File.Copy(snapshot, Path.Combine(filesRoot, name)); files.Add(name);
            }
            token.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(new Manifest(Path.GetFileName(source), hash, DateTimeOffset.UtcNow, files), JsonOptions));
            // Atomic publication: an interrupted/invalid import never appears in the library.
            Directory.Move(staging, final);
            return new(false, files.Count, Path.GetFileName(source));
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public static List<LibraryFile> List(string root, CancellationToken token = default)
    {
        var result = new List<LibraryFile>();
        if (!Directory.Exists(root)) return result;
        CheckPath(root);
        foreach (var folder in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            string id = Path.GetFileName(folder);
            if (!Regex.IsMatch(id, "^[a-f0-9]{64}$")) continue;
            CheckPath(folder);
            var manifest = ReadManifest(folder);
            if (manifest.Sha256 != id) throw new InvalidDataException("Import metadata differs from its folder ID.");
            foreach (string name in manifest.Files)
            {
                string relative = SafeRelative(name);
                string full = Path.Combine(folder, "files", relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) { CheckPath(full); result.Add(new(full, relative, manifest.SourceName, id)); }
            }
        }
        return result;
    }

    private static Manifest ReadManifest(string folder)
    {
        string path = Path.Combine(folder, "manifest.json"); CheckPath(path);
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Import metadata is too large.");
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing import metadata.");
        if (manifest.Files is null || manifest.Files.Count > MaxEntries || manifest.SourceName is null)
            throw new InvalidDataException("Invalid import metadata.");
        return manifest;
    }

    private static string SafeRelative(string path)
    {
        var relative = path.Replace('\\', '/').TrimEnd('/');
        if (relative.Length == 0 || relative.Length > 2000 || relative.StartsWith('/')) throw new InvalidDataException("Invalid ZIP path.");
        foreach (string part in relative.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || ":<>\"|?*".Contains(c)) ||
                Regex.IsMatch(part.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
                throw new InvalidDataException("The ZIP contains an unsafe or nonportable path.");
        }
        return relative;
    }

    private static uint CopyBounded(Stream input, Stream output, long expected, long maximum, CancellationToken token)
    {
        byte[] buffer = new byte[128 * 1024]; long total = 0; uint crc = uint.MaxValue;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested(); total += read;
            if (total > maximum || total > expected) throw new InvalidDataException("File expansion exceeded the declared size.");
            output.Write(buffer, 0, read);
            for (int i = 0; i < read; i++) crc = CrcTable[(crc ^ buffer[i]) & 0xff] ^ (crc >> 8);
        }
        if (total != expected) throw new InvalidDataException("File size differs from the declared size.");
        return ~crc;
    }

    // Reject reparse points throughout existing paths, including pre-existing shared-library ancestors.
    private static void CheckPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Import paths must not contain symbolic links or reparse points.");
    }
}
