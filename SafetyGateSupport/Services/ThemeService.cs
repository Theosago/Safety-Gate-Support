using System.Windows;
using System.Windows.Media;
namespace SafetyGateSupport.Services;
public static class ThemeService
{
// Light values match Themes/Modern.xaml; dark values keep surfaces lighter than the window
// background and text at readable contrast. Semantic keys (Surface, Ink, AccentBrush…) drive the
// shared control styles; the older colour-named keys remain for existing layouts.
private static readonly Dictionary<string, (string Light, string Dark)> Palette = new() {
["ThemeForeground555555"] = ("#555555", "#A9BBCB"),
["ThemeForeground61788A"] = ("#61788A", "#DCE7F1"),
["ThemeBackgroundF7F9FB"] = ("#F7F9FB", "#1D2D3B"),
["ThemeBorderBrushD6DCE2"] = ("#D6DCE2", "#33495C"),
["ThemeForeground666666"] = ("#666666", "#A9BBCB"),
["ThemeBackgroundECEFF2"] = ("#ECEFF2", "#1D2D3B"),
["ThemeBorderBrushCDD3D9"] = ("#CDD3D9", "#33495C"),
["ThemeBackgroundF4F7FA"] = ("#F4F7FA", "#1D2D3B"),
["ThemeForeground444444"] = ("#444444", "#E4EDF5"),
["ThemeForeground777777"] = ("#777777", "#A9BBCB"),
["ThemeForeground1769AA"] = ("#1769AA", "#81B8E4"),
["ThemeBackgroundFAFBFC"] = ("#FAFBFC", "#1D2D3B"),
["ThemeForeground333333"] = ("#333333", "#E4EDF5"),
["ThemeBackgroundF3F6FA"] = ("#F3F6FA", "#0F1922"),
["ThemeForeground172D40"] = ("#172D40", "#E4EDF5"),
["ThemeBorderBrushD5DFE8"] = ("#D5DFE8", "#33495C"),
["ThemeForeground617585"] = ("#617585", "#A9BBCB"),
["ThemeForegroundAEC4D3"] = ("#AEC4D3", "#A9BBCB"),
["ThemeForeground999999"] = ("#999999", "#A9BBCB"),
["ThemeBackgroundFFFFFF"] = ("#FFFFFF", "#182633"),
["ThemeForeground536E81"] = ("#536E81", "#DCE7F1"),
["ThemeBackgroundEDF1F4"] = ("#EDF1F4", "#1D2D3B"),
["ThemeForeground176B87"] = ("#176B87", "#81CBE4"),
["ThemeForeground965813"] = ("#965813", "#E4B581"),
["ThemeBackgroundE9EEF2"] = ("#E9EEF2", "#1D2D3B"),
["ThemeForeground566D7C"] = ("#566D7C", "#A9BBCB"),
["ThemeBackgroundEDF1F5"] = ("#EDF1F5", "#1D2D3B"),
["ThemeForeground8293A1"] = ("#8293A1", "#A9BBCB"),
["ThemeForeground1B637A"] = ("#1B637A", "#82CCE3"),
["ThemeForeground687A88"] = ("#687A88", "#A9BBCB"),
["ThemeBackgroundCED8E1"] = ("#CED8E1", "#2A4054"),
["ThemeBackgroundF7F9FC"] = ("#F7F9FC", "#1D2D3B"),
["ThemeBackgroundEAF2F7"] = ("#EAF2F7", "#1D2D3B"),
["ThemeBorderBrushC8C8C8"] = ("#C8C8C8", "#33495C"),
["ThemeBackgroundF7F7F7"] = ("#F7F7F7", "#1D2D3B"),
["ThemeBackgroundFFF8E6"] = ("#FFF8E6", "#2E2A1E"),
["ThemeBorderBrushE1C36A"] = ("#E1C36A", "#33495C"),
["ThemeForegroundBCD0DE"] = ("#BCD0DE", "#A9BBCB"),
["ThemeBackgroundEAF3F7"] = ("#EAF3F7", "#1D2D3B"),
["ThemeForeground6C8090"] = ("#6C8090", "#A9BBCB"),
["ThemeBackgroundF5F7FA"] = ("#F5F7FA", "#0F1922"),
["ThemeForeground14263D"] = ("#14263D", "#E4EDF5"),
["ThemeForeground394C60"] = ("#394C60", "#E4EDF5"),
["ThemeBorderBrushD6DEE8"] = ("#D6DEE8", "#33495C"),
["ThemeForeground88520A"] = ("#88520A", "#E4BA81"),
["ThemeBorderBrushDFE6ED"] = ("#DFE6ED", "#33495C"),
["ThemeForeground405D70"] = ("#405D70", "#DCE7F1"),
["ThemeForeground18384D"] = ("#18384D", "#E4EDF5"),
["ThemeBorderBrushCDD9E3"] = ("#CDD9E3", "#33495C"),
["ThemeBorderBrush17859C"] = ("#17859C", "#2C93B8"),
["ThemeBorderBrush176B87"] = ("#176B87", "#2C93B8"),
["ThemeBorderBrush179CB5"] = ("#179CB5", "#2C93B8"),
["AppBackground"] = ("#F3F6F9", "#0F1922"),
["Surface"] = ("#FFFFFF", "#182633"),
["SurfaceAlt"] = ("#F6F8FA", "#1D2D3B"),
["SurfaceHover"] = ("#EEF3F7", "#223546"),
["SurfacePressed"] = ("#E2EAF0", "#2A4054"),
["Line"] = ("#DCE3EA", "#2B3E50"),
["LineStrong"] = ("#C5D0DA", "#3A5166"),
["Ink"] = ("#16293A", "#E4EDF5"),
["InkMuted"] = ("#56697A", "#A9BBCB"),
["InkSubtle"] = ("#8396A6", "#7590A6"),
["AccentBrush"] = ("#176B87", "#2C93B8"),
["AccentHover"] = ("#125A72", "#3AA3C8"),
["AccentPressed"] = ("#0E4A5E", "#237C9C"),
["AccentSoft"] = ("#E5F1F6", "#173B4C"),
["AccentInk"] = ("#125A72", "#7DCBE6"),
["OnAccent"] = ("#FFFFFF", "#FFFFFF"),
["Danger"] = ("#B42318", "#F07167"),
["DangerSoft"] = ("#FDEDEB", "#3A1F22"),
["FocusRing"] = ("#2E8DB0", "#4FB3D6"),
["NavInk"] = ("#D5E3EE", "#C9D8E4"),
["NavHover"] = ("#1A3448", "#16293A"),
["NavActive"] = ("#21455E", "#1F3F55"),
["TooltipBackground"] = ("#16293A", "#E4EDF5"),
["TooltipInk"] = ("#F2F6F9", "#0F1922"),
};
public static void Apply(bool dark)
{
foreach (var (key, value) in Palette) Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? value.Dark : value.Light));
var surface = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#182633" : "#FFFFFF"));
var text = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#E4EDF5" : "#16293A"));
foreach (var key in new[] { SystemColors.WindowBrushKey, SystemColors.ControlBrushKey }) Application.Current.Resources[key] = surface;
foreach (var key in new[] { SystemColors.WindowTextBrushKey, SystemColors.ControlTextBrushKey }) Application.Current.Resources[key] = text;
}
}
