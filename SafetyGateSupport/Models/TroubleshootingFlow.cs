namespace SafetyGateSupport.Models;

public class TroubleshootingFlow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New troubleshooting flow";
    public string Keywords { get; set; } = "";
    public string ManufacturerFilter { get; set; } = "";
    public List<string> AppliesToGateIds { get; set; } = new();
    public List<string> AppliesToUnitIds { get; set; } = new();
    public string StartNodeId { get; set; } = "";
    public List<TroubleshootingNode> Nodes { get; set; } = new();

    public string AdminDisplay => Title;
    public bool AppliesTo(SafetyGate gate, SafetySystem unit)
    {
        if (AppliesToGateIds.Count > 0 || AppliesToUnitIds.Count > 0)
            return AppliesToGateIds.Contains(gate.Id, StringComparer.OrdinalIgnoreCase) || AppliesToUnitIds.Contains(unit.Id, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ManufacturerFilter) && !gate.Manufacturer.Contains(ManufacturerFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }
}

public class TroubleshootingNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Prompt { get; set; } = "";
    public string Body { get; set; } = "";
    public bool IsTerminal { get; set; }
    public List<string> ResourceIds { get; set; } = new();
    public List<TroubleshootingChoice> Choices { get; set; } = new();
    public string AdminDisplay => string.IsNullOrWhiteSpace(Prompt) ? Id : Prompt;
}

public class TroubleshootingChoice
{
    public string Text { get; set; } = "Next";
    public string NextNodeId { get; set; } = "";
}
