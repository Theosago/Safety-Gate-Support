namespace SafetyGateSupport.Models;

public class HelpItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Section { get; set; } = "Quick Help";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Keywords { get; set; } = "";
    public string PhotoPath { get; set; } = "";
    public List<string> PhotoPaths { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Photos => (PhotoPaths.Count > 0 ? PhotoPaths : new[] { PhotoPath }.ToList()).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public bool Visible { get; set; } = true;
    public int SortOrder { get; set; }

    public string AdminDisplay => $"[{Section}] {Title}";
}
