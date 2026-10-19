namespace SafetyGateSupport.Models;

public sealed class SearchIndexEntry
{
    public string Title { get; set; } = "";
    public string BaseSubtitle { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Target { get; set; } = "";
    public string SystemId { get; set; } = "";
    public string Body { get; set; } = "";
    public string SearchText { get; set; } = "";
    public string NotesText { get; set; } = "";
}
