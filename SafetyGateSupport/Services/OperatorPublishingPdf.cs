using System.IO;
using System.Text;
using QRCoder;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Outline;
using UglyToad.PdfPig.Outline.Destinations;
using UglyToad.PdfPig.Writer;

namespace SafetyGateSupport.Services;

internal sealed record UnitGuidePhoto(byte[] Jpeg, int Width, int Height);
internal sealed record GuidePdfSource(string Title, byte[] Bytes);
internal sealed record UnitQrLabel(string Asset, string Unit, string Location, string Url);

internal static class OperatorPublishingPdf
{
    private static readonly Lazy<byte[]> RegularFont = new(() => ReadFont("Regular"));
    private static readonly Lazy<byte[]> BoldFont = new(() => ReadFont("Bold"));
    private static readonly AnnotationBorder BorderlessLink = new(0, 0, 0, []);
    private const double Width = 612, Height = 792;
    private static readonly (byte, byte, byte) Ink = (18, 42, 58), Muted = (63, 83, 96),
        Accent = (0, 102, 108), Pale = (237, 244, 246), Line = (192, 209, 216), White = (255, 255, 255);

    internal static byte[] Guide(OperatorPublicationUnit item, UnitGuidePhoto? photo, IReadOnlyList<GuidePdfSource> sources,
        string folderLink, string? drawingLink, string? notesLink)
    {
        using var document = new PdfDocumentBuilder(1.7);
        document.DocumentInformation.Title = item.Label + " - Unit guide";
        document.DocumentInformation.Subject = "Unit identification and electrical reference";
        var normal = document.AddTrueTypeFont(RegularFont.Value);
        var bold = document.AddTrueTypeFont(BoldFont.Value);
        var cover = document.AddPage(Width, Height);
        var entries = new List<(string Title, int Page, int Count)>();
        int contentsPages = sources.Count > 3 ? (int)Math.Ceiling((sources.Count - 3) / 19d) : 0;
        for (int i = 0; i < contentsPages; i++) document.AddPage(Width, Height);

        foreach (var source in sources)
        {
            using var input = PdfDocument.Open(source.Bytes);
            int page = document.Pages.Count + 1;
            for (int i = 1; i <= input.NumberOfPages; i++)
                document.AddPage(input, i, new PdfDocumentBuilder.AddPageOptions { KeepAnnotations = false });
            entries.Add((source.Title, page, input.NumberOfPages));
        }

        document.Bookmarks = new Bookmarks(new[] { Bookmark("Unit overview", 1) }
            .Concat(entries.Select(entry => Bookmark(entry.Title, entry.Page))).ToArray());

        Fill(cover, 0, 644, Width, 148, Ink);
        Fill(cover, 36, 744, 30, 3, (76, 206, 194));
        Text(cover, "SAFETY GATE SUPPORT", 36, 715, 13, bold, (182, 221, 222), 530);
        Text(cover, item.Label, 36, 666, 34, bold, White, 536);
        Text(cover, "Unit identification", 36, 611, 20, bold, Ink, 536);

        const double photoX = 36, photoY = 357, photoW = 284, photoH = 231;
        Fill(cover, photoX, photoY, photoW, photoH, Pale);
        if (photo is { Width: > 0, Height: > 0 })
        {
            double scale = Math.Min((photoW - 16) / photo.Width, (photoH - 16) / photo.Height);
            double w = photo.Width * scale, h = photo.Height * scale;
            double x = photoX + (photoW - w) / 2, y = photoY + (photoH - h) / 2;
            cover.AddJpeg(photo.Jpeg, new PdfRectangle(x, y, x + w, y + h));
        }
        else Text(cover, "No identification photo on file", 55, 465, 12, normal, Muted, 245);

        var details = new[]
        {
            ("UNIT NAME", item.Unit.DisplayName), ("AREA", item.Gate.Category), ("LOCATION", item.Gate.Location),
            ("MANUFACTURER / MODEL", string.Join(" / ", new[] { item.Gate.ManufacturerDisplay, item.Gate.Model }
                .Where(value => !string.IsNullOrWhiteSpace(value)))), ("EQUIPMENT", item.Gate.EquipmentType)
        };
        double detailY = 574;
        foreach (var (label, value) in details.Where(d => !string.IsNullOrWhiteSpace(d.Item2)))
        {
            Text(cover, label, 346, detailY, 9, bold, Accent, 227);
            Text(cover, value, 346, detailY - 20, 13, normal, Ink, 227);
            detailY -= 46;
        }

        Text(cover, "In this guide", 36, 319, 20, bold, Ink, 536);
        if (entries.Count == 0 || !entries[0].Title.Equals("Electrical drawing", StringComparison.Ordinal))
            Text(cover, "No electrical PDF is assigned to this unit.", 36, 290, 12, normal, Muted, 536);
        double firstEntry = entries.Count == 0 || entries[0].Title != "Electrical drawing" ? 253 : 276;
        for (int i = 0; i < Math.Min(3, entries.Count); i++) Entry(cover, entries[i], firstEntry - 35 * i, normal, bold);
        if (contentsPages > 0)
        {
            Text(cover, "More documents on the next page", 36, 149, 11, normal, Accent, 540);
            Link(cover, new GoToAction(Destination(2)), new PdfRectangle(36, 142, 310, 161));
        }

        double next = 36;
        if (drawingLink is not null) next += WebLink(cover, "Open standalone electrical PDF", drawingLink, next, 102, bold) + 22;
        if (notesLink is not null) WebLink(cover, "Open operator notes", notesLink, next, 102, bold);
        Footer(cover, folderLink, normal, "Unit overview");

        for (int i = 0; i < contentsPages; i++)
        {
            var page = document.Pages[i + 2];
            Text(page, item.Label, 36, 731, 14, bold, Accent, 540);
            Text(page, "In this guide", 36, 697, 28, bold, Ink, 540);
            int row = 0;
            foreach (var entry in entries.Skip(3 + i * 19).Take(19)) Entry(page, entry, 657 - 31 * row++, normal, bold);
            Footer(page, folderLink, normal, "Contents");
        }

        return document.Build();
    }

