namespace SafetyGateSupport.Models;

public class DrawingFilePair
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Pdf { get; set; } = "";
    public string Dwg { get; set; } = "";
    public string PdfHash { get; set; } = "";
    public string DwgHash { get; set; } = "";
    public DateTimeOffset? UploadedTogetherUtc { get; set; }
    public string ReviewNote { get; set; } = "";
    public bool Contains(string filename) => Pdf.Equals(filename, StringComparison.OrdinalIgnoreCase) || Dwg.Equals(filename, StringComparison.OrdinalIgnoreCase);
}
