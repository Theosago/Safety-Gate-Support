using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SafetyGateSupport.Services;

/// <summary>Offline reading-direction detection. Native OCR runs in a bounded child process.</summary>
public static class DrawingOrientationService
{
    public sealed record Result(int Angle, string Method, double Confidence, bool Cached = false);
    private static readonly SemaphoreSlim Worker = new(1);
    private const string Version = "reading-v1";
    public static Task<Result> DetectAsync(BitmapSource image, byte[]? pdf, int page, string cacheRoot, CancellationToken token) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested(); int landscape = image.PixelHeight > image.PixelWidth ? 90 : 0;
        byte[]? pixels = null;
        string hash = pdf is not null ? Convert.ToHexString(SHA256.HashData(pdf)) + "-" + page : Convert.ToHexString(SHA256.HashData(pixels = Png(image)));
        string cache = Path.Combine(cacheRoot, Version + "-" + hash + ".json");
        try
        {
            if (File.Exists(cache) && JsonSerializer.Deserialize<Result>(await File.ReadAllTextAsync(cache, token)) is { Angle: 0 or 90 or 180 or 270 } prior)
                return prior with { Cached = true };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        await Worker.WaitAsync(token);
        try
        {
            Result? result = null;
            if (pdf is not null)
            {
                try { if (PdfTextOrientation.Detect(pdf, page, landscape, token) is { } text) result = new(text.Angle, "PDF text direction", text.Confidence); }
                catch (Exception error) when (error is not OperationCanceledException) { /* Image-only or unusual PDFs use OCR below. */ }
            }
            if (result is null && IsUniform(image)) result = new(landscape, "Landscape layout; no directional content", 0);
            if (result is null)
            {
                int opposite = (landscape + 180) % 360;
                var first = await ScoreAsync(image, landscape, token); var second = await ScoreAsync(image, opposite, token);
                int angle = second.Value > first.Value ? opposite : landscape;
                double best = Math.Max(first.Value, second.Value);
                double confidence = best > 0 ? (best - Math.Min(first.Value, second.Value)) / best : 0;
                result = best > 0 ? new(angle, "Local text recognition", confidence) : new(landscape, "Landscape layout; text direction uncertain", 0);
            }
            token.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(cacheRoot); string temp = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(result), token); token.ThrowIfCancellationRequested(); File.Move(temp, cache, true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* A cache failure does not prevent viewing. */ }
            return result;
        }
        finally { Worker.Release(); }
    }, token);

    public static BitmapSource Rotate(BitmapSource image, int angle)
    {
        if (angle == 0) return image;
        var output = new TransformedBitmap(image, new RotateTransform(angle)); output.Freeze(); return output;
    }
    private static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static bool IsUniform(BitmapSource image)
    {
        double scale = Math.Min(1, 80d / Math.Max(image.PixelWidth, image.PixelHeight));
        BitmapSource sample = scale < 1 ? new TransformedBitmap(image, new ScaleTransform(scale, scale)) : image;
        var gray = new FormatConvertedBitmap(sample, PixelFormats.Gray8, null, 0); byte[] pixels = new byte[gray.PixelWidth * gray.PixelHeight]; gray.CopyPixels(pixels, gray.PixelWidth, 0);
        return pixels.Length == 0 || pixels.Max() - pixels.Min() < 12;
    }
    private sealed record OcrScore(double Value, int Words);
    private static async Task<OcrScore> ScoreAsync(BitmapSource image, int angle, CancellationToken token)
    {
        image = Rotate(image, angle);
        double scale = Math.Min(1, 1800d / Math.Max(image.PixelWidth, image.PixelHeight));
        if (scale < 1) { var smaller = new TransformedBitmap(image, new ScaleTransform(scale, scale)); smaller.Freeze(); image = smaller; }
        byte[] png = Png(image);
        string directory = Path.Combine(AppContext.BaseDirectory, "Ocr");
        var start = new ProcessStartInfo(Path.Combine(directory, "tesseract.exe")) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "stdin", "stdout", "--tessdata-dir", "tessdata", "-l", "eng", "--oem", "1", "--psm", "11", "-c", "tessedit_create_tsv=1", "-c", "tessedit_create_txt=0" }) start.ArgumentList.Add(argument);
        start.Environment["OMP_THREAD_LIMIT"] = "1";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = new Process { StartInfo = start };
        bool started = false;
        try
        {
            if (!(started = process.Start())) throw new IOException("Local drawing-text recognition could not start.");
            Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token), errors = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.StandardInput.BaseStream.WriteAsync(png, deadline.Token); process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            string tsv = await output, error = await errors;
            if (process.ExitCode != 0) throw new IOException("Local drawing-text recognition failed: " + error.Trim());
            return ParseScore(tsv);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Drawing-text recognition exceeded its time limit."); }
        finally { if (started && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
    }
    public static ScoreSummary ScoreWords(string tsv)
    {
        var score = ParseScore(tsv); return new(score.Value, score.Words);
    }
    public sealed record ScoreSummary(double Value, int Words);
    private static OcrScore ParseScore(string tsv)
    {
        double weighted = 0, weight = 0; int words = 0;
        foreach (string line in tsv.Split('\n').Skip(1))
        {
            var cells = line.TrimEnd('\r').Split('\t');
            if (cells.Length < 12 || cells[0] != "5" || !double.TryParse(cells[10], NumberStyles.Float, CultureInfo.InvariantCulture, out double confidence) || confidence < 35) continue;
            string word = cells[11]; int letters = word.Count(char.IsLetter); if (letters < 2) continue;
            double w = Math.Min(letters, 14); weighted += confidence / 100 * w; weight += w; words++;
        }
        return words < 2 ? new(0, words) : new(weighted / weight * Math.Log2(2 + weight), words);
    }
}
