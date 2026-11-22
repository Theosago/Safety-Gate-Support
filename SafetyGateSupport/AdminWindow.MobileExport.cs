using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class AdminWindow
{
    private async void ExportPhoneGuides_Click(object sender, RoutedEventArgs e) =>
        await ExportPhoneGuidesAsync(PhoneExportStatus, ExportIncludeNotesCheck.IsChecked == true);

    private async void ExportPilotPhoneGuide_Click(object sender, RoutedEventArgs e)
    {
        if (NeedLibrary() || Pilot() is not { } pilot) return;
        await ExportPhoneGuidesAsync(PilotExportStatus, includeNotes: false, unitId: pilot.Id);
    }

    private async Task ExportPhoneGuidesAsync(TextBlock status, bool includeNotes, string? unitId = null)
    {
        if (!_data.IsReady) { status.Text = "Connect to shared data first."; return; }
        var dialog = new SaveFileDialog { Title = "Export phone guides and QR labels", Filter = "Guide package|*.zip", FileName = unitId is null ? "SafetyGate-Phone-Guides.zip" : "SafetyGate-Pilot.zip" };
        if (dialog.ShowDialog(this) != true) return;
        SetupTabs.IsEnabled = false; status.Text = "Preparing local guide files and labels…";
        try
        {
            var result = await Task.Run(() => MobileSupportExporter.Export(_data, dialog.FileName, includeNotes, RenderMobilePhoto, unitId));
            if (!_isClosed) status.Text = $"Exported {result.Guides} guides and {result.Labels} labels. {(result.Labels == 0 ? "Add offline text or an HTTPS link to generate a QR label. " : "")}Extract the ZIP and open index.html. Nothing was uploaded. " + (result.Warnings.Count > 0 ? $"{result.Warnings.Count} export warnings are listed in READ-ME.txt." : "");
        }
        catch (Exception error) { if (!_isClosed) status.Text = "Export failed: " + error.Message; }
        finally { if (!_isClosed) SetupTabs.IsEnabled = true; }
    }
    private static byte[]? RenderMobilePhoto(string path)
    {
        var result = IdentificationPhotoLoader.LoadAsync(path, 1200).GetAwaiter().GetResult();
        if (result.Source is null) return null;
        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(result.Source));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