    private static ExplicitDestination Destination(int page) =>
        new(page, ExplicitDestinationType.FitPage, ExplicitDestinationCoordinates.Empty);

    private static BookmarkNode Bookmark(string title, int page) => new DocumentBookmarkNode(title, 0, Destination(page), []);

    private static void Entry(PdfPageBuilder page, (string Title, int Page, int Count) entry, double y,
        PdfDocumentBuilder.AddedFont normal, PdfDocumentBuilder.AddedFont bold)
    {
        Fill(page, 36, y - 9, 540, 28, Pale);
        Text(page, entry.Title, 47, y, 12, bold, Ink, 414);
        string pageNumber = entry.Count == 1 ? $"Page {entry.Page}" : $"Pages {entry.Page}-{entry.Page + entry.Count - 1}";
        Text(page, pageNumber, 475, y, 10, normal, Accent, 94);
        Link(page, new GoToAction(Destination(entry.Page)), new PdfRectangle(36, y - 9, 576, y + 19));
    }

    private static void Footer(PdfPageBuilder page, string folderLink, PdfDocumentBuilder.AddedFont font, string section)
    {
        Fill(page, 36, 69, 540, 1, Line);
        WebLink(page, "Open the unit folder for the current files", folderLink, 36, 45, font);
        Text(page, section, 475, 45, 10, font, Muted, 100);
    }

    private static double WebLink(PdfPageBuilder page, string label, string link, double x, double y,
        PdfDocumentBuilder.AddedFont font)
    {
        double width = Text(page, label, x, y, 11, font, Accent, 350);
        if (MobileSupportExporter.ValidLink(link))
            Link(page, new UriAction(link), new PdfRectangle(x, y - 4, x + width, y + 13));
        return width;
    }

