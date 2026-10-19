namespace SafetyGateSupport.Models;

public class SafetyGate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Category { get; set; } = "906 / Outside";
    public string AssetNumber { get; set; } = "";
    public string Manufacturer { get; set; } = "Safe2Cross";
    public string Model { get; set; } = "";
    public string Location { get; set; } = "";
    public string EquipmentType { get; set; } = "";
    public string Keywords { get; set; } = "";

    public string AssetKey => string.IsNullOrWhiteSpace(AssetNumber) ? "Unassigned" : AssetNumber.Trim();
    public string AssetDisplay => AssetKey == "Unassigned" ? "Unassigned" : $"Asset {AssetKey}";
    public string ManufacturerDisplay => string.IsNullOrWhiteSpace(Manufacturer) ? "Unspecified" : Manufacturer.Trim();
    public string Display => $"{AssetDisplay} - {ManufacturerDisplay}";
    public string DetailLine => string.Join(" • ", new[] { Category, Model, Location }.Where(x => !string.IsNullOrWhiteSpace(x)));
}
