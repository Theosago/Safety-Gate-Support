using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using QRCoder;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

/// <summary>Prepare local printable guides and QR labels; never uploads or publishes.</summary>
public static class MobileSupportExporter
{
    public sealed record Result(int Guides, int Labels, IReadOnlyList<string> Warnings);
    public static string FileKey(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant()[..24];
    public static bool ValidLink(string link) => Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == "https" && !uri.IsLoopback && uri.UserInfo.Length == 0;
    public static string OfflinePayload(SafetyGate gate, SafetySystem unit, DateTimeOffset date) =>
        $"{gate.AssetDisplay} / {unit.UnitDisplay}\n{gate.Location}\n{unit.MobileOfflineInstructions.Trim()}\nGuide snapshot: {date:yyyy-MM-dd}. Follow the approved site procedure.";
    public static string QrSvg(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var code = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        int n = code.ModuleMatrix.Count;
        var svg = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {n} {n}\" role=\"img\" aria-label=\"Support QR code\" shape-rendering=\"crispEdges\"><rect width=\"{n}\" height=\"{n}\" fill=\"white\"/><g fill=\"black\">");
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) if (code.ModuleMatrix[y][x]) svg.Append($"<rect x=\"{x}\" y=\"{y}\" width=\"1\" height=\"1\"/>");
        return svg.Append("</g></svg>").ToString();
    }
    public static Result Export(DataService data, string destination, bool includeNotes = false, Func<string, byte[]?>? photoEncoder = null, string? unitId = null)
    {
        var units = data.LoadSystems();
        if (unitId is not null)
        {
            units = units.Where(unit => unit.Id == unitId).ToList();
            if (units.Count == 0) throw new InvalidOperationException("The saved pilot unit is no longer in the library.");
        }
        var gates = data.LoadGates(); var parts = data.LoadParts();
        var problems = data.LoadHelpItems().Where(p => p.Visible && p.Section.Equals("Common Problems", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.SortOrder).ThenBy(p => p.Title).ToList();
        if (units.Count == 0) throw new InvalidOperationException("No units are configured.");
        foreach (var unit in units)
        {
            if (unit.MobileSupportUrl.Length > 0 && !ValidLink(unit.MobileSupportUrl)) throw new InvalidOperationException($"{unit.AdminDisplay}: enter a valid HTTPS document link or clear it.");
            if (unit.MobileOfflineInstructions.Length > 700) throw new InvalidOperationException($"{unit.AdminDisplay}: offline QR instructions must be at most 700 characters.");
        }
        var warnings = new List<string>(); var pages = new List<(string Name, string Body)>(); long totalCharacters = 0;
        var date = DateTimeOffset.Now; var index = new StringBuilder(); var labels = new StringBuilder(); int labelCount = 0;
        string H(string? value) => WebUtility.HtmlEncode(value ?? "");
        string Image(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            // Embed only managed shared-folder files; never follow a link out of the shared tree.
            string root = Path.GetFullPath(data.SharedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string source = Path.GetFullPath(data.Resolve(path));
            if (!source.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { warnings.Add(label + ": photo outside shared folder omitted."); return ""; }
            try
            {
                for (var current = new FileInfo(source) as FileSystemInfo; current is not null && current.FullName.Length >= root.Length; current = current is FileInfo f ? f.Directory : ((DirectoryInfo)current).Parent)
                    if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked photo omitted.");
                if (photoEncoder is not null)
                {
                    var encoded = photoEncoder(source);
                    if (encoded is null || encoded.Length > 2 * 1024 * 1024) { warnings.Add(label + ": photo could not be prepared for the guide."); return ""; }
                    return $"<img alt=\"{H(label)}\" src=\"data:image/jpeg;base64,{Convert.ToBase64String(encoded)}\">";
                }
                string mime = Path.GetExtension(source).ToLowerInvariant() switch { ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", _ => "" };
                if (mime.Length == 0 || new FileInfo(source).Length > 8 * 1024 * 1024) { warnings.Add(label + ": unsupported or oversized photo omitted."); return ""; }
                return $"<img alt=\"{H(label)}\" src=\"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(source))}\">";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { warnings.Add(label + ": photo unavailable."); return ""; }
        }
        string problemHtml = string.Join("", problems.Select(p => $"<section><h2>{H(p.Title)}</h2>{string.Join("", p.Photos.Select(path => Image(path, p.Title)))}<p class=\"instructions\">{H(p.Body)}</p></section>"));
        foreach (var unit in units)
        {
            var gate = gates.SingleOrDefault(g => g.Id == unit.GateId);
            if (gate is null) { warnings.Add(unit.UnitDisplay + ": gate missing; omitted."); continue; }
            string title = gate.AssetDisplay + " / " + unit.UnitDisplay;
            string filename = "unit-" + FileKey(unit.Id) + ".html";
            string selectedParts = string.Join("", parts.Where(p => unit.ReplacementPartIds is { } ids ? ids.Contains(p.Id, StringComparer.OrdinalIgnoreCase) : p.AppliesTo(gate.Id, unit.Id))
                .Select(p => $"<section><h3>{H(p.Description)}</h3>{Image(p.PhotoPath, p.Description)}<p>Item ID: {H(p.PartNumber)} · Reported bin: {H(p.LocationDisplay)}</p><p>Reported quantity: {H(p.QuantityDisplay)} (snapshot; check stockroom for current availability)</p></section>"));
            var body = new StringBuilder($"<h1>{H(title)}</h1><p>{H(gate.Category)} · {H(gate.Location)}</p><p>Guide snapshot: {date:yyyy-MM-dd HH:mm zzz}. Follow the approved site procedure. This is a read-only copy.</p>{Image(unit.PrimaryPhoto, title)}");
            if (unit.MobileOfflineInstructions.Length > 0) body.Append($"<h2>Quick guidance</h2><p class=\"instructions\">{H(unit.MobileOfflineInstructions)}</p>");
            body.Append("<h2>Common Problems</h2>").Append(problemHtml).Append("<h2>Replacement parts</h2>").Append(selectedParts.Length > 0 ? selectedParts : "<p>No replacement parts chosen.</p>");
            if (includeNotes) body.Append("<h2>Shared notes (snapshot)</h2><p class=\"instructions\">").Append(H(data.LoadSystemNotes(unit))).Append("</p>");
            totalCharacters += body.Length;
            if (totalCharacters > 160 * 1024 * 1024) throw new InvalidDataException("This guide package is too large. Reduce the number or size of common-problem photos and retry.");
            pages.Add((filename, Page(title, body.ToString()))); index.Append($"<li><a href=\"{filename}\">{H(title)}</a></li>");
            string payload = unit.MobileSupportUrl.Length > 0 ? unit.MobileSupportUrl : unit.MobileOfflineInstructions.Trim().Length > 0 ? OfflinePayload(gate, unit, date) : "";
            if (payload.Length > 0)
            {
                if (Encoding.UTF8.GetByteCount(payload) > 2000) throw new InvalidOperationException(title + ": shorten the QR link or offline text (maximum 2000 UTF-8 bytes).");
                labelCount++; string qr = QrSvg(payload);
                pages.Add(("qr-" + FileKey(unit.Id) + ".svg", qr));
                labels.Append($"<section class=\"label\"><h2>{H(title)}</h2>{qr}<p>{(unit.MobileSupportUrl.Length > 0 ? "Scan to open the approved document link. Internet required. Test access on a signed-out phone before fitting the label." : "Offline text guidance — no photos, login or internet. Camera support for text codes varies.")}</p><p class=\"instructions\">{H(unit.MobileSupportUrl.Length > 0 ? "Keep the printed guide with the unit for offline reference." : payload)}</p></section>");
            }
        }
        if (pages.All(p => !p.Name.StartsWith("unit-"))) throw new InvalidOperationException("No units have a usable gate record.");
        pages.Add(("index.html", Page("Unit support guides", $"<h1>Unit support guides</h1><p>Open a guide, then use your browser’s Print → Save as PDF for a portable photo guide, or print a copy for the box. No server is required to read these self-contained guide files. Nothing has been uploaded.</p><ul>{index}</ul>{(labelCount > 0 ? "<a href=\"qr-labels.html\">Open printable QR labels</a>" : "<p>Guide only. Add offline text or an HTTPS link in Setup and export again to create a QR label.</p>")}")));
        pages.Add(("qr-labels.html", Page("Unit QR labels", "<h1>Unit QR labels</h1><p>Print and test at the intended label size before use. Replace offline text labels whenever instructions change.</p>" + (labelCount == 0 ? "<p>No QR labels generated. Set a unit’s offline text or approved HTTPS document link in Setup, then export again.</p>" : labels.ToString()))));
        pages.Add(("READ-ME.txt", "Extract this ZIP. Open index.html and select a unit. Each unit HTML guide embeds its photos and can be printed or saved as PDF in a browser. Put a printed photo guide in the box. QR labels contain either configured offline text, or the exact approved document link supplied in Setup. No URL is invented, no server is started, and nothing is published. Offline QR codes contain no photos. Test scanning on the phones used by your crew. For document links, test with a worker who has no employee account before printing labels. Use only company-approved storage/sharing. This export contains published common problems, unit identification and assigned parts; shared notes are included only when selected. It does not include live inventory, USB controls, drawings or manual attachments. Re-export when guidance changes.\n\nExport warnings:\n" + string.Join("\n", warnings.Distinct())));
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
                foreach (var page in pages) { using var writer = new StreamWriter(zip.CreateEntry(page.Name).Open(), new UTF8Encoding(false)); writer.Write(page.Body); }
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return new(pages.Count(p => p.Name.StartsWith("unit-")), labelCount, warnings.Distinct().ToList());
    }
    private static string Page(string title, string body) => "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>" + WebUtility.HtmlEncode(title) + "</title><style>body{font:17px/1.5 system-ui,sans-serif;max-width:760px;margin:auto;padding:20px;color:#183244;background:#fff}img{display:block;max-width:100%;max-height:480px;object-fit:contain;margin:12px auto}section{border-bottom:1px solid #ccd9e2;padding:12px 0}.instructions{white-space:pre-wrap;overflow-wrap:anywhere}svg{display:block;width:240px;max-width:100%;margin:auto}.label{break-inside:avoid;page-break-inside:avoid}a{color:#12627f}h1{line-height:1.2}@media print{body{padding:0;font-size:12pt}section{break-inside:avoid}}</style><body>" + body + "</body></html>";
}