    internal static byte[] QrLabels(IReadOnlyList<UnitQrLabel> labels)
    {
        if (labels.Count == 0) throw new InvalidOperationException("Publish units before printing QR codes.");
        if (labels.Any(label => !MobileSupportExporter.ValidLink(label.Url)))
            throw new InvalidOperationException("A unit folder is missing a valid QR link.");

        using var document = new PdfDocumentBuilder();
        document.DocumentInformation.Title = "Safety Gate Support - Unit QR codes";
        var normal = document.AddTrueTypeFont(RegularFont.Value);
        var bold = document.AddTrueTypeFont(BoldFont.Value);
        using var generator = new QRCodeGenerator();
        const int perPage = 8;
        for (int start = 0; start < labels.Count; start += perPage)
        {
            var page = document.AddPage(Width, Height);
            Fill(page, 28, 732, 556, 1, Line);
            Text(page, "Safety Gate Support", 28, 750, 19, bold, Ink, 350);
            Text(page, $"Unit QR codes | {start / perPage + 1} of {(labels.Count + perPage - 1) / perPage}",
                406, 751, 10, normal, Muted, 178);

            for (int i = 0; i < Math.Min(perPage, labels.Count - start); i++)
            {
                var label = labels[start + i];
                double x = 28 + (i % 2) * 278, y = 727 - (i / 2 + 1) * 172;
                page.SetStrokeColor(Line.Item1, Line.Item2, Line.Item3);
                page.DrawRectangle(new PdfPoint(x, y), 278, 172, .6);
                using var code = generator.CreateQrCode(label.Url, QRCodeGenerator.ECCLevel.M);
                DrawQr(page, code, x + 10, y + 35, 103);
                Text(page, label.Asset, x + 117, y + 113, 18, bold, Ink, 151);
                Text(page, label.Unit, x + 117, y + 90, 16, bold, Ink, 151);
                Text(page, label.Location, x + 117, y + 67, 10, normal, Muted, 151);
                Text(page, "Scan to open unit files", x + 117, y + 40, 10, normal, Accent, 151);
                Link(page, new UriAction(label.Url), new PdfRectangle(x + 10, y + 35, x + 113, y + 138));
            }
            Text(page, "Print at actual size (100%). Cut along the borders.", 28, 23, 9, normal, Muted, 550);
        }
        return document.Build();
    }

    private static void Link(PdfPageBuilder page, PdfAction action, PdfRectangle rectangle) =>
        page.AddLink(new LinkAnnotation(action, rectangle, BorderlessLink));

    private static void DrawQr(PdfPageBuilder page, QRCodeData code, double x, double y, double size)
    {
        int count = code.ModuleMatrix.Count;
        double cell = size / count;
        Fill(page, x, y, size, size, White);
        page.SetStrokeColor(0, 0, 0);
        page.SetTextAndFillColor(0, 0, 0);
        for (int row = 0; row < count; row++)
        {
            int col = 0;
            while (col < count)
            {
                if (!code.ModuleMatrix[row][col]) { col++; continue; }
                int start = col++;
                while (col < count && code.ModuleMatrix[row][col]) col++;
                page.DrawRectangle(new PdfPoint(x + start * cell, y + (count - row - 1) * cell),
                    (col - start) * cell, cell, 0, true);
            }
        }
    }

    private static void Fill(PdfPageBuilder page, double x, double y, double width, double height, (byte R, byte G, byte B) color)
    {
        page.SetStrokeColor(color.R, color.G, color.B);
        page.SetTextAndFillColor(color.R, color.G, color.B);
        page.DrawRectangle(new PdfPoint(x, y), width, height, 0, true);
    }

    private static double Text(PdfPageBuilder page, string value, double x, double y, double size,
        PdfDocumentBuilder.AddedFont font, (byte R, byte G, byte B) color, double maxWidth)
    {
        value = new string(value.Normalize().Select(c => char.IsWhiteSpace(c) || char.IsControl(c) ? ' ' : c)
            .Take(180).ToArray()).Trim();
        if (value.Length == 0) return 0;
        double Measure(string text) => page.MeasureText(text, size, new PdfPoint(0, 0), font)[^1].EndBaseLine.X;
        double width;
        try { width = Measure(value); }
        catch (InvalidOperationException)
        {
            // Names may include characters outside the embedded font. Continue publishing with a visible replacement.
            var supported = new StringBuilder(value.Length);
            foreach (var rune in value.EnumerateRunes())
            {
                string symbol = rune.ToString();
                try { Measure(symbol); supported.Append(symbol); }
                catch (InvalidOperationException) { supported.Append('?'); }
            }
            value = supported.ToString();
            width = Measure(value);
        }
        if (width > maxWidth)
        {
            while (value.Length > 0 && Measure(value + "...") > maxWidth) value = value[..^1].TrimEnd();
            value += "...";
            width = Measure(value);
        }
        page.SetTextAndFillColor(color.R, color.G, color.B);
        page.AddText(value, size, new PdfPoint(x, y), font);
        return width;
    }

    private static byte[] ReadFont(string weight)
    {
        using var stream = typeof(OperatorPublishingPdf).Assembly.GetManifestResourceStream($"Publishing.LiberationSans-{weight}.ttf")
            ?? throw new InvalidOperationException("The embedded publishing font is unavailable.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
