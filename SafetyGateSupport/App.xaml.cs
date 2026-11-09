using System.IO;
using System.Text.Json;
using System.Windows;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 0 || e.Args[0] is not ("--validate-pdf-preview" or "--validate-drawing-orientation" or "--validate-stockroom-webview" or "--validate-stockroom-item")) { base.OnStartup(e); return; }
        // Package verification is explicit and isolated: never create the normal
        // window or read its shared-root settings when validating the bundled engine.
        Navigating += (_, navigation) => navigation.Cancel = true;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        int exitCode = 1;
        try
        {
            if (e.Args.Length != (e.Args[0] == "--validate-stockroom-item" ? 4 : 3)) throw new ArgumentException("Supply an input PDF and a new verification-result path.");
            if (e.Args[0] is "--validate-stockroom-webview" or "--validate-stockroom-item")
            {
                var address = new Uri(Path.GetFullPath(e.Args[1])).AbsoluteUri;
                var result = e.Args[0] == "--validate-stockroom-item" ? await StockroomWebViewReader.SearchAsync(address, e.Args[3], CancellationToken.None) : await StockroomWebViewReader.FetchAsync(address, CancellationToken.None);
                using var output = new FileStream(e.Args[2], FileMode.CreateNew, FileAccess.Write, FileShare.None); JsonSerializer.Serialize(output, result);
            }
            else if (e.Args[0] == "--validate-drawing-orientation")
            {
                var image = await IdentificationPhotoLoader.LoadAsync(e.Args[1], 2000, CancellationToken.None);
                if (image.Source is null) throw new InvalidDataException("The OCR verification image could not be decoded.");
                var result = await DrawingOrientationService.DetectAsync(image.Source, null, 0, Path.Combine(Path.GetDirectoryName(e.Args[2])!, "orientation-cache"), CancellationToken.None);
                if (result.Method != "Local text recognition" || result.Confidence <= 0) throw new InvalidDataException("Bundled drawing OCR did not recognize directional text.");
                using var output = new FileStream(e.Args[2], FileMode.CreateNew, FileAccess.Write, FileShare.None); JsonSerializer.Serialize(output, result);
            }
            else
            {
                var bytes = await DrawingPreviewService.ReadAsync(e.Args[1], CancellationToken.None);
                var page = await DrawingPreviewService.RenderPdfAsync(bytes, 0, CancellationToken.None);
                using var output = new FileStream(e.Args[2], FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(output, new { page.PageCount, page.Image.PixelWidth, page.Image.PixelHeight });
            }
            exitCode = 0;
        }
        catch (Exception error) { Console.Error.WriteLine("PDF package verification failed: " + error); }
        finally { Shutdown(exitCode); }
    }
}
