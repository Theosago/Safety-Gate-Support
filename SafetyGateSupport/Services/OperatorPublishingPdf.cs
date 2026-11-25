using System.IO;
using System.Text;
using QRCoder;
using SafetyGateSupport.Models;
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

        Text(cover, "Files for this unit", 36, 319, 20, bold, Ink, 536);
        if (drawingLink is null)
            Text(cover, "No electrical PDF is assigned to this unit.", 36, 290, 12, normal, Muted, 536);
        double firstEntry = drawingLink is null ? 253 : 276;
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
            Text(page, "Files for this unit", 36, 697, 28, bold, Ink, 540);
            int row = 0;
            foreach (var entry in entries.Skip(3 + i * 19).Take(19)) Entry(page, entry, 657 - 31 * row++, normal, bold);
            Footer(page, folderLink, normal, "Contents");
        }

        return document.Build();
    }

    internal static byte[] NoAssignedDrawing(OperatorPublicationUnit item, string folderLink)
    {
        using var document = new PdfDocumentBuilder();
        document.DocumentInformation.Title = item.Label + " - Electrical drawing";
        var normal = document.AddTrueTypeFont(RegularFont.Value);
        var bold = document.AddTrueTypeFont(BoldFont.Value);
        var page = document.AddPage(Width, Height);
        Fill(page, 0, 644, Width, 148, Ink);
        Text(page, "ELECTRICAL DRAWING", 36, 714, 13, bold, White, 536);
        Text(page, item.Label, 36, 666, 34, bold, White, 536);
        Text(page, "No electrical drawing is assigned to this unit.", 36, 601, 16, bold, Ink, 536);
        Footer(page, folderLink, normal, "Unit files");
        return document.Build();
    }

    internal static byte[] SpareParts(OperatorPublicationUnit item, UnitSpareParts report, string folderLink)
    {
        using var document = new PdfDocumentBuilder();
        document.DocumentInformation.Title = item.Label + " - Spare parts";
        var normal = document.AddTrueTypeFont(RegularFont.Value);
        var bold = document.AddTrueTypeFont(BoldFont.Value);
        const int pageSize = 9;
        int pageCount = Math.Max(1, (report.Parts.Count + pageSize - 1) / pageSize);
        string scan = report.LastConfirmedUtc is { } last ? OperatorSpareParts.ScanTime(last) : "No confirmed count yet";
        for (int index = 0; index < pageCount; index++)
        {
            var page = document.AddPage(Width, Height);
            Fill(page, 0, 644, Width, 148, Ink);
            Text(page, "SPARE PARTS", 36, 715, 15, bold, (182, 221, 222), 536);
            Text(page, item.Label, 36, 666, 32, bold, White, 536);
            Text(page, "Last successful stockroom scan", 36, 616, 11, bold, Accent, 536);
            Text(page, scan, 36, 596, 17, bold, Ink, 536);
            Text(page, report.Plant is null ? "Stockroom plant not selected" : report.Plant + " stockroom", 36, 576, 11, normal, Muted, 536);
            if (report.Parts.Count == 0)
                Text(page, "No spare parts are assigned to this unit.", 36, 529, 14, normal, Ink, 536);

            int row = 0;
            foreach (var part in report.Parts.Skip(index * pageSize).Take(pageSize))
            {
                double y = 551 - 52 * row++;
                Fill(page, 36, y - 45, 540, 47, Pale);
                Text(page, part.Description, 46, y - 12, 12, bold, Ink, 518);
                string bin = !string.IsNullOrWhiteSpace(part.Stock?.Bin) ? "   |   Bin " + part.Stock.Bin :
                    part.CatalogBin.Length > 0 ? "   |   Catalog bin " + part.CatalogBin : "";
                Text(page, "Item ID " + (part.ItemId.Length > 0 ? part.ItemId : "unlisted") + bin, 46, y - 26, 10, normal, Ink, 518);
                string quantity = part.Stock is { } stock
                    ? $"{PartRecord.FormatQuantity(stock.Quantity)} in stock   |   Checked {OperatorSpareParts.ScanTime(stock.CheckedUtc)}"
                    : "Quantity unknown";
                Text(page, quantity, 46, y - 39, 10, normal, Muted, 518);
            }
            Footer(page, folderLink, normal, $"{index + 1} of {pageCount}");
        }
        return document.Build();
    }

    /// <summary>
    /// A phone-readable chart of a signal column configuration: each input combination drawn as the
    /// column (segment 1 at the base), with the input wires, state names, blinking and sound.
    /// </summary>
    internal static byte[] LightColumn(string unitLabel, string configurationName, byte[] signal)
    {
        using var document = new PdfDocumentBuilder();
        document.DocumentInformation.Title = unitLabel + " - Light column";
        var normal = document.AddTrueTypeFont(RegularFont.Value);
        var bold = document.AddTrueTypeFont(BoldFont.Value);
        PdfPageBuilder Header()
        {
            var page = document.AddPage(Width, Height);
            Fill(page, 0, 644, Width, 148, Ink);
            Text(page, "LIGHT COLUMN", 36, 715, 15, bold, (182, 221, 222), 536);
            Text(page, unitLabel, 36, 666, 32, bold, White, 536);
            Text(page, "What the light means", 36, 611, 20, bold, Ink, 536);
            Text(page, "Configuration " + configurationName, 36, 591, 10, normal, Muted, 536);
            return page;
        }

        Siemens.PreparedSignalUpload selected;
        Siemens.SignalLabels labels;
        try { selected = Siemens.PreparedSignalUpload.FromBytes(signal, configurationName); labels = Siemens.SignalLabels.Read(selected); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            var notice = Header();
            Text(notice, "This configuration cannot be drawn here. Use the configuration sheet.", 36, 556, 13, normal, Ink, 536);
            return document.Build();
        }

        var page = Header();
        // Input wires
        Text(page, "INPUTS", 36, 560, 9, bold, Accent, 200);
        double y = 542;
        foreach (var input in labels.Inputs)
        {
            double x = 36 + (input.Number - 1) % 2 * 270;
            if (input.Number > 1 && input.Number % 2 == 1) y -= 18;
            if (input.WireColor is { } wire) { Fill(page, x, y - 1, 10, 10, Darken(wire)); Fill(page, x + 1, y, 8, 8, wire); }
            Text(page, input.Label + (input.Description.Length > 0 ? " - " + input.Description : ""), x + 16, y, 10.5, normal, Ink, 250);
        }

        // One drawn column per configured input combination, six to a row.
        const double cardW = 90, cardH = 172, segH = 9, colW = 22;
        double rowTop = y - 26;
        int index = 0;
        foreach (int state in selected.Archive.ActiveStates.Distinct().Order())
        {
            Siemens.SignalStatePreview.Appearance look;
            try { look = Siemens.SignalStatePreview.ReadAppearance(selected, state); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { continue; }
            int column = index % 6;
            if (column == 0 && index > 0) rowTop -= cardH;
            if (column == 0 && rowTop - cardH < 78) { LightFooter(page, normal); page = Header(); rowTop = 570; }
            double cx = 36 + column * cardW, cy = rowTop;
            Fill(page, cx + 2, cy - cardH + 8, cardW - 4, cardH - 8, Pale);

            // the column: cap, segments, base (upside-down mounting puts the base on top)
            int count = look.Segments.Count;
            bool sound = look.SoundMode != 0 && look.SoundVolume != 0;
            double bodyX = cx + (cardW - colW) / 2, baseY = cy - 14 - 10 - count * segH - 10;
            double stackTop = baseY + 10 + count * segH;
            if (labels.UpsideDown)
            {
                Fill(page, bodyX - 2, stackTop, colW + 4, 10, (31, 36, 41));
                for (int s = 0; s < count; s++) Lens(page, look.Segments[s], bodyX, stackTop - (s + 1) * segH, colW, segH);
                Fill(page, bodyX, baseY + 10 - (sound ? 10 : 5), colW, sound ? 10 : 5, (52, 58, 64));
            }
            else
            {
                Fill(page, bodyX - 2, baseY, colW + 4, 10, (31, 36, 41));
                for (int s = 0; s < count; s++) Lens(page, look.Segments[s], bodyX, baseY + 10 + s * segH, colW, segH);
                Fill(page, bodyX, stackTop, colW, sound ? 10 : 5, (52, 58, 64));
                if (sound) for (int k = 0; k < 3; k++) Fill(page, bodyX + 5, stackTop + 2 + k * 3, colW - 10, 1, (150, 158, 166));
            }

            var on = labels.Inputs.Where(i => (state & (1 << (i.Number - 1))) != 0).ToArray();
            Text(page, on.Length == 0 ? "No inputs" : "Input " + string.Join(" + ", on.Select(i => i.Number)), cx + 8, baseY - 13, 10, bold, Ink, cardW - 14);
            double line = baseY - 25;
            if (on.Any(i => i.WireColor is not null))
                Text(page, string.Join(", ", on.Select(i => i.WireName)), cx + 8, line, 8.5, normal, Muted, cardW - 14);
            line -= 11;
            if (labels.NameFor(state) is { } name) { Text(page, name, cx + 8, line, 8.5, normal, Ink, cardW - 14); line -= 11; }
            string effects = string.Join(", ", look.Segments.Where(s => !s.IsOff && s.Effect != 10).Select(s => s.EffectName.ToLowerInvariant()).Distinct());
            if (sound) effects = effects.Length == 0 ? "sound" : effects + ", sound";
            if (effects.Length > 0) Text(page, effects, cx + 8, line, 8.5, normal, Accent, cardW - 14);
            index++;
        }
        if (index == 0) Text(page, "No input combination lights the column or sounds.", 36, rowTop - 20, 12, normal, Ink, 536);
        LightFooter(page, normal);
        return document.Build();
    }

    private static void LightFooter(PdfPageBuilder page, PdfDocumentBuilder.AddedFont font)
    {
        Fill(page, 36, 69, 540, 1, Line);
        Text(page, "Drawn from the saved configuration. Segment 1 is next to the base.", 36, 45, 9.5, font, Muted, 430);
        Text(page, "Light column", 490, 45, 10, font, Muted, 86);
    }

    private static void Lens(PdfPageBuilder page, Siemens.SignalStatePreview.Segment segment, double x, double y, double w, double h)
    {
        (byte, byte, byte) color;
        if (segment.IsOff) color = (214, 220, 226);
        else
        {
            var (r, g, b) = segment.DisplayColor; double o = segment.DisplayOpacity;
            color = ((byte)(255 - (255 - r) * o), (byte)(255 - (255 - g) * o), (byte)(255 - (255 - b) * o));
        }
        Fill(page, x, y, w, h, Darken(color));
        Fill(page, x + 1, y + 1, w - 2, h - 1.5, color);
    }

    private static (byte, byte, byte) Darken((byte R, byte G, byte B) c) => ((byte)(c.R * 0.7), (byte)(c.G * 0.7), (byte)(c.B * 0.7));

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
