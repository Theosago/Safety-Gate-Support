using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using UglyToad.PdfPig;

namespace SafetyGateSupport.Services;

internal sealed record DriveFolder(string Id, string? Link = null, bool Pending = false);
internal sealed record DrivePdf(string Id, string? Checksum = null, string? PendingChecksum = null, string? Link = null);
internal sealed record SharedGuidePdf(string FileName, string Hash, string[] UnitIds);
internal sealed record AssignedGuidePdf(string Path, IReadOnlyList<string> UnitIds);

internal sealed class OperatorUnitPublication
{
    public string GateId { get; set; } = "";
    public DriveFolder? Folder { get; set; }
    public DrivePdf? Electrical { get; set; }
    public DrivePdf? Guide { get; set; }
    public DrivePdf? SpareParts { get; set; }
    public string? SparePartsFingerprint { get; set; }
    public string? SparePartsError { get; set; }
    public string? NotesId { get; set; }
    public string? NotesLink { get; set; }
    public SharedNotesCheckpoint? NotesCheckpoint { get; set; }
    public string? NotesError { get; set; }
    public string? PrimaryPdf { get; set; }
    public string? DrawingPairId { get; set; }
    public string? Fingerprint { get; set; }
    public string? Error { get; set; }
    public bool ImportedPilot { get; set; }
    public bool Paused { get; set; }
    public string? OriginalAssetName { get; set; }
    public string? OriginalUnitName { get; set; }
}

internal sealed class OperatorPublishingState
{
    internal const string DefaultFolderName = "Safety Gate Operator PDFs";
    internal static readonly string LocalDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SafetyGateSupport");
    internal static readonly string LegacyPath = Path.Combine(LocalDirectory, "operator-drive-pilot.json");
    internal static string StatePath(string root) => Path.Combine(root, "_AppData", "Operator Publishing", "publishing.json");
    internal static string AttachmentsPath(string root, string unitId) => Path.Combine(root, "_AppData",
        "Operator Publishing", "Guide PDFs", MobileSupportExporter.FileKey(unitId));
    private static string AssetGuideFolder(string root, string gateId) =>
        Path.Combine(root, "_AppData", "Operator Publishing", "Shared Guide PDFs", MobileSupportExporter.FileKey(gateId));

    internal static List<SharedGuidePdf> LoadGuidePdfs(string root, string gateId)
    {
        var records = Read<List<SharedGuidePdf>>(Path.Combine(AssetGuideFolder(root, gateId), "attachments.json")) ?? [];
        if (records.Any(pdf => pdf is null || string.IsNullOrWhiteSpace(pdf.FileName) ||
            Path.GetFileName(pdf.FileName) != pdf.FileName || pdf.FileName.Contains('\\') || pdf.FileName.Contains(':') ||
            !OperatorPublicationSources.IsPdf(pdf.FileName) || pdf.Hash is not { Length: 64 } ||
            pdf.Hash.Any(c => !char.IsAsciiHexDigit(c)) || pdf.UnitIds is not { Length: > 0 }))
            throw new InvalidDataException("The saved guide attachment index could not be read.");
        return records;
    }

