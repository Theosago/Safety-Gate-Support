using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

internal sealed record OperatorPublicationUnit(SafetyGate Gate, SafetySystem Unit, string AssetFolder, string UnitFolder)
{
    internal string Label => $"{AssetFolder} / {UnitFolder}";
}

internal sealed record OperatorGuideSection(string Title, string Path, string? PairId = null, string? ReplacedPath = null, string? PairHash = null);

internal sealed record OperatorPublicationSources(OperatorGuideSection? Primary, IReadOnlyList<OperatorGuideSection> Sections,
    string? PhotoPath, string Fingerprint, bool PrimaryReassigned)
{
    internal const int MaximumPdfBytes = 80 * 1024 * 1024;

    internal static OperatorPublicationUnit[] ListUnits(DataService data)
    {
        var gates = data.LoadGates();
        var units = data.LoadSystems(gates);
        var sharedAssetNames = gates.GroupBy(g => AssetFileLayout.SafeName(g.AssetDisplay), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sharedUnitNames = units.GroupBy(u => (u.GateId, Name: AssetFileLayout.UnitName(u).ToUpperInvariant()))
            .Where(group => group.Count() > 1).SelectMany(group => group.Select(u => u.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byGate = gates.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);
        return units.Select(u =>
        {
            if (!byGate.TryGetValue(u.GateId, out var gate))
                throw new InvalidDataException($"{u.UnitDisplay} is missing its asset record.");
            string asset = AssetFileLayout.SafeName(gate.AssetDisplay);
            string unit = AssetFileLayout.UnitName(u);
            if (sharedAssetNames.Contains(asset)) asset += $" ({MobileSupportExporter.FileKey(gate.Id)[..8]})";
            if (sharedUnitNames.Contains(u.Id)) unit += $" ({MobileSupportExporter.FileKey(u.Id)[..8]})";
            return new OperatorPublicationUnit(gate, u, asset, unit);
        }).OrderBy(u => SortNumber(u.Gate.AssetNumber)).ThenBy(u => u.AssetFolder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => SortNumber(u.Unit.UnitNumber)).ThenBy(u => u.UnitFolder, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static int SortNumber(string? number) => int.TryParse(number, out int value) ? value : int.MaxValue;

    internal static OperatorPublicationSources Read(DataService data, OperatorPublicationUnit item,
        IReadOnlyList<ResourceDefinition> resources, OperatorUnitPublication published, CancellationToken token)
    {
        var applicable = data.ResourcesFor(item.Gate, item.Unit, resources)
            .Where(r => r.StorageMode != "Asset" || r.FileAssignments.Keys.Any(r.IncludesFile));
        var listing = AssignedDrawings.List(data, item.Gate, item.Unit, applicable, token);
        if (listing.Unavailable.Count > 0)
            throw new IOException("Some drawing folders are unavailable. Check the shared library connection.");
        var drawings = listing.Files.Where(f => IsPdf(f.Path)).Select(f =>
        {
            var pair = f.Resource?.DrawingPairs.FirstOrDefault(p => p.Pdf.Equals(Path.GetFileName(f.Path), StringComparison.OrdinalIgnoreCase));
            return new OperatorGuideSection(Path.GetFileNameWithoutExtension(f.Path), RequireSharedFile(data.SharedRoot, f.Path),
                pair?.Id, string.IsNullOrWhiteSpace(pair?.ReplacedPdf) ? null : Path.Combine(Path.GetDirectoryName(f.Path)!, pair.ReplacedPdf),
                pair?.PdfHash);
        }).ToList();
        string? previous = string.IsNullOrWhiteSpace(published.PrimaryPdf) ? null : data.Resolve(published.PrimaryPdf);
        var primary = drawings.FirstOrDefault(f => published.DrawingPairId is not null && f.PairId == published.DrawingPairId)
            ?? drawings.FirstOrDefault(f => previous is not null &&
                (f.Path.Equals(previous, StringComparison.OrdinalIgnoreCase) || string.Equals(f.ReplacedPath, previous, StringComparison.OrdinalIgnoreCase)));
        bool reassigned = primary is null && previous is not null &&
            IsAssignedElsewhere(data, item, resources, published.DrawingPairId, previous);
        if (primary is null && !reassigned && published.ImportedPilot && previous is not null && IsPdf(previous) && File.Exists(previous))
            primary = new(Path.GetFileNameWithoutExtension(previous), RequireSharedFile(data.SharedRoot, previous));
        if (primary is null && !reassigned && published.Electrical is not null && previous is not null)
            throw new IOException("The published electrical drawing is missing. Restore it or replace its pair through the app.");
        primary ??= drawings.FirstOrDefault();

        string? photo = null;
        if (!string.IsNullOrWhiteSpace(item.Unit.PrimaryPhoto))
        {
            string photoSource = data.Resolve(item.Unit.PrimaryPhoto);
            if (File.Exists(photoSource)) photo = RequireSharedFile(data.SharedRoot, photoSource);
        }
        string attachmentFolder = OperatorPublishingState.AttachmentsPath(data.SharedRoot, item.Unit.Id);
        var attachments = Directory.Exists(attachmentFolder)
            ? Directory.EnumerateFiles(attachmentFolder).Where(IsPdf).Order(StringComparer.OrdinalIgnoreCase)
                .Select(p => new OperatorGuideSection(Path.GetFileNameWithoutExtension(p), RequireSharedFile(data.SharedRoot, p)))
            : Enumerable.Empty<OperatorGuideSection>();
        var sections = drawings.Where(f => f != primary).Concat(attachments).ToList();
        if (primary is not null) sections.Insert(0, primary with { Title = "Electrical drawing" });
        var fingerprint = JsonSerializer.SerializeToUtf8Bytes(new
        {
            LayoutVersion = 2,
            item.Label,
            item.Gate.Category,
            item.Gate.Location,
            Manufacturer = item.Gate.ManufacturerDisplay,
            item.Gate.Model,
            item.Gate.EquipmentType,
            item.Unit.DisplayName,
            Photos = photo is null ? null : Stamp(photo),
            Pages = sections.Select(f => new { f.Title, f.PairId, f.PairHash, Source = Stamp(f.Path) })
        });
        return new(primary, sections, photo, Convert.ToHexString(SHA256.HashData(fingerprint)), reassigned);
    }

    // A recorded assignment to other units is different from an unavailable file. Follow the new
    // coverage only when the library still has the previous drawing indexed under this asset.
    private static bool IsAssignedElsewhere(DataService data, OperatorPublicationUnit item,
        IReadOnlyList<ResourceDefinition> resources, string? previousPairId, string previousPath)
    {
        foreach (var resource in resources.Where(r => r.GateId == item.Gate.Id && r.StorageMode == "Asset" && AssignedDrawings.IsDrawing(r)))
        {
            string folder = data.ResourceCurrentFolder(resource, item.Gate);
            var pair = resource.DrawingPairs.FirstOrDefault(p => previousPairId is not null && p.Id == previousPairId);
            bool sameFolder = Path.GetFullPath(folder).Equals(Path.GetDirectoryName(previousPath), StringComparison.OrdinalIgnoreCase);
            string oldName = Path.GetFileName(previousPath);
            if (pair is null && sameFolder)
                pair = resource.DrawingPairs.FirstOrDefault(p => p.ReplacedPdf.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            var scope = pair is not null ? resource.FileScope(pair.Pdf) : sameFolder ? resource.FileScope(oldName) : null;
            if (scope is not null)
                return !resource.AppliesTo(item.Unit.Id) || scope.Count > 0 && !scope.Contains(item.Unit.Id, StringComparer.OrdinalIgnoreCase);
        }
        return false;
    }

    internal static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private static object Stamp(string path)
    {
        var file = new FileInfo(path);
        return new { file.FullName, file.Length, Modified = file.LastWriteTimeUtc.Ticks };
    }

    internal static string RequireSharedFile(string root, string path)
    {
        string prefix = Path.GetFullPath(root);
        if (!Path.EndsInDirectorySeparator(prefix)) prefix += Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"The source file {Path.GetFileName(path)} is outside the shared library.");
        for (FileSystemInfo? current = new FileInfo(full); current is not null && current.FullName.Length >= prefix.Length;
             current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"The source file {Path.GetFileName(path)} uses a linked path.");
        return full;
    }

    internal static byte[] ReadPdf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 5 || stream.Length > MaximumPdfBytes)
            throw new IOException($"{Path.GetFileName(path)} must be a PDF up to 80 MiB.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (!bytes.AsSpan().StartsWith("%PDF-"u8))
            throw new IOException($"{Path.GetFileName(path)} does not have a PDF header.");
        return bytes;
    }
}
