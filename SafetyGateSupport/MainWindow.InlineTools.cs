using System.IO;
using System.Windows;
using System.Windows.Controls;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;
namespace SafetyGateSupport;
public partial class MainWindow
{
    private Window? _inlineWindow;
    private bool InlineBusy => _inlineWindow is SiemensUsbCheck.MainWindow c && c.IsEmbeddedBusy || _inlineWindow is UamDiagnosticsWindow s && s.IsEmbeddedBusy || _inlineWindow is ResourceWindow r && r.IsEmbeddedBusy;
    private bool CloseInlineTool()
    {
        if (InlineBusy) { MessageBox.Show(this, "Finish or cancel the current operation before leaving this tool."); return false; }
        var old = _inlineWindow; _inlineWindow = null; InlineToolContent.Content = null; old?.Close();
        InlineToolPanel.Visibility = Visibility.Collapsed; UnitOverview.Visibility = Visibility.Visible;
        return true;
    }
    private void UnitOverview_Click(object sender, RoutedEventArgs e) { if (CloseInlineTool() && SelectedGate is { } g && SelectedUnit is { } u) LoadDrawings(g, u); }
    private void HostInline(Window window, FrameworkElement content)
    {
        _inlineWindow = window; InlineToolContent.Content = content;
        UnitOverview.Visibility = Visibility.Collapsed; InlineToolPanel.Visibility = Visibility.Visible;
        window.Closed += (_, _) => { if (ReferenceEquals(_inlineWindow, window)) { _inlineWindow = null; InlineToolContent.Content = null; InlineToolPanel.Visibility = Visibility.Collapsed; UnitOverview.Visibility = Visibility.Visible; } };
    }
    private async void ShowInlineResource(string category, bool filesOnly = false)
    {
        if (SelectedGate is not { } gate || SelectedUnit is not { } unit || !CloseInlineTool()) return;
        try
        {
            var resources = await Task.Run(_data.LoadResources);
            if (_isClosed || SelectedUnit?.Id != unit.Id) return;
            var assigned = _data.ResourcesFor(gate, unit, resources).Where(r => AssetFileLayout.Category(r) == category).ToList();
            if (category == "Light Configurations" && !filesOnly)
            {
                var paths = await Task.Run(() => assigned.SelectMany(r => Directory.Exists(_data.ResourceCurrentFolder(r, gate)) ? _data.GetResourceFiles(r, gate) : Array.Empty<string>()).Where(p => Path.GetExtension(p).Equals(".signal", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
                if (_isClosed || SelectedUnit?.Id != unit.Id) return;
                var column = new SiemensUsbCheck.MainWindow($"{gate.Display} / {unit.UnitDisplay}", paths, previewOnly: _data.IsHomePreview);
                var panel = new DockPanel(); var files = new Button { Content = "Add / update saved column configurations…", HorizontalAlignment = HorizontalAlignment.Left };
                files.Click += (_, _) => ShowInlineResource(category, true); DockPanel.SetDock(files, Dock.Top); panel.Children.Add(files); panel.Children.Add(column.CreateEmbeddedView(this)); HostInline(column, panel); return;
            }
            if (assigned.FirstOrDefault() is not { } resource)
            {
                if (category == "Scanner Configurations") { ShowInlineScanner(); return; }
                MessageBox.Show(this, "No file collection is assigned to this unit. Add the collection in Setup."); return;
            }
            var view = new ResourceWindow(_data, gate, unit, resource) { Owner = this };
            view.EmbeddedColumnRequested = () => ShowInlineResource("Light Configurations"); view.EmbeddedScannerRequested = ShowInlineScanner;
            var content = (FrameworkElement)view.Content; view.Content = null; HostInline(view, content);
        }
        catch (Exception error) { MessageBox.Show(this, "Unable to load unit tool: " + error.Message); }
    }
    private void ShowInlineScanner()
    {
        if (SelectedGate is not { } gate || SelectedUnit is not { } unit || !CloseInlineTool()) return;
        var scanner = new UamDiagnosticsWindow(_data, gate, unit); HostInline(scanner, scanner.CreateEmbeddedView(this));
    }
}
