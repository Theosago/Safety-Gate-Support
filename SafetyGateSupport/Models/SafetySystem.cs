namespace SafetyGateSupport.Models;

// A SafetySystem record now represents one UNIT. Gate-level information lives in gates.json.
// Legacy gate fields are retained for migration/backward compatibility and are synchronized from SafetyGate.
public class SafetySystem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GateId { get; set; } = "";
    public string? UnitNumber { get; set; }
    public string DisplayName { get; set; } = "";
    public int? SelectedPhotoSourcePage { get; set; }
    public string PrimaryPhoto { get; set; } = "";
    public string SystemFolder { get; set; } = "";
    public string PhotosFolder { get; set; } = "";
    public string NotesFolder { get; set; } = "";

    // Legacy fields retained so old shared JSON remains readable.
    public string Category { get; set; } = "906 / Outside";
    public string? AssetNumber { get; set; }
    public string GateBrand { get; set; } = "Safe2Cross";
    public string EquipmentType { get; set; } = "";
    public List<int> SourceHeaderPages { get; set; } = new();
    public string DrawingsFolder { get; set; } = "";
    public string LightConfigFolder { get; set; } = "";
    public string ScannerConfigFolder { get; set; } = "";
    public string ManualsFolder { get; set; } = "";
    public string PartsFolder { get; set; } = "";
    public bool ShowElectricalDrawings { get; set; } = true;
    public bool ShowLightConfiguration { get; set; } = true;
    public bool ShowScannerConfiguration { get; set; } = true;
    public bool ShowManuals { get; set; } = true;
    public bool ShowPartsBom { get; set; } = true;
    public bool ShowPhotos { get; set; } = true;
    public List<string> DataWarnings { get; set; } = new();
    public RepairInfo Repair { get; set; } = new();

    public string AssetKey => string.IsNullOrWhiteSpace(AssetNumber) ? "Unassigned" : AssetNumber;
    public string AssetDisplay => AssetKey == "Unassigned" ? "Unassigned" : $"Asset {AssetKey}";
    public string GateBrandDisplay => string.IsNullOrWhiteSpace(GateBrand) ? "Unspecified" : GateBrand.Trim();
    public string GateDisplay => $"{AssetDisplay} - {GateBrandDisplay}";
    public string UnitDisplay => $"Unit {(string.IsNullOrWhiteSpace(UnitNumber) ? "Unknown" : UnitNumber)}";
    public string AdminDisplay => $"{GateDisplay} / {UnitDisplay}";
}

public class RepairInfo
{
    public string RepairNeeded { get; set; } = "";
    public string RepairWork { get; set; } = "";
    public string PartsUsed { get; set; } = "";
}
