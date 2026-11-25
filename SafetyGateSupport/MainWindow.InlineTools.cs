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
    private async void ShowInlineResource(string category, bool filesOnly = false, bool assetWideDrawings = false)
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
                var column = new SiemensUsbCheck.MainWindow($"{gate.Display} / {unit.UnitDisplay}", paths.Order(StringComparer.OrdinalIgnoreCase).ToArray(), previewOnly: _data.IsHomePreview)
                {
                    RenderPdf = async (pdf, token) => (await DrawingPreviewService.RenderPdfAsync(await DrawingPreviewService.ReadAsync(pdf, token), 0, token)).Image,
                    OpenFile = path => { try { FileLauncherService.Open(path); } catch (Exception error) { MessageBox.Show(this, "Unable to open the PDF: " + error.Message); } }
                };
                var panel = new DockPanel();
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                var upload = new Button { Content = "Upload configuration pair…", ToolTip = "Save a .signal file together with the PDF that shows its configuration" };
                upload.SetResourceReference(StyleProperty, "PrimaryButton");
                upload.Click += (_, _) => UploadSignalPair(gate, unit, column);
                var files = new Button { Content = "Manage saved files…", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Rename, archive or choose which units use each saved configuration" };
                files.SetResourceReference(StyleProperty, "QuietButton");
                files.Click += (_, _) => ShowInlineResource(category, true);
                actions.Children.Add(upload); actions.Children.Add(files);
                DockPanel.SetDock(actions, Dock.Top); panel.Children.Add(actions); panel.Children.Add(column.CreateEmbeddedView(this)); HostInline(column, panel); return;
            }
            var selectedDrawing = assetWideDrawings ? DrawingFileBox.SelectedItem as AssignedDrawings.FileItem : null;
            var assetDrawings = assetWideDrawings
                ? resources.Where(r => r.GateId == gate.Id && r.StorageMode == "Asset" && r.IsVisible && AssignedDrawings.IsDrawing(r)).ToArray()
                : [];
            var resource = assetDrawings.FirstOrDefault(r => r.Id == selectedDrawing?.Resource?.Id)
                ?? assetDrawings.FirstOrDefault() ?? assigned.FirstOrDefault();
            if (resource is null)
            {
                if (category == "Scanner Configurations") { ShowInlineScanner(); return; }
                MessageBox.Show(this, "No file collection is assigned to this unit. Add the collection in Setup."); return;
            }
            var view = new ResourceWindow(_data, gate, unit, resource, selectedDrawing?.Path, assetWideDrawings) { Owner = this };
            view.EmbeddedColumnRequested = () => ShowInlineResource("Light Configurations"); view.EmbeddedScannerRequested = ShowInlineScanner;
            var content = (FrameworkElement)view.Content; view.Content = null; HostInline(view, content);
        }
        catch (Exception error) { MessageBox.Show(this, "Unable to load unit tool: " + error.Message); }
    }
    // A .signal file and the PDF that shows it are uploaded together, like a drawing's PDF and DWG.
    private async void UploadSignalPair(SafetyGate gate, SafetySystem unit, SiemensUsbCheck.MainWindow column)
    {
        if (column.IsEmbeddedBusy) { MessageBox.Show(this, "Finish or cancel the current column operation first."); return; }
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose the .signal file and its PDF", Filter = "Signal configuration and PDF (*.signal;*.pdf)|*.signal;*.pdf", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        var signals = dialog.FileNames.Where(f => Path.GetExtension(f).Equals(".signal", StringComparison.OrdinalIgnoreCase)).ToArray();
        var pdfs = dialog.FileNames.Where(f => Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (signals.Length != 1 || pdfs.Length > 1 || signals.Length + pdfs.Length != dialog.FileNames.Length)
        { MessageBox.Show(this, "Choose one .signal file and the PDF that goes with it (hold Ctrl to pick both).", "Upload configuration pair"); return; }
        string? replacing = null;
        if (column.SelectedConfigurationPath is { } current)
        {
            int choice = ChoiceDialog.Ask(this, "Upload configuration pair", $"What is {Path.GetFileName(signals[0])}?",
                ("A new revision of " + Path.GetFileName(current), "Replaces it everywhere it is used. The previous files are archived."),
                ("A different configuration", "Saves it alongside the current one for " + unit.UnitDisplay + "."));
            if (choice < 0) return;
            if (choice == 0) replacing = current;
        }
        if (pdfs.Length == 0 && MessageBox.Show(this, "No PDF was chosen. Save the configuration without its PDF sheet?", "Upload configuration pair", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            await Task.Run(() => _data.SaveSignalPair(gate, unit, signals[0], pdfs.FirstOrDefault(), replacing));
            if (!_isClosed && SelectedUnit?.Id == unit.Id && ReferenceEquals(_inlineWindow, column)) ShowInlineResource(DataService.SignalCategory);
        }
        catch (Exception error) { MessageBox.Show(this, "The configuration was not saved: " + error.Message, "Upload configuration pair", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void ShowInlineScanner()
    {
        if (SelectedGate is not { } gate || SelectedUnit is not { } unit || !CloseInlineTool()) return;
        var scanner = new UamDiagnosticsWindow(_data, gate, unit); HostInline(scanner, scanner.CreateEmbeddedView(this));
    }
}
