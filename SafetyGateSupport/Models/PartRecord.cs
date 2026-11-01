using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
    public string PhotoPath { get; set; } = "";
    public string BinLocation { get; set; } = "";
    public string Site { get; set; } = "";
    public decimal? ReportedQuantity { get; set; }
    public string DocumentPath { get; set; } = "";
    public List<string> AppliesToGateIds { get; set; } = new();
    public List<string> AppliesToUnitIds { get; set; } = new();

    private string NoteValue(string label) => Regex.Match(Notes ?? "", @"(?im)^" + label + @":[ \t]*([^\r\n]*)", RegexOptions.None, TimeSpan.FromMilliseconds(100)).Groups[1].Value.Trim();
    [JsonIgnore] public string LocationValue => string.IsNullOrWhiteSpace(BinLocation) ? NoteValue("(?:Bin location|Bin|Location)") : BinLocation;
    [JsonIgnore] public string LocationDisplay => LocationValue.Length == 0 ? "Location not supplied" : LocationValue;
    [JsonIgnore] public string SiteValue => string.IsNullOrWhiteSpace(Site) ? NoteValue("Site") : Site;
    [JsonIgnore] public decimal? QuantityValue => ReportedQuantity ?? (decimal.TryParse(Regex.Match(NoteValue("Reported quantity"), @"^[\d,.]+").Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null);
    public static string FormatQuantity(decimal quantity) => quantity.ToString("0.############################", CultureInfo.InvariantCulture);
    [JsonIgnore] public string QuantityDisplay => QuantityValue is { } quantity ? FormatQuantity(quantity) : "Not supplied";
    [JsonIgnore] public string NotesDisplay => string.Join(Environment.NewLine, (Notes ?? "").Split('\n').Where(line => !Regex.IsMatch(line, @"^(?:Bin location|Bin|Location|Reported quantity|Site):", RegexOptions.IgnoreCase)));
    [JsonIgnore] public string Display => $"{Description} • {LocationDisplay}" + (string.IsNullOrWhiteSpace(PartNumber) ? "" : $" ({PartNumber})");
    public bool AppliesTo(string gateId, string unitId) =>
        (AppliesToGateIds.Count == 0 && AppliesToUnitIds.Count == 0) ||
        AppliesToGateIds.Contains(gateId, StringComparer.OrdinalIgnoreCase) ||
        AppliesToUnitIds.Contains(unitId, StringComparer.OrdinalIgnoreCase);
}
