using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using Microsoft.Win32;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class ResourceWindow : Window
{
    private bool _fileUpdateBusy;
    private readonly DataService _data;
    private readonly SafetyGate _gate;
    private readonly SafetySystem _unit;
    private readonly ResourceDefinition _resource;

    public ResourceWindow(DataService data, SafetyGate gate, SafetySystem unit, ResourceDefinition resource, string? preselectedFile = null)
    {
        InitializeComponent();
        resource = data.LoadResources().FirstOrDefault(r => r.Id == resource.Id) ?? resource;
        _data = data; _gate = gate; _unit = unit; _resource = resource.ForUnit(unit.Id);
        ResourceTitleText.Text = resource.Label;
        SystemText.Text = $"{gate.Display} - {unit.UnitDisplay}   •   {gate.Category}";
        DescriptionText.Text = string.IsNullOrWhiteSpace(resource.Description) ? DefaultDescription(resource) : resource.Description;
        UpdateButton.Visibility = resource.AllowUserUpload && resource.Kind.Equals("ManagedFiles", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.Content = resource.StorageMode == "Asset" ? "Add files…" : resource.Label.Contains("Drawing", StringComparison.OrdinalIgnoreCase) ? "Upload / Update Drawing" : "Upload / Update File";
        if (AssignedDrawings.IsDrawing(resource)) { UpdateButton.Content = "Add PDF + DWG…"; ReplaceFileButton.Content = "Replace selected print pair…"; }
        ReplaceFileButton.Visibility = resource.StorageMode == "Asset" && resource.AllowUserUpload ? Visibility.Visible : Visibility.Collapsed;
        FileUnitsButton.Visibility = resource.StorageMode == "Asset" ? Visibility.Visible : Visibility.Collapsed;
        if (resource.LaunchAction.Equals("Siemens", StringComparison.OrdinalIgnoreCase)) { LaunchButton.Content = "Launch Siemens 8WD46"; LaunchButton.Visibility = Visibility.Visible; }
        else if (resource.LaunchAction.Equals("UAM", StringComparison.OrdinalIgnoreCase)) { LaunchButton.Content = "Launch UAM Project Designer"; LaunchButton.Visibility = Visibility.Visible; }
        else LaunchButton.Visibility = Visibility.Collapsed;
        SignalColumnButton.Visibility = SignalColumnService.IsLightResource(resource) ? Visibility.Visible : Visibility.Collapsed;
        UamDiagnosticsButton.Visibility = resource.LaunchAction.Equals("UAM", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        RefreshFiles();
        if (preselectedFile is not null) FileList.SelectedItem = FileList.Items.Cast<ResourceFileItem>().FirstOrDefault(f => f.Path.Equals(preselectedFile, StringComparison.OrdinalIgnoreCase));
    }

    private static string DefaultDescription(ResourceDefinition r) => r.Kind switch
    {
        "Photos" => "Photos linked to this gate/unit.",
        "ManagedFiles" => "Current files linked to this gate/unit. Previous revisions are kept in the archive when updating is enabled.",
        _ => "Files and information linked to this gate/unit."
    };

    private string CurrentFolder()
    {
        if (_resource.Kind.Equals("Photos", StringComparison.OrdinalIgnoreCase)) return _data.ResourceBaseFolder(_resource, _gate);
        return _data.ResourceCurrentFolder(_resource, _gate);
    }

    private string ArchiveFolder() => _data.ResourceArchiveFolder(_resource, _gate);

    private void RefreshFiles()
    {
        _data.EnsureResourceFolders(_resource, _gate);
        var folder = CurrentFolder(); Directory.CreateDirectory(folder);
        var files = _data.GetResourceFiles(_resource, _gate).Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .Select(f => new ResourceFileItem { Name = Path.GetFileName(f), Path = f, Modified = File.GetLastWriteTime(f).ToString("g"), Size = FormatSize(new FileInfo(f).Length) }).ToList();
        FileList.ItemsSource = files; EmptyText.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = files.Count == 0 ? "No current file is assigned." : $"{files.Count} current file(s). Double-click a file to open it.";
    }

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024L ? $"{bytes / (1024d * 1024d):0.0} MB" : bytes >= 1024L ? $"{bytes / 1024d:0.0} KB" : $"{bytes} B";
    private void FileList_DoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();
    private void OpenSelected_Click(object sender, RoutedEventArgs e) => OpenSelected();
    private void OpenSelected() { if (FileList.SelectedItem is ResourceFileItem item && File.Exists(item.Path)) FileLauncherService.Open(item.Path); }
    private void OpenCurrentFolder_Click(object sender, RoutedEventArgs e) { var folder = CurrentFolder(); Directory.CreateDirectory(folder); FileLauncherService.Open(folder); }
    private void OpenArchive_Click(object sender, RoutedEventArgs e) { var folder = ArchiveFolder(); Directory.CreateDirectory(folder); FileLauncherService.Open(folder); }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (!_resource.AllowUserUpload) return;
        if (AssignedDrawings.IsDrawing(_resource)) { UploadDrawingPair(null); return; }
        var filter = _resource.Label.Contains("Drawing", StringComparison.OrdinalIgnoreCase)
            ? "Drawings|*.pdf;*.dwg;*.dxf|PDF|*.pdf|AutoCAD|*.dwg;*.dxf|All files|*.*"
            : "All files|*.*";
        var dialog = new OpenFileDialog { Title = $"Upload {_resource.Label} for {_gate.Display} / {_unit.UnitDisplay}", Filter = filter, Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        var names = string.Join("\n", dialog.FileNames.Select(Path.GetFileName));
        var applies = _resource.AppliesToUnitIds.Count > 1
            ? "\n\nThis resource is shared by multiple units. The update will be visible from every linked unit."
            : "";
        if (MessageBox.Show($"Update {_resource.Label}?\n\n{names}{applies}\n\nMatching current files will be archived according to this resource's archive rule.", "Confirm update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { _data.AddOrUpdateResourceFiles(_resource, _gate, _unit, dialog.FileNames); RefreshFiles(); StatusText.Text = "Update complete. Previous matching file(s) were archived."; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Unable to update resource", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ReplaceFile_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not ResourceFileItem item) { StatusText.Text = "Select the sheet or file you are replacing first."; return; }
        if (AssignedDrawings.IsDrawing(_resource)) { UploadDrawingPair(item.Path); return; }
        var dialog = new OpenFileDialog { Title = "Choose the replacement for " + item.Name, Multiselect = false, Filter = "All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, $"Replace '{item.Name}' with '{Path.GetFileName(dialog.FileName)}'?\n\nThe previous file will be archived. Every unit linked to this same file will use the replacement; unrelated sheets stay current.", "Replace selected file", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { _data.ReplaceAssetFile(_resource, _gate, item.Path, dialog.FileName); RefreshFiles(); StatusText.Text = "Replacement saved. Previous revision archived; existing unit links preserved."; }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Unable to replace file", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void UploadDrawingPair(string? replacing)
    {
        if (_fileUpdateBusy) return;
        var dialog = new OpenFileDialog { Title = replacing is null ? "Select the PDF and DWG for one print" : "Select both replacement companions", Multiselect = true, Filter = "Print pair (PDF + DWG)|*.pdf;*.dwg" };
        if (dialog.ShowDialog(this) != true) return;
        if (dialog.FileNames.Length != 2 || dialog.FileNames.Count(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) != 1 || dialog.FileNames.Count(p => Path.GetExtension(p).Equals(".dwg", StringComparison.OrdinalIgnoreCase)) != 1)
        { MessageBox.Show(this, "Select one PDF and its corresponding DWG together. The missing companion must be included before this print can be published.", "PDF/DWG companion required", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        string action = replacing is null ? "Add this print pair?" : "Replace the selected print pair?";
        if (MessageBox.Show(this, action + "\n\n" + string.Join("\n", dialog.FileNames.Select(Path.GetFileName)) + "\n\nBoth companions will be published together. Replacing a shared print retains all its existing unit links and archives its previous files. Other prints are unchanged.", "Publish PDF + DWG", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _fileUpdateBusy = true; IsEnabled = false; StatusText.Text = "Staging and verifying both drawing files…";
        try
        {
            var result = await System.Threading.Tasks.Task.Run(() => _data.SaveDrawingPair(_resource, _gate, _unit, dialog.FileNames, replacing));
            RefreshFiles(); StatusText.Text = result.ReviewNote.Length == 0 ? "PDF and DWG published together; unit links retained." : result.ReviewNote + " Verify the corresponding export; this warning is also shown beside the drawing.";
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Drawing pair not published", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _fileUpdateBusy = false; IsEnabled = true; }
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_fileUpdateBusy) { e.Cancel = true; StatusText.Text = "Wait for the paired drawing update to finish before closing."; }
        base.OnClosing(e);
    }

    private void FileUnits_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not ResourceFileItem item) { StatusText.Text = "Select a file to view or change the units it covers."; return; }
        var record = _data.LoadResources().Single(r => r.Id == _resource.Id);
        var linked = record.FileAssignments.First(p => p.Key.Equals(item.Name, StringComparison.OrdinalIgnoreCase)).Value;
        var window = new Window { Owner = this, Title = "Units covered by " + item.Name, Width = 540, Height = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(20) }; var save = new Button { Content = "Save unit links", HorizontalAlignment = HorizontalAlignment.Right };
        var info = new TextBlock { Text = "Select only the units covered by this file. A shared drawing stays one physical file. Other units keep their own drawings.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(info, Dock.Top); panel.Children.Add(info); DockPanel.SetDock(save, Dock.Bottom); panel.Children.Add(save);
        var choices = new StackPanel(); var checks = new List<(SafetySystem Unit, System.Windows.Controls.CheckBox Check)>();
        foreach (var unit in _data.LoadSystems().Where(u => u.GateId == _gate.Id).OrderBy(u => int.TryParse(u.UnitNumber, out var n) ? n : int.MaxValue))
        {
            var check = new System.Windows.Controls.CheckBox { Content = unit.UnitDisplay, IsChecked = linked.Count == 0 || linked.Contains(unit.Id), Margin = new Thickness(4, 8, 4, 8) };
            checks.Add((unit, check)); choices.Children.Add(check);
        }
        panel.Children.Add(new System.Windows.Controls.ScrollViewer { Content = choices, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto }); window.Content = panel;
        save.Click += (_, _) =>
        {
            try { _data.SetAssetFileUnits(_resource, _gate, item.Name, checks.Where(c => c.Check.IsChecked == true).Select(c => c.Unit.Id)); window.Close(); RefreshFiles(); StatusText.Text = "Unit links saved. The file was not duplicated."; }
            catch (Exception error) { MessageBox.Show(window, error.Message, "Unit links", MessageBoxButton.OK, MessageBoxImage.Information); }
        };
        window.ShowDialog();
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_resource.LaunchAction.Equals("Siemens", StringComparison.OrdinalIgnoreCase)) LaunchSoftware(_data.Settings.SiemensExePath, "Siemens 8WD46 Configurator");
        else if (_resource.LaunchAction.Equals("UAM", StringComparison.OrdinalIgnoreCase)) LaunchSoftware(_data.Settings.UamExePath, "UAM Project Designer");
    }
    private static void LaunchSoftware(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { MessageBox.Show($"{name} has not been configured on this computer.\n\nUse hidden setup mode to configure the executable path.", "Software path not configured", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        FileLauncherService.Open(path);
    }
    private void UamDiagnostics_Click(object sender, RoutedEventArgs e) => new UamDiagnosticsWindow(_data, _gate, _unit) { Owner = this }.ShowDialog();
    private async void SignalColumn_Click(object sender, RoutedEventArgs e) => await SignalColumnService.OpenAsync(this, _data, _gate, _unit, new[] { _resource });
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private sealed class ResourceFileItem { public string Name { get; set; } = ""; public string Path { get; set; } = ""; public string Modified { get; set; } = ""; public string Size { get; set; } = ""; }
}
