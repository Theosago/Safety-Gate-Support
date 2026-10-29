using System.IO;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace SafetyGateSupport.Services;

/// <summary>Renders a local PDF snapshot with the Windows PDF engine; no browser/runtime installer.</summary>
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

    public static Task<Page> RenderPdfAsync(byte[] bytes, int pageNumber, CancellationToken token) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        using var source = new MemoryStream(bytes, writable: false);
        using var random = source.AsRandomAccessStream();
        var document = await PdfDocument.LoadFromStreamAsync(random).AsTask(token);
        if (pageNumber < 0 || pageNumber >= document.PageCount) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        using var page = document.GetPage((uint)pageNumber);
        using var rendered = new InMemoryRandomAccessStream();
        double scale = Math.Min(2000 / page.Size.Width, 2000 / page.Size.Height);
        await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale), DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale)
        }).AsTask(token);
        token.ThrowIfCancellationRequested();
        using var imageStream = rendered.AsStreamForRead(); imageStream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = imageStream; image.EndInit(); image.Freeze();
        return new Page(image, (int)document.PageCount);
    }, token);
}
