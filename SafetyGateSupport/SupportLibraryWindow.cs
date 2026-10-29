using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;
using SafetyGateSupport.Siemens;

namespace SafetyGateSupport;

/// <summary>On-demand local library: no vendor programs or document parsing during startup.</summary>
public sealed class SupportLibraryWindow : Window
{
    private readonly DataService _data;
    private readonly TextBox _search = new() { Margin = new Thickness(0, 6, 0, 6), MinHeight = 32 };
    private readonly ListBox _files = new() { DisplayMemberPath = "Display", SelectionMode = SelectionMode.Extended };
    private readonly ComboBox _units = new() { DisplayMemberPath = "AdminDisplay", MinWidth = 300 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly WrapPanel _actions = new();
    private readonly Button _cancel = new() { Content = "Cancel import", IsEnabled = false };
    private List<LibraryFile> _all = new();
    private List<SafetyGate> _gates = new();
    private CancellationTokenSource? _operation;
    private bool _pairUpdateBusy;
    private bool _closeAfterOperation;

    public SupportLibraryWindow(DataService data, SafetyGate? gate = null, SafetySystem? unit = null)
    {
        _data = data;
        Title = "Safety Gate Support — Support library"; Width = 1100; Height = 740; MinWidth = 850; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = new StackPanel(); grid.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Support library", FontSize = 25, FontWeight = FontWeights.Bold });
        top.Children.Add(new TextBlock { Text = "Import the original PDFs, DOCX, .signal, .hux or ZIP files from this PC. ZIP folders remain intact for portable software. Imports never run software or assign equipment automatically.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 5) });
        var toolbar = new WrapPanel(); top.Children.Add(toolbar);
        var import = new Button { Content = "Import files / ZIPs…" }; import.Click += Import_Click; toolbar.Children.Add(import);
        AddButton(toolbar, "Open library folder", () => { var root = SupportLibraryService.Root(_data); Directory.CreateDirectory(root); FileLauncherService.Open(root); });
        AddButton(toolbar, "Launch BusLink…", () => LaunchVendor("BusLink", _data.Settings.BusLinkExePath));
        AddButton(toolbar, "Launch UAM…", () => LaunchVendor("UAM", _data.Settings.UamExePath));
        _cancel.Click += (_, _) => _operation?.Cancel(); toolbar.Children.Add(_cancel);
        top.Children.Add(new TextBlock { Text = "Search filenames, source ZIP names, assets, or units (files are not assigned by filename)." });
        top.Children.Add(_search); _search.TextChanged += (_, _) => Filter();
        Grid.SetRow(_files, 1); grid.Children.Add(_files);
        var bottom = new StackPanel(); Grid.SetRow(bottom, 2); grid.Children.Add(bottom);
        bottom.Children.Add(new TextBlock { Text = "Assign selected configuration / drawing to:", Margin = new Thickness(0, 8, 0, 3) });
        bottom.Children.Add(_units); bottom.Children.Add(_actions);
        AddButton(_actions, "Open selected…", OpenSelected);
        AddButton(_actions, "Open containing folder", () => { if (_files.SelectedItem is LibraryFile f) FileLauncherService.Open(Path.GetDirectoryName(f.FullPath)!); });
        AddButton(_actions, "Assign to selected unit…", AssignSelected);
        AddButton(_actions, "Close", Close); bottom.Children.Add(_status);
        Content = grid;
        Loaded += async (_, _) =>
        {
            try
            {
                var snapshot = await Task.Run(() => { var gates = _data.LoadGates(); return (gates, units: _data.LoadSystems(gates), files: SupportLibraryService.List(SupportLibraryService.Root(_data))); });
                _gates = snapshot.gates; _units.ItemsSource = snapshot.units.OrderBy(u => u.AdminDisplay).ToList();
                _units.SelectedItem = snapshot.units.FirstOrDefault(u => u.Id == unit?.Id);
                _all = snapshot.files; Filter(); _status.Text = $"{_all.Count} files in {_data.SharedRoot}. Select a unit explicitly before assigning. Supplied source filenames are retained.";
            }
            catch (Exception error) { _status.Text = "Unable to load library: " + error.Message; }
        };
        // Disable the import controls together while an operation is running.
        _importToolbar = toolbar;
    }
    private readonly WrapPanel _importToolbar;
    private static void AddButton(Panel parent, string text, Action action)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 6, 8, 0), Padding = new Thickness(10, 7, 10, 7) };
        b.Click += (_, _) => { try { action(); } catch (Exception error) { MessageBox.Show(error.Message, "Support library", MessageBoxButton.OK, MessageBoxImage.Warning); } }; parent.Children.Add(b);
    }
    private void Filter()
    {
        var query = _search.Text.Trim();
        _files.ItemsSource = _all.Where(f => Path.GetExtension(f.FullPath).ToLowerInvariant() is ".signal" or ".hux" or ".pdf" or ".dwg" or ".docx" or ".exe")
            .Where(f => query.Length == 0 || f.Display.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.RelativePath).ToList();
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Import support documents, configuration files, or complete software ZIPs", Filter = "Support files|*.zip;*.pdf;*.dwg;*.docx;*.signal;*.hux" };
        if (dialog.ShowDialog(this) != true) return;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        foreach (UIElement child in _importToolbar.Children) child.IsEnabled = ReferenceEquals(child, _cancel);
        _actions.IsEnabled = false; _units.IsEnabled = false;
        var messages = new List<string>();
        var progress = new Progress<string>(s => _status.Text = "Importing " + s);
        try
        {
            foreach (var path in dialog.FileNames)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    var result = await Task.Run(() => SupportLibraryService.Import(SupportLibraryService.Root(_data), path, cancellation.Token, ((IProgress<string>)progress).Report));
                    messages.Add($"{result.SourceName}: {(result.AlreadyImported ? "already imported" : "imported")}, {result.FileCount} files.");
                }
                catch (Exception error) when (error is not OperationCanceledException) { messages.Add($"{Path.GetFileName(path)}: {error.Message}"); }
            }
        }
        catch (OperationCanceledException) { messages.Add("Canceled. Completed imports remain available; unfinished import was discarded."); }
        finally
        {
            try
            {
                _all = await Task.Run(() => SupportLibraryService.List(SupportLibraryService.Root(_data)));
                // Standalone documents become searchable manuals. ZIP drawings stay unassigned until reviewed.
                await Task.Run(() => IndexStandaloneDocuments()); Filter();
            }
            catch (Exception error) { messages.Add("Unable to refresh/index library: " + error.Message); }
            _status.Text = string.Join("\n", messages);
            foreach (UIElement child in _importToolbar.Children) child.IsEnabled = !ReferenceEquals(child, _cancel);
            _actions.IsEnabled = true; _units.IsEnabled = true; _operation = null;
            if (_closeAfterOperation) Close();
        }
    }
    private void IndexStandaloneDocuments()
    {
        var manuals = _data.LoadManualRecords(); var paths = manuals.Select(m => m.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (var f in _all.Where(f => !f.SourceName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(f.FullPath).ToLowerInvariant() is ".pdf" or ".docx"))
        {
            string path = _data.ToRelative(f.FullPath);
            if (!paths.Add(path)) continue;
            manuals.Add(new ManualRecord { Title = Path.GetFileNameWithoutExtension(f.Name), FilePath = path, Keywords = "Imported documentation " + f.SourceName }); changed = true;
        }
        if (changed) _data.SaveManualRecords(manuals);
    }
    private void OpenSelected()
    {
        if (_files.SelectedItem is not LibraryFile file) return;
        string extension = Path.GetExtension(file.FullPath).ToLowerInvariant();
        if (extension == ".exe" && MessageBox.Show(this, $"Launch this imported vendor application?\n\n{file.FullPath}\n\nOnly launch software from a source you trust.", "Launch vendor application", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (extension is ".signal" or ".hux") { FileLauncherService.Open(Path.GetDirectoryName(file.FullPath)!); return; }
        FileLauncherService.Open(file.FullPath);
    }
    private void LaunchVendor(string kind, string path)
    {
        if (!File.Exists(path))
        {
            var dialog = new OpenFileDialog { Title = "Select installed " + kind + " executable (not an installer)", Filter = "Application (*.exe)|*.exe" };
            if (dialog.ShowDialog(this) != true) return;
            path = dialog.FileName;
            if (kind == "BusLink") _data.Settings.BusLinkExePath = path; else _data.Settings.UamExePath = path;
            _data.SaveLocalSettings();
        }
        FileLauncherService.Open(path);
    }
    private void AssignSelected()
    {
        if (_files.SelectedItem is not LibraryFile file || _units.SelectedItem is not SafetySystem unit) { _status.Text = "Select a file and the exact unit first."; return; }
        var gate = _gates.FirstOrDefault(g => g.Id == unit.GateId) ?? throw new InvalidOperationException("The selected unit has no gate.");
        var selected = _files.SelectedItems.Cast<LibraryFile>().ToArray();
        if (selected.Any(f => Path.GetExtension(f.FullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(f.FullPath).Equals(".dwg", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(gate.AssetFolder)) throw new InvalidOperationException("Organize asset folders in Setup first. Drawing updates now use one PDF/DWG pair in the asset’s Electrical Drawings folder.");
            if (selected.Length != 2 || selected.Count(f => Path.GetExtension(f.FullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) != 1 || selected.Count(f => Path.GetExtension(f.FullPath).Equals(".dwg", StringComparison.OrdinalIgnoreCase)) != 1) throw new InvalidOperationException("Select both the PDF and DWG for this print using Ctrl+click, then assign the pair to the exact unit.");
            var resource = _data.LoadResources().FirstOrDefault(r => r.GateId == gate.Id && r.StorageCategory == "Electrical Drawings") ?? throw new InvalidOperationException("The asset drawing collection is missing. Refresh the storage layout first.");
            if (MessageBox.Show(this, "Assign this PDF/DWG pair to " + gate.AssetDisplay + " / " + unit.UnitDisplay + "?\n\nExisting identical pairs are shared without another copy. To update a previous revision, use Replace selected print pair in its file list.", "Assign print pair", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            AssignPairAsync(resource, gate, unit, selected.Select(f => f.FullPath).ToArray()); return;
        }
        var extension = Path.GetExtension(file.FullPath).ToLowerInvariant();
        string type = extension switch { ".signal" => "Light Configuration", ".hux" => "Scanner Configuration", ".pdf" => "Electrical Drawings", _ => throw new NotSupportedException("Assign a .signal, .hux, or PDF drawing. Other documents remain available from the library.") };
        if (extension == ".signal") PreparedSignalUpload.Load(file.FullPath);
        if (MessageBox.Show(this, $"Assign '{file.RelativePath}' as {type} for\n{gate.Display} / {unit.UnitDisplay}?\n\nConfirm the source documentation identifies this exact unit. This does not program hardware. This links the file to the selected unit. Identical files are reused. To revise an existing sheet, use Replace selected file in its file list; unrelated sheets will not be archived.", "Confirm unit assignment", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        string category = type == "Light Configuration" ? "Light Configurations" : type == "Scanner Configuration" ? "Scanner Configurations" : "Electrical Drawings";
        _data.AssignAssetFile(gate, unit, file.FullPath, category);
        _status.Text = $"Assigned {file.Name} to {gate.AssetDisplay} / {unit.UnitDisplay}. Open the unit resource to compare/upload; no USB device was opened.";
    }
    private async void AssignPairAsync(ResourceDefinition resource, SafetyGate gate, SafetySystem unit, string[] files)
    {
        _pairUpdateBusy = true; IsEnabled = false; _status.Text = "Staging and checking both companions…";
        try
        {
            var result = await Task.Run(() => _data.SaveDrawingPair(resource.ForUnit(unit.Id), gate, unit, files));
            _status.Text = result.ReviewNote.Length > 0 ? result.ReviewNote : "Print pair linked. Use Applies to units to select additional units covered by this same print.";
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Unable to assign print pair", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _pairUpdateBusy = false; IsEnabled = true; }
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_pairUpdateBusy) { e.Cancel = true; _status.Text = "Wait for both drawing companions to finish publishing."; }
        if (_operation is not null) { e.Cancel = true; _closeAfterOperation = true; _operation.Cancel(); _status.Text = "Canceling before closing…"; }
        base.OnClosing(e);
    }
}
