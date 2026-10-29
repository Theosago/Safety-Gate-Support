using System.IO;
using System.Text.Json;
using System.Windows;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 0 || e.Args[0] != "--validate-pdf-preview") { base.OnStartup(e); return; }
        // Package verification is explicit and isolated: never create the normal
        // window or read its shared-root settings when validating the bundled engine.
        Navigating += (_, navigation) => navigation.Cancel = true;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        int exitCode = 1;
        try
        {
            if (e.Args.Length != 3) throw new ArgumentException("Supply an input PDF and a new verification-result path.");
            var bytes = await DrawingPreviewService.ReadAsync(e.Args[1], CancellationToken.None);
            var page = await DrawingPreviewService.RenderPdfAsync(bytes, 0, CancellationToken.None);
            using var output = new FileStream(e.Args[2], FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(output, new { page.PageCount, page.Image.PixelWidth, page.Image.PixelHeight });
            exitCode = 0;
        }
        catch (Exception error) { Console.Error.WriteLine("PDF package verification failed: " + error); }
        finally { Shutdown(exitCode); }
    }
}
