using System.IO;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

public partial class DataService
{
    public const string SignalCategory = "Light Configurations";

    /// <summary>
    /// Saves a signal column configuration (.signal) and, optionally, the PDF sheet that shows it.
    /// The PDF is stored beside the configuration under the same name so the two stay paired.
    /// With <paramref name="replacingSignal"/> the previous revision of both files is archived and
    /// every unit linked to the old configuration stays linked to the new one.
    /// </summary>
    public (string Signal, string? Pdf) SaveSignalPair(SafetyGate gate, SafetySystem unit, string signalSource, string? pdfSource, string? replacingSignal = null)
    {
        if (!Path.GetExtension(signalSource).Equals(".signal", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose a .signal configuration file.");
        if (pdfSource is not null && !Path.GetExtension(pdfSource).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The configuration sheet must be a PDF.");
        using var lease = AcquireAssetWriteLock();
        var view = replacingSignal is null ? null : LoadResources()
            .Where(r => r.GateId == gate.Id && r.StorageMode == "Asset" && AssetFileLayout.Category(r) == SignalCategory)
            .Select(r => r.ForUnit(unit.Id))
            .FirstOrDefault(r => Path.GetDirectoryName(Path.GetFullPath(replacingSignal))!.Equals(Path.GetFullPath(ResourceCurrentFolder(r, gate)).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                && r.IncludesFile(Path.GetFileName(replacingSignal)));

        string signal, oldPdf = replacingSignal is null ? "" : Path.ChangeExtension(replacingSignal, ".pdf");
        signal = view is not null ? ReplaceAssetFile(view, gate, replacingSignal!, signalSource) : AssignAssetFile(gate, unit, signalSource, SignalCategory);
        if (pdfSource is null) return (signal, File.Exists(Path.ChangeExtension(signal, ".pdf")) ? Path.ChangeExtension(signal, ".pdf") : null);

        // Stage the PDF under the configuration's name so the pair is recognised wherever it is shown.
        string staging = Path.Combine(Path.GetTempPath(), "SafetyGateSupport-signal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string staged = Path.Combine(staging, Path.GetFileNameWithoutExtension(signal) + ".pdf");
            File.Copy(pdfSource, staged);
            string pdf;
            if (view is not null && File.Exists(oldPdf) && view.IncludesFile(Path.GetFileName(oldPdf)) && FindAssignmentKey(view, Path.GetFileName(oldPdf)) is not null)
                pdf = ReplaceAssetFile(view, gate, oldPdf, staged);
            else
            {
                pdf = AssignAssetFile(gate, unit, staged, SignalCategory);
                if (view is not null) CopyFileScope(gate, Path.GetFileName(signal), Path.GetFileName(pdf));
            }
            return (signal, pdf);
        }
        finally { try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static string? FindAssignmentKey(ResourceDefinition resource, string name) =>
        resource.FileAssignments.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    // A new sheet for a shared configuration covers the same units as the configuration.
    private void CopyFileScope(SafetyGate gate, string from, string to)
    {
        var resources = LoadResources();
        var resource = resources.FirstOrDefault(r => r.GateId == gate.Id && r.StorageMode == "Asset" && AssetFileLayout.Category(r) == SignalCategory);
        if (resource is null || FindAssignmentKey(resource, from) is not { } source || FindAssignmentKey(resource, to) is not { } target) return;
        resource.FileAssignments[target] = resource.FileAssignments[source].ToList();
        SaveResources(resources); WriteAssetFileIndex(gate);
    }
}