    internal static IEnumerable<AssignedGuidePdf> GuidePdfsFor(string root, string gateId, string unitId)
    {
        string folder = AssetGuideFolder(root, gateId);
        var shared = LoadGuidePdfs(root, gateId)
            .Where(pdf => pdf.UnitIds.Contains(unitId, StringComparer.OrdinalIgnoreCase)).ToArray();
        var names = shared.Select(pdf => pdf.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var legacy = AttachmentsPath(root, unitId);
        if (Directory.Exists(legacy))
            foreach (string path in Directory.EnumerateFiles(legacy).Where(OperatorPublicationSources.IsPdf).Order(StringComparer.OrdinalIgnoreCase))
                if (!names.Contains(Path.GetFileName(path)))
                    yield return new(OperatorPublicationSources.RequireSharedFile(root, path), [unitId]);
        foreach (var pdf in shared.OrderBy(pdf => pdf.FileName, StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.Combine(folder, pdf.Hash, pdf.FileName);
            if (!File.Exists(path)) throw new IOException($"The guide PDF {pdf.FileName} is unavailable in the shared library.");
            yield return new(OperatorPublicationSources.RequireSharedFile(root, path), pdf.UnitIds);
        }
    }

    internal static void SaveGuidePdf(DataService data, string root, string gateId, IReadOnlyCollection<string> unitIds,
        string source, SharedGuidePdf? previous)
    {
        string name = Path.GetFileName(source);
        if (!OperatorPublicationSources.IsPdf(name)) throw new InvalidDataException("Choose a PDF for the unit guides.");
        byte[] bytes = OperatorPublicationSources.ReadPdf(source);
        using (var document = PdfDocument.Open(bytes))
            if (document.NumberOfPages == 0) throw new InvalidDataException("The selected PDF has no pages.");
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        using var lease = data.AcquireAssetWriteLock();
        if (!data.IsReady || !Path.GetFullPath(data.SharedRoot).Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Connect the original shared library before saving this PDF.");
        var eligible = data.LoadSystems().Where(unit => unit.GateId == gateId).Select(unit => unit.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (unitIds.Count == 0 || unitIds.Any(id => !eligible.Contains(id)))
            throw new InvalidOperationException("Choose the units covered by this PDF on the same asset.");
        var records = LoadGuidePdfs(root, gateId);
        var existing = records.SingleOrDefault(pdf => pdf.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing?.Hash != previous?.Hash || existing is not null && previous is not null &&
            !existing.UnitIds.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(previous.UnitIds))
            throw new IOException("This guide PDF changed while you were selecting units. Choose the PDF again.");

        string folder = AssetGuideFolder(root, gateId);
        string destination = Path.Combine(folder, hash, name);
        if (File.Exists(destination))
        {
            using var stored = File.OpenRead(OperatorPublicationSources.RequireSharedFile(root, destination));
            if (Convert.ToHexStringLower(SHA256.HashData(stored)) != hash)
                throw new IOException("The stored guide PDF differs from its saved revision.");
        }
        else WriteFile(destination, bytes);
        if (existing is not null) records.Remove(existing);
        records.Add(new(name, hash, unitIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        WriteFile(Path.Combine(folder, "attachments.json"), JsonSerializer.SerializeToUtf8Bytes(records));
    }

    public DriveFolder? Root { get; set; }
    public bool PublishAll { get; set; }
    public bool SyncSharedNotes { get; set; }
    public string? NotesError { get; set; }
    public Dictionary<string, DriveFolder> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, OperatorUnitPublication> Units { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? StockroomPlantId { get; set; }
    public Dictionary<string, OperatorStockroomScan> StockroomScans { get; set; } = [];
    public string? SparePartsCatalogError { get; set; }

    internal static OperatorPublishingState Load(string root) =>
        Read<OperatorPublishingState>(StatePath(root)) ?? new();

    internal bool ImportPilot(string root, string? path = null)
    {
        var pilot = Read<LegacyPilot>(path ?? LegacyPath);
        if (pilot is null || string.IsNullOrWhiteSpace(pilot.DriveRoot) ||
            !Path.TrimEndingDirectorySeparator(Path.GetFullPath(pilot.SharedRoot))
                .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase) ||
            Units.ContainsKey(pilot.UnitId))
            return false;
        if (Root is not null && Root.Id != pilot.DriveRoot)
            throw new InvalidOperationException("This library already uses a different Drive folder from the pilot. The original QR links are still in the pilot folder.");

        Root ??= new(pilot.DriveRoot);
        Units.Add(pilot.UnitId, new OperatorUnitPublication
        {
            GateId = pilot.GateId,
            Folder = pilot.FolderId is null ? null : new(pilot.FolderId, pilot.FolderLink),
            Electrical = pilot.FileId is not null ? new(pilot.FileId, pilot.LastHash, Link: pilot.Link)
                : pilot.PendingFileId is not null ? new(pilot.PendingFileId, PendingChecksum: pilot.LastHash) : null,
            PrimaryPdf = pilot.RelativePdf,
            NotesId = pilot.NotesId,
            NotesLink = pilot.NotesLink,
            ImportedPilot = true,
            Paused = pilot.Paused,
            OriginalAssetName = pilot.Asset,
            OriginalUnitName = pilot.Unit
        });
        return true;
    }

    internal void Save(string root) => WriteFile(StatePath(root), JsonSerializer.SerializeToUtf8Bytes(this));

    internal static void WriteFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream) ?? throw new IOException("Publishing settings could not be read.");
    }

    private sealed record LegacyPilot(string SharedRoot, string GateId, string UnitId, string Asset, string Unit,
        string RelativePdf, string DriveRoot, string? FileId, string? LastHash, string? Link, string? PendingFileId,
        bool Paused, string? FolderId, string? FolderLink, string? NotesId, string? NotesLink);
}
