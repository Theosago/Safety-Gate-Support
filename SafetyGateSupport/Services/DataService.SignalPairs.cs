using System.IO;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

public partial class DataService
{
    public const string SignalCategory = "Light Configurations";

    /// <summary>
    /// Saves a signal column configuration (.signal) together with the PDF sheet that shows it.
    /// The PDF is stored beside the configuration under the same name so the two stay paired.
    /// With <paramref name="replacingSignal"/> the previous revision of both files is archived and
    /// every unit linked to the old configuration stays linked to the new one.
    /// <paramref name="unitIds"/> are the units the pair covers (organised asset libraries only).
    /// </summary>
    public (string Signal, string Pdf) SaveSignalPair(SafetyGate gate, SafetySystem unit, string signalSource, string pdfSource,
        string? replacingSignal = null, IReadOnlyCollection<string>? unitIds = null)
    {
        if (!Path.GetExtension(signalSource).Equals(".signal", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose a .signal configuration file.");
        if (pdfSource is null || !Path.GetExtension(pdfSource).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose the PDF that shows this configuration as well. A configuration is saved as a .signal and PDF pair.");
        using var lease = AcquireAssetWriteLock();
        var view = replacingSignal is null ? null : LoadResources()
            .Where(r => r.GateId == gate.Id && r.StorageMode == "Asset" && AssetFileLayout.Category(r) == SignalCategory)
            .Select(r => r.ForUnit(unit.Id))
            .FirstOrDefault(r => Path.GetDirectoryName(Path.GetFullPath(replacingSignal))!.Equals(Path.GetFullPath(ResourceCurrentFolder(r, gate)).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                && r.IncludesFile(Path.GetFileName(replacingSignal)));

        string signal, oldPdf = replacingSignal is null ? "" : Path.ChangeExtension(replacingSignal, ".pdf");
        signal = view is not null ? ReplaceAssetFile(view, gate, replacingSignal!, signalSource) : AssignAssetFile(gate, unit, signalSource, SignalCategory);

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
            if (unitIds is { Count: > 0 } && LoadResources().FirstOrDefault(r => r.GateId == gate.Id && r.StorageMode == "Asset" && AssetFileLayout.Category(r) == SignalCategory) is { } light)
                foreach (string name in new[] { signal, pdf }) SetAssetFileUnits(light, gate, Path.GetFileName(name), unitIds);
            return (signal, pdf);
        }
        finally { try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>The units a pair can cover, and those the given configuration covers now.</summary>
    public (IReadOnlyList<SafetySystem> Eligible, IReadOnlyList<string> Linked) SignalPairUnits(SafetyGate gate, string? signalPath)
    {
        var light = LoadResources().FirstOrDefault(r => r.GateId == gate.Id && r.StorageMode == "Asset" && AssetFileLayout.Category(r) == SignalCategory);
        var eligible = light is null
            ? LoadSystems().Where(u => u.GateId == gate.Id).OrderBy(u => int.TryParse(u.UnitNumber, out int n) ? n : int.MaxValue).ThenBy(u => u.UnitDisplay, StringComparer.OrdinalIgnoreCase).ToList()
            : AssetFileUnits(gate, light);
        IReadOnlyList<string> linked = [];
        if (light is not null && signalPath is not null && light.FileScope(Path.GetFileName(signalPath)) is { } scope)
            linked = scope.Count == 0 ? eligible.Select(u => u.Id).ToArray() : scope.ToArray();
        return (eligible, linked);
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
