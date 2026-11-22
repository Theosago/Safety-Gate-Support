using System.IO;
using System.Text.Json;

namespace SafetyGateSupport.Services;

internal sealed record DriveFolder(string Id, string? Link = null, bool Pending = false);
internal sealed record DrivePdf(string Id, string? Checksum = null, string? PendingChecksum = null, string? Link = null);

internal sealed class OperatorUnitPublication
{
    public string GateId { get; set; } = "";
    public DriveFolder? Folder { get; set; }
    public DrivePdf? Electrical { get; set; }
    public DrivePdf? Guide { get; set; }
    public string? NotesId { get; set; }
    public string? NotesLink { get; set; }
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

    public DriveFolder? Root { get; set; }
    public bool PublishAll { get; set; }
    public Dictionary<string, DriveFolder> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, OperatorUnitPublication> Units { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
