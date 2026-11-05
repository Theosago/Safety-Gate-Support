using System.Windows;
using System.Windows.Media;
namespace SafetyGateSupport.Services;
public static class ThemeService
{
private static readonly Dictionary<string, (string Light, string Dark)> Palette = new() {
["ThemeForeground555555"] = ("#555555", "#DCE7F1"),
["ThemeForeground61788A"] = ("#61788A", "#DCE7F1"),
["ThemeBackgroundF7F9FB"] = ("#F7F9FB", "#1C2938"),
["ThemeBorderBrushD6DCE2"] = ("#D6DCE2", "#50657A"),
["ThemeForeground666666"] = ("#666666", "#DCE7F1"),
["ThemeBackgroundECEFF2"] = ("#ECEFF2", "#263748"),
["ThemeBorderBrushCDD3D9"] = ("#CDD3D9", "#50657A"),
["ThemeBackgroundF4F7FA"] = ("#F4F7FA", "#1C2938"),
["ThemeForeground444444"] = ("#444444", "#DCE7F1"),
["ThemeForeground777777"] = ("#777777", "#DCE7F1"),
["ThemeForeground1769AA"] = ("#1769AA", "#81B8E4"),
["ThemeBackgroundFAFBFC"] = ("#FAFBFC", "#1C2938"),
["ThemeForeground333333"] = ("#333333", "#DCE7F1"),
["ThemeBackgroundF3F6FA"] = ("#F3F6FA", "#1C2938"),
["ThemeForeground172D40"] = ("#172D40", "#8EB5D7"),
["ThemeBorderBrushD5DFE8"] = ("#D5DFE8", "#50657A"),
["ThemeForeground617585"] = ("#617585", "#DCE7F1"),
["ThemeForegroundAEC4D3"] = ("#AEC4D3", "#DCE7F1"),
["ThemeForeground999999"] = ("#999999", "#DCE7F1"),
["ThemeBackgroundFFFFFF"] = ("#FFFFFF", "#1C2938"),
["ThemeForeground536E81"] = ("#536E81", "#DCE7F1"),
["ThemeBackgroundEDF1F4"] = ("#EDF1F4", "#1C2938"),
["ThemeForeground176B87"] = ("#176B87", "#81CBE4"),
["ThemeForeground965813"] = ("#965813", "#E4B581"),
["ThemeBackgroundE9EEF2"] = ("#E9EEF2", "#263748"),
["ThemeForeground566D7C"] = ("#566D7C", "#DCE7F1"),
["ThemeBackgroundEDF1F5"] = ("#EDF1F5", "#1C2938"),
["ThemeForeground8293A1"] = ("#8293A1", "#DCE7F1"),
["ThemeForeground1B637A"] = ("#1B637A", "#82CCE3"),
["ThemeForeground687A88"] = ("#687A88", "#DCE7F1"),
["ThemeBackgroundCED8E1"] = ("#CED8E1", "#263748"),
["ThemeBackgroundF7F9FC"] = ("#F7F9FC", "#1C2938"),
["ThemeBackgroundEAF2F7"] = ("#EAF2F7", "#1C2938"),
["ThemeBorderBrushC8C8C8"] = ("#C8C8C8", "#50657A"),
["ThemeBackgroundF7F7F7"] = ("#F7F7F7", "#1C2938"),
["ThemeBackgroundFFF8E6"] = ("#FFF8E6", "#1C2938"),
["ThemeBorderBrushE1C36A"] = ("#E1C36A", "#50657A"),
["ThemeForegroundBCD0DE"] = ("#BCD0DE", "#DCE7F1"),
["ThemeBackgroundEAF3F7"] = ("#EAF3F7", "#1C2938"),
["ThemeForeground6C8090"] = ("#6C8090", "#DCE7F1"),
["ThemeBackgroundF5F7FA"] = ("#F5F7FA", "#1C2938"),
["ThemeForeground14263D"] = ("#14263D", "#8CAED9"),
["ThemeForeground394C60"] = ("#394C60", "#DCE7F1"),
["ThemeBorderBrushD6DEE8"] = ("#D6DEE8", "#50657A"),
["ThemeForeground88520A"] = ("#88520A", "#E4BA81"),
["ThemeBorderBrushDFE6ED"] = ("#DFE6ED", "#50657A"),
["ThemeForeground405D70"] = ("#405D70", "#DCE7F1"),
["ThemeForeground18384D"] = ("#18384D", "#8ABBDB"),
["ThemeBorderBrushCDD9E3"] = ("#CDD9E3", "#50657A"),
["ThemeBorderBrush17859C"] = ("#17859C", "#50657A"),
["ThemeBorderBrush176B87"] = ("#176B87", "#50657A"),
["ThemeBorderBrush179CB5"] = ("#179CB5", "#50657A"),
};
public static void Apply(bool dark)
{
foreach (var (key, value) in Palette) Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? value.Dark : value.Light));
var surface = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#1C2938" : "#FFFFFF"));
var text = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#DCE7F1" : "#172D40"));
foreach (var key in new[] { SystemColors.WindowBrushKey, SystemColors.ControlBrushKey }) Application.Current.Resources[key] = surface;
foreach (var key in new[] { SystemColors.WindowTextBrushKey, SystemColors.ControlTextBrushKey }) Application.Current.Resources[key] = text;
}
}
