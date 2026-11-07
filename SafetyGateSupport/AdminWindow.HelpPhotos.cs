using System.IO;
using System.Windows;
using Microsoft.Win32;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class AdminWindow
{
    private void NewCommonProblem_Click(object sender, RoutedEventArgs e)
    { NewHelp_Click(sender, e); HelpSectionBox.SelectedIndex = 2; }
    private string _pendingHelpPhoto = "";
    private bool _removeHelpPhoto;
    private int _helpPhotoRevision;
    private void ChooseHelpPhoto_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Choose a problem photo", Filter = "Photos|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff" };
        if (picker.ShowDialog(this) != true) return;
        if (new FileInfo(picker.FileName).Length > 64 * 1024 * 1024) { HelpPhotoStatus.Text = "Choose a photo smaller than 64 MB."; return; }
        _pendingHelpPhoto = picker.FileName; _removeHelpPhoto = false; RefreshHelpPhoto();
    }
    private void RemoveHelpPhoto_Click(object sender, RoutedEventArgs e)
    { _pendingHelpPhoto = ""; _removeHelpPhoto = true; RefreshHelpPhoto(); }
    private async void RefreshHelpPhoto()
    {
        int revision = ++_helpPhotoRevision; HelpPhotoPreview.Source = null;
        string path = _removeHelpPhoto ? "" : _pendingHelpPhoto.Length > 0 ? _pendingHelpPhoto : _selectedHelp?.PhotoPath ?? "";
        HelpPhotoStatus.Text = path.Length == 0 ? "No photo selected." : "Loading photo…";
        if (path.Length == 0) return;
        try
        {
            var result = await IdentificationPhotoLoader.LoadAsync(_data.Resolve(path), 640);
            if (_isClosed || revision != _helpPhotoRevision) return;
            HelpPhotoPreview.Source = result.Source;
            HelpPhotoStatus.Text = result.Source is null ? "Photo cannot be read. Choose a replacement." : _pendingHelpPhoto.Length > 0 ? "Photo will copy to shared data when saved." : "Shared photo loaded.";
        }
        catch (Exception error) { if (!_isClosed && revision == _helpPhotoRevision) HelpPhotoStatus.Text = "Photo unavailable: " + error.Message; }
    }
}
