namespace SafetyGateSupport.Models;

public class HelpItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Section { get; set; } = "Quick Help";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Keywords { get; set; } = "";
    public int SortOrder { get; set; }

    public string AdminDisplay => $"[{Section}] {Title}";
}
