using System.IO;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

public static class DrawingPairStatus
{
    public sealed record Status(string Message, bool NeedsAttention, string? DwgPath);
    public static Status Check(DataService data, SafetyGate gate, ResourceDefinition resource, string file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string folder = data.ResourceCurrentFolder(resource, gate), name = Path.GetFileName(file);
        var pair = resource.DrawingPairs.FirstOrDefault(p => p.Contains(name));
        if (pair is null)
        {
            string extension = Path.GetExtension(name).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? ".dwg" : ".pdf";
            string sibling = Path.Combine(folder, Path.GetFileNameWithoutExtension(name) + extension);
            return File.Exists(sibling) ? new("PDF and DWG are present, but no coordinated pair upload is recorded. Upload both together to establish a revision baseline.", true, extension == ".dwg" ? sibling : file)
                : new($"{extension.ToUpperInvariant()[1..]} companion missing. Add the PDF and DWG together for this print.", true, extension == ".dwg" ? sibling : file);
        }
        string pdf = Path.Combine(folder, pair.Pdf), dwg = Path.Combine(folder, pair.Dwg);
        if (!File.Exists(pdf)) return new("PDF companion missing. Upload both the PDF and DWG together.", true, File.Exists(dwg) ? dwg : null);
        if (!File.Exists(dwg)) return new("DWG companion missing. Upload both the PDF and DWG together.", true, dwg);
        var pdfScope = resource.FileAssignments.FirstOrDefault(p => p.Key.Equals(pair.Pdf, StringComparison.OrdinalIgnoreCase)).Value;
        var dwgScope = resource.FileAssignments.FirstOrDefault(p => p.Key.Equals(pair.Dwg, StringComparison.OrdinalIgnoreCase)).Value;
        if (pdfScope is null || dwgScope is null || !pdfScope.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(dwgScope.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            return new("PDF and DWG have different unit assignments. Use Applies to units to set both companions to the units covered by this print.", true, dwg);
        string pdfHash = AssetStorageMigration.Hash(pdf); token.ThrowIfCancellationRequested();
        string dwgHash = AssetStorageMigration.Hash(dwg); token.ThrowIfCancellationRequested();
        bool changedPdf = pdfHash != pair.PdfHash, changedDwg = dwgHash != pair.DwgHash;
        if (changedDwg && !changedPdf) return new("DWG changed, but the PDF has not. Upload the corresponding PDF and DWG together; the displayed PDF may be out of date.", true, dwg);
        if (changedPdf && !changedDwg) return new("PDF changed, but the DWG has not. Upload the corresponding DWG and PDF together.", true, dwg);
        if (changedPdf || changedDwg) return new("Both files changed outside a recorded pair upload. Upload them together to record the intended revision.", true, dwg);
        if (pair.UploadedTogetherUtc is null) return new("PDF/DWG companions found. Matching revisions have not been confirmed by a paired upload.", true, dwg);
        if (pair.ReviewNote.Length > 0) return new(pair.ReviewNote + " Both files were submitted together; verify the export is intentional.", true, dwg);
        return new($"PDF + DWG uploaded together {pair.UploadedTogetherUtc.Value.LocalDateTime:g}. File checks do not verify that the PDF was plotted from this DWG.", false, dwg);
    }
}
