namespace SafetyGateSupport.Models;

public class ManualRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Keywords { get; set; } = "";
    public string FilePath { get; set; } = "";
    public List<string> AppliesToGateIds { get; set; } = new();
    public List<string> AppliesToUnitIds { get; set; } = new();

    public string Display => string.IsNullOrWhiteSpace(Manufacturer) ? Title : $"{Title} — {Manufacturer}";
    public bool AppliesTo(string gateId, string unitId) =>
        (AppliesToGateIds.Count == 0 && AppliesToUnitIds.Count == 0) ||
        AppliesToGateIds.Contains(gateId, StringComparer.OrdinalIgnoreCase) ||
        AppliesToUnitIds.Contains(unitId, StringComparer.OrdinalIgnoreCase);
}
