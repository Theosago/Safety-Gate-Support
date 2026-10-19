namespace SafetyGateSupport.Models;

public class GlobalSearchResult
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Target { get; set; } = "";
    public string SystemId { get; set; } = "";
    public string Body { get; set; } = "";
    public string MatchSource { get; set; } = "";
    public string MatchText { get; set; } = "";

    public string Display => string.IsNullOrWhiteSpace(Subtitle) ? Title : $"{Title}    —    {Subtitle}";
}
