using System.IO;
using System.Windows.Media.Imaging;
using PDFtoImage;
using SkiaSharp;

namespace SafetyGateSupport.Services;

/// <summary>Renders a local PDF snapshot with the bundled PDF engine; no browser/runtime installer.</summary>
public static class DrawingPreviewService
{
    public const int MaxFileBytes = 64 * 1024 * 1024;
    public sealed record Page(BitmapSource Image, int PageCount);

    public static Task<byte[]> ReadAsync(string path, CancellationToken token) => Task.Run(async () =>
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        if (file.Length > MaxFileBytes) throw new InvalidDataException("This drawing is too large for the inline preview. Use Open original.");
        using var copy = new MemoryStream();
        var buffer = new byte[81920]; int count;
        while ((count = await file.ReadAsync(buffer, token)) > 0)
        {
            if (copy.Length + count > MaxFileBytes) throw new InvalidDataException("This drawing exceeds the inline preview limit. Use Open original.");
            copy.Write(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return copy.ToArray();
    }, token);

    private static readonly SemaphoreSlim PdfRenderer = new(1);
    public static Task<Page> RenderPdfAsync(byte[] bytes, int pageNumber, CancellationToken token) => Task.Run(async () =>
    {
        await PdfRenderer.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            int pageCount = Conversion.GetPageCount(bytes);
            if (pageNumber < 0 || pageNumber >= pageCount) throw new ArgumentOutOfRangeException(nameof(pageNumber));
            var size = Conversion.GetPageSizes(bytes).ElementAt(pageNumber);
            double scale = Math.Min(2000 / size.Width, 2000 / size.Height);
            using var bitmap = Conversion.ToImage(bytes, page: pageNumber, options: new RenderOptions(
                Width: Math.Max(1, (int)(size.Width * scale)), Height: Math.Max(1, (int)(size.Height * scale))));
            token.ThrowIfCancellationRequested();
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var imageStream = encoded.AsStream();
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = imageStream; image.EndInit(); image.Freeze();
            return new Page(image, pageCount);
        }
        finally { PdfRenderer.Release(); }
    }, token);
}
