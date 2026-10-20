using System.Diagnostics;
using System.IO;
using System.Windows;

namespace SafetyGateSupport.Services;

public static class FileLauncherService
{
    public static void Open(string target)
    {
        var isUrl = Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        if (!isUrl && !File.Exists(target) && !Directory.Exists(target))
        {
            MessageBox.Show($"Not found:\n\n{target}", "File not found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true, WorkingDirectory = !isUrl && File.Exists(target) ? Path.GetDirectoryName(Path.GetFullPath(target))! : "" });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to open", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
