using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class AdminWindow
{
    private async void ExportPhoneGuides_Click(object sender, RoutedEventArgs e)
    {
        if (!_data.IsReady) { PhoneExportStatus.Text = "Connect to shared data first."; return; }
        var dialog = new SaveFileDialog { Title = "Export phone guides and QR labels", Filter = "Guide package|*.zip", FileName = "SafetyGate-Phone-Guides.zip" };
        if (dialog.ShowDialog(this) != true) return;
        bool notes = ExportIncludeNotesCheck.IsChecked == true;
        SetupTabs.IsEnabled = false; PhoneExportStatus.Text = "Preparing local guide files and labels…";
        try
        {
            var result = await Task.Run(() => MobileSupportExporter.Export(_data, dialog.FileName, notes, RenderMobilePhoto));
            if (!_isClosed) PhoneExportStatus.Text = $"Exported {result.Guides} guides and {result.Labels} labels. Extract the ZIP and open index.html. Nothing was uploaded. " + (result.Warnings.Count > 0 ? $"{result.Warnings.Count} photo warnings are listed in READ-ME.txt." : "");
        }
        catch (Exception error) { if (!_isClosed) PhoneExportStatus.Text = "Export failed: " + error.Message; }
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
