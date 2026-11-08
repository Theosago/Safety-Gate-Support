using System.IO;
using System.Windows;
using Microsoft.Win32;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class AdminWindow
{
    private void NewCommonProblem_Click(object sender, RoutedEventArgs e)
    { NewHelp_Click(sender, e); HelpSectionBox.SelectedIndex = 2; }
    private sealed record HelpPhotoChoice(string Path, string Label);
    private List<HelpPhotoChoice> _helpPhotos = new();
    private int _helpPhotoRevision;
    private void ResetHelpPhotos()
    {
        _helpPhotos = (_selectedHelp?.Photos ?? []).Select((p, i) => new HelpPhotoChoice(p, "Photo " + (i + 1))).ToList();
        RefreshHelpPhotoChoices();
    }
    private void RefreshHelpPhotoChoices()
    {
        HelpPhotoList.ItemsSource = null; HelpPhotoList.ItemsSource = _helpPhotos;
        HelpPhotoList.SelectedIndex = _helpPhotos.Count > 0 ? 0 : -1; RefreshHelpPhoto();
    }
    private void ChooseHelpPhoto_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Add problem photos", Filter = "Photos|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        if (_helpPhotos.Count + picker.FileNames.Length > 20) { HelpPhotoStatus.Text = "Choose at most 20 photos per problem."; return; }
        if (picker.FileNames.Any(p => new FileInfo(p).Length > 64 * 1024 * 1024)) { HelpPhotoStatus.Text = "Choose photos smaller than 64 MB each."; return; }
        foreach (string path in picker.FileNames) if (!_helpPhotos.Any(p => p.Path == path)) _helpPhotos.Add(new(path, Path.GetFileName(path)));
        RefreshHelpPhotoChoices();
    }
    private void RemoveHelpPhoto_Click(object sender, RoutedEventArgs e)
    { if (HelpPhotoList.SelectedItem is HelpPhotoChoice photo) _helpPhotos.Remove(photo); RefreshHelpPhotoChoices(); }
    private void CoverHelpPhoto_Click(object sender, RoutedEventArgs e)
    { if (HelpPhotoList.SelectedItem is HelpPhotoChoice photo) { _helpPhotos.Remove(photo); _helpPhotos.Insert(0, photo); RefreshHelpPhotoChoices(); } }
    private void HelpPhoto_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshHelpPhoto();
    private async void RefreshHelpPhoto()
    {
        if (HelpPhotoPreview is null) return;
        int revision = ++_helpPhotoRevision; HelpPhotoPreview.Source = null;
        string path = (HelpPhotoList.SelectedItem as HelpPhotoChoice)?.Path ?? "";
        HelpPhotoStatus.Text = path.Length == 0 ? "No photo selected." : "Loading photo…";
        if (path.Length == 0) return;
        try
        {
            var result = await IdentificationPhotoLoader.LoadAsync(_data.Resolve(path), 640);
            if (_isClosed || revision != _helpPhotoRevision) return;
            HelpPhotoPreview.Source = result.Source;
            HelpPhotoStatus.Text = result.Source is null ? "Photo cannot be read. Choose a replacement." : "First photo is the cover. Changes copy to shared data when saved.";
        }
        catch (Exception error) { if (!_isClosed && revision == _helpPhotoRevision) HelpPhotoStatus.Text = "Photo unavailable: " + error.Message; }
    }
}
