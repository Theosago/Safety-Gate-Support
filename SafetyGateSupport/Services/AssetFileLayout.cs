using System.IO;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

public static class AssetFileLayout
{
    public static string SafeName(string value)
    {
        var clean = new string(value.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length == 0) clean = "Unassigned";
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(clean.Split('.')[0], StringComparer.OrdinalIgnoreCase)) clean = "_" + clean;
        return clean.Length > 100 ? clean[..100] : clean;
    }
    public static string SuggestedRoot(SafetyGate gate) => "Assets/" + SafeName(gate.AssetKey == "Unassigned" ? $"Unassigned - {gate.ManufacturerDisplay} - {gate.Category}" : "Asset " + gate.AssetKey);
    public static string UnitName(SafetySystem unit) => SafeName(unit.UnitDisplay);
    public static string Category(ResourceDefinition r)
    {
        if (!string.IsNullOrWhiteSpace(r.StorageCategory)) return r.StorageCategory;
        if (AssignedDrawings.IsDrawing(r)) return "Electrical Drawings";
        if (r.LaunchAction.Equals("Siemens", StringComparison.OrdinalIgnoreCase) || r.Label.Contains("Light Config", StringComparison.OrdinalIgnoreCase)) return "Light Configurations";
        if (r.LaunchAction.Equals("UAM", StringComparison.OrdinalIgnoreCase) || r.Label.Contains("Scanner Config", StringComparison.OrdinalIgnoreCase)) return "Scanner Configurations";
        if (r.Label.Contains("PLC", StringComparison.OrdinalIgnoreCase)) return "PLC";
        return SafeName(r.Label.Replace(" (imported)", "", StringComparison.OrdinalIgnoreCase));
    }
    public static ResourceDefinition Create(SafetyGate gate, string category, string? id = null) => new()
    {
        Id = id ?? $"asset-{gate.Id}-{category.ToLowerInvariant().Replace(' ', '-')}", GateId = gate.Id,
        Label = category, Kind = "ManagedFiles", StorageMode = "Asset", StorageCategory = category, RelativeFolder = $"{gate.AssetFolder}/{category}",
        FileAssignments = new(StringComparer.OrdinalIgnoreCase), AllowUserUpload = true, ArchiveMode = "SameName",
        LaunchAction = category == "Light Configurations" ? "Siemens" : category == "Scanner Configurations" ? "UAM" : "None",
        SortOrder = category == "Electrical Drawings" ? 10 : category == "Light Configurations" ? 20 : category == "Scanner Configurations" ? 30 : 50,
        Description = "One active file location for this asset. Unit links share these files; replacing a file preserves its previous version in Archive."
    };
}
