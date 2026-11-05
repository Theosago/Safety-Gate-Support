using System.Windows;
using System.Windows.Controls;
using SafetyGateSupport.Services;
namespace SafetyGateSupport;
public partial class MainWindow
{
    private void InitializeTheme()
    {
        ThemeBox.SelectedIndex = _data.Settings.ThemeMode == "Dark" ? 1 : 0;
        ThemeService.Apply(ThemeBox.SelectedIndex == 1);
    }
    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_data is null) return;
        _data.Settings.ThemeMode = ThemeBox.SelectedIndex == 1 ? "Dark" : "Light";
        ThemeService.Apply(ThemeBox.SelectedIndex == 1);
        try { _data.SaveLocalSettings(); } catch (Exception error) { MessageBox.Show(this, "Appearance preference could not be saved: " + error.Message); }
    }
}
