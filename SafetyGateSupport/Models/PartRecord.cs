namespace SafetyGateSupport.Models;

public class PartRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PartNumber { get; set; } = "";
    public string Description { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Keywords { get; set; } = "";
    public string Notes { get; set; } = "";
    public string RelatedAssets { get; set; } = ""; // legacy display/search field
    public string DocumentPath { get; set; } = "";
    public List<string> AppliesToGateIds { get; set; } = new();
    public List<string> AppliesToUnitIds { get; set; } = new();

    public string Display => string.IsNullOrWhiteSpace(PartNumber) ? Description : $"{PartNumber} — {Description}";
    public bool AppliesTo(string gateId, string unitId) =>
        (AppliesToGateIds.Count == 0 && AppliesToUnitIds.Count == 0) ||
        AppliesToGateIds.Contains(gateId, StringComparer.OrdinalIgnoreCase) ||
        AppliesToUnitIds.Contains(unitId, StringComparer.OrdinalIgnoreCase);
}
