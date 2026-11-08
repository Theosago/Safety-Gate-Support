using System.IO;
using System.Windows;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class AdminWindow
{
    private async void TrySamplePilot_Click(object sender, RoutedEventArgs e)
    {
        DirectoryInfo? workspace = null;
        SetupTabs.IsEnabled = false;
        StartGuideStatus.Text = "Preparing the fictional example…";
        try
        {
            workspace = Directory.CreateTempSubdirectory("SafetyGateDemo-");
            string theme = _data.Settings.ThemeMode;
            var demo = await Task.Run(() => DataService.CreateOnboardingDemo(workspace.FullName, theme));
            if (_isClosed) return;

            new MainWindow(demo) { Owner = this }.ShowDialog();
            if (!_isClosed)
                StartGuideStatus.Text = "Ready to try this with your own equipment? Start with one gate, one photo, and one PDF.";
        }
        catch (Exception error)
        {
            if (!_isClosed) StartGuideStatus.Text = "Could not open the fictional example. " + error.Message;
        }
        finally
        {
            ThemeService.Apply(_data.Settings.ThemeMode == "Dark");
            if (workspace is not null)
            {
                // An external PDF viewer may still hold the fictional file open.
                try { workspace.Delete(recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (!_isClosed) SetupTabs.IsEnabled = true;
        }
    }
}
