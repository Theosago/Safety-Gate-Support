using System.IO;
using System.Text.Json;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

public partial class DataService
{
    public sealed record PairUploadResult(string PdfPath, string DwgPath, string ReviewNote, IReadOnlyList<string> UnitIds, bool Reused = false);

    public PairUploadResult SaveDrawingPair(ResourceDefinition view, SafetyGate gate, SafetySystem unit,
        IEnumerable<string> sources, string? replaceFile = null, IReadOnlyCollection<string>? unitIds = null,
        IReadOnlyCollection<string>? expectedReplacementUnits = null)
    {
        using var lease = AcquireAssetWriteLock();
        var files = sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length != 2 || files.Count(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) != 1 || files.Count(p => Path.GetExtension(p).Equals(".dwg", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidOperationException("Select exactly one PDF and its DWG companion. Neither file has been published; add the missing companion and try again.");
        if (view.StorageMode != "Asset") throw new InvalidOperationException("Run Setup → Shared Data / Software → Organize asset folders once before publishing paired drawing updates.");
        var records = LoadResources();
        var record = records.SingleOrDefault(r => r.Id == view.Id);
        if (record is null)
        {
            if (replaceFile is not null || string.IsNullOrWhiteSpace(gate.AssetFolder) ||
                view.Id != AssetFileLayout.Create(gate, "Electrical Drawings").Id)
                throw new InvalidOperationException("The drawing collection is unavailable. Refresh the unit and try again.");
            record = AssetFileLayout.Create(gate, "Electrical Drawings");
            records.Add(record);
        }
        if (AssetFileLayout.Category(record) != "Electrical Drawings") throw new InvalidOperationException("Use the electrical drawing collection for print pairs.");
        if (unit.GateId != gate.Id || record.GateId != gate.Id) throw new InvalidOperationException("The selected unit and drawing collection must belong to this asset.");
        var eligible = AssetFileUnits(gate, record);
        if (replaceFile is not null && unitIds is not null)
            throw new InvalidOperationException("Change unit links separately from a drawing replacement.");
        var scope = replaceFile is null ? ValidateFileUnits(unitIds ?? new[] { unit.Id }, eligible) : null;
        string pdfSource = files.Single(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        string dwgSource = files.Single(p => Path.GetExtension(p).Equals(".dwg", StringComparison.OrdinalIgnoreCase));
        string pdfHash = AssetStorageMigration.Hash(pdfSource), dwgHash = AssetStorageMigration.Hash(dwgSource);
        var identical = record.DrawingPairs.FirstOrDefault(p => p.PdfHash == pdfHash && p.DwgHash == dwgHash);
        string folder = ResourceCurrentFolder(record, gate); Directory.CreateDirectory(folder);
        if (scope is not null && identical is not null && File.Exists(Path.Combine(folder, identical.Pdf)) &&
            File.Exists(Path.Combine(folder, identical.Dwg)) && AssetStorageMigration.Hash(Path.Combine(folder, identical.Pdf)) == pdfHash &&
            AssetStorageMigration.Hash(Path.Combine(folder, identical.Dwg)) == dwgHash)
        {
            var prior = DrawingPairScope(record, identical.Pdf, identical);
            var combined = prior.Count == 0 ? prior : prior.Concat(scope).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!SameFileScope(prior, combined))
            {
                WriteFileScope(record, identical.Pdf, combined);
                WriteFileScope(record, identical.Dwg, combined);
                SaveResources(records);
                LogAudit("Drawing pair unit links added", gate.Id, unit.Id, record.Id, identical.Pdf + " + " + identical.Dwg);
            }
            view.FileAssignments = record.FileAssignments;
            view.DrawingPairs = record.DrawingPairs;
            string notice = WriteAssetFileIndex(gate);
            return new(Path.Combine(folder, identical.Pdf), Path.Combine(folder, identical.Dwg),
                string.Join(" ", new[] { identical.ReviewNote, notice }.Where(s => s.Length > 0)),
                ExpandFileScope(combined, eligible), Reused: true);
        }
        if (replaceFile is not null && !Path.GetFullPath(replaceFile).Equals(Path.GetFullPath(Path.Combine(folder, Path.GetFileName(replaceFile))), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a current print from this collection for replacement.");
        var previousPair = replaceFile is null ? null : record.DrawingPairs.FirstOrDefault(p => p.Contains(Path.GetFileName(replaceFile)));
        var oldNames = previousPair is not null ? new[] { previousPair.Pdf, previousPair.Dwg } : replaceFile is not null ? [Path.GetFileName(replaceFile)] : Array.Empty<string>();
        if (oldNames.Any(n => !record.ForUnit(view.ViewUnitId).IncludesFile(n)))
            throw new InvalidOperationException("Choose a print linked to the selected unit.");
        scope ??= DrawingPairScope(record, oldNames[0], previousPair);
        var affectedUnits = ExpandFileScope(scope, eligible);
        if (affectedUnits.Count == 0)
            throw new InvalidOperationException("Use Units covered to link this drawing to a current unit before replacing it.");
        if (expectedReplacementUnits is not null && !SameFileScope(affectedUnits, expectedReplacementUnits))
            throw new InvalidOperationException("The units covered by this drawing changed. Review the current drawing before replacing it.");
        var names = new[] { Path.GetFileName(pdfSource), Path.GetFileName(dwgSource) };
        foreach (string name in names)
            if (File.Exists(Path.Combine(folder, name)) && !oldNames.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new IOException("The filename already belongs to another current sheet: " + name + ". Select that sheet for replacement, or use distinct filenames for a separate print.");
        string review = previousPair is not null && (pdfHash == previousPair.PdfHash) != (dwgHash == previousPair.DwgHash)
            ? pdfHash == previousPair.PdfHash ? "DWG changed while the selected PDF content is unchanged." : "PDF changed while the selected DWG content is unchanged." : "";
        string transaction = Path.Combine(SharedRoot, "_AppData", "Drawing Updates", DateTime.Now.ToString("yyyy-MM-dd HHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(transaction);
        var originalMetadata = new[] { ResourcesFile, ManualsIndexFile, PartsFile }.Where(File.Exists).ToDictionary(p => p, File.ReadAllBytes);
        foreach (var pair in originalMetadata) File.WriteAllBytes(Path.Combine(transaction, Path.GetFileName(pair.Key)), pair.Value);
        string stagedPdf = Path.Combine(transaction, "incoming.pdf"), stagedDwg = Path.Combine(transaction, "incoming.dwg");
        AtomicCopy(pdfSource, stagedPdf); AtomicCopy(dwgSource, stagedDwg);
        if (AssetStorageMigration.Hash(stagedPdf) != pdfHash || AssetStorageMigration.Hash(stagedDwg) != dwgHash) throw new IOException("A selected source changed during upload. No pair was published.");
        string archive = Path.Combine(ResourceArchiveFolder(record, gate), DateTime.Now.ToString("yyyy-MM-dd HHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6]);
        var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in oldNames)
        {
            string old = Path.Combine(folder, name); if (!File.Exists(old)) continue;
            Directory.CreateDirectory(archive); string saved = Path.Combine(archive, name); File.Copy(old, saved, false); backups[old] = saved;
        }
        string pdfTarget = Path.Combine(folder, names[0]), dwgTarget = Path.Combine(folder, names[1]);
        var published = new List<string>(); var removed = new List<string>();
        try
        {
            AtomicCopy(stagedPdf, pdfTarget, File.Exists(pdfTarget)); published.Add(pdfTarget);
            AtomicCopy(stagedDwg, dwgTarget, File.Exists(dwgTarget)); published.Add(dwgTarget);
            foreach (string name in oldNames) record.FileAssignments.Remove(name);
            record.FileAssignments[names[0]] = scope.ToList(); record.FileAssignments[names[1]] = scope.ToList();
            if (previousPair is not null) record.DrawingPairs.Remove(previousPair);
            record.DrawingPairs.Add(new DrawingFilePair
            {
                Id = previousPair?.Id ?? Guid.NewGuid().ToString("N"), Pdf = names[0], Dwg = names[1],
                ReplacedPdf = previousPair?.ReplacedPdf ?? oldNames.FirstOrDefault(n => Path.GetExtension(n).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) ?? "",
                PdfHash = pdfHash, DwgHash = dwgHash, UploadedTogetherUtc = DateTimeOffset.UtcNow, ReviewNote = review
            });
            var manuals = LoadManualRecords(); var parts = LoadParts();
            foreach (string name in oldNames)
            {
                string old = Path.Combine(folder, name), replacement = Path.GetExtension(name).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? pdfTarget : dwgTarget;
                foreach (var manual in manuals.Where(m => Resolve(m.FilePath).Equals(old, StringComparison.OrdinalIgnoreCase))) manual.FilePath = ToRelative(replacement);
                foreach (var part in parts.Where(p => Resolve(p.DocumentPath).Equals(old, StringComparison.OrdinalIgnoreCase))) part.DocumentPath = ToRelative(replacement);
            }
            foreach (string name in oldNames.Where(n => !names.Contains(n, StringComparer.OrdinalIgnoreCase)))
            { string old = Path.Combine(folder, name); if (File.Exists(old)) { File.Delete(old); removed.Add(old); } }
            SaveResources(records); SaveManualRecords(manuals); SaveParts(parts);
        }
        catch
        {
            foreach (string target in published)
            {
                if (backups.TryGetValue(target, out var saved)) File.Copy(saved, target, true);
                else if (!oldNames.Contains(Path.GetFileName(target), StringComparer.OrdinalIgnoreCase) && File.Exists(target)) File.Delete(target);
            }
            foreach (string old in removed) File.Copy(backups[old], old, true);
            foreach (var pair in originalMetadata) File.WriteAllBytes(pair.Key, pair.Value);
            throw;
        }
        File.Delete(stagedPdf); File.Delete(stagedDwg);
        view.FileAssignments = record.FileAssignments; view.DrawingPairs = record.DrawingPairs;
        string indexNotice = WriteAssetFileIndex(gate);
        LogAudit("Drawing pair published", gate.Id, unit.Id, record.Id, names[0] + " + " + names[1]);
        return new(pdfTarget, dwgTarget, string.Join(" ", new[] { review, indexNotice }.Where(s => s.Length > 0)), affectedUnits);
    }

    private static List<string> DrawingPairScope(ResourceDefinition record, string name, DrawingFilePair? pair)
    {
        var scope = record.FileScope(name) ?? throw new InvalidOperationException("The selected sheet is no longer in the current index.");
        if (pair is not null && (record.FileScope(pair.Pdf) is not { } pdf || record.FileScope(pair.Dwg) is not { } dwg || !SameFileScope(pdf, dwg)))
            throw new InvalidOperationException("The PDF and DWG have different unit links. Use Units covered to save one selection for both.");
        return scope;
    }
}
