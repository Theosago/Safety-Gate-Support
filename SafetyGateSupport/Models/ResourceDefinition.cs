namespace SafetyGateSupport.Models;

public class ResourceDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GateId { get; set; } = "";
    public string Label { get; set; } = "Resource";
    // ManagedFiles, Manuals, Parts, Photos, Folder, Url
    public string Kind { get; set; } = "ManagedFiles";
    public string RelativeFolder { get; set; } = "";
    public string Target { get; set; } = "";
    public List<string> AppliesToUnitIds { get; set; } = new();
    public bool IsVisible { get; set; } = true;
    public bool AllowUserUpload { get; set; }
    // SameExtension, SameName, All, None
    public string ArchiveMode { get; set; } = "SameExtension";
    // None, Siemens, UAM
    public string LaunchAction { get; set; } = "None";
    public string Keywords { get; set; } = "";
    public string Description { get; set; } = "";
    public int SortOrder { get; set; } = 100;

    public string AdminDisplay => $"{Label}  [{Kind}]";
    public bool AppliesTo(string unitId) => AppliesToUnitIds.Count == 0 || AppliesToUnitIds.Contains(unitId, StringComparer.OrdinalIgnoreCase);
}
