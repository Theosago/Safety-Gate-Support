using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class ResourceWindow : Window
{
    private readonly DataService _data;
    private readonly SafetyGate _gate;
    private readonly SafetySystem _unit;
    private readonly ResourceDefinition _resource;

    public ResourceWindow(DataService data, SafetyGate gate, SafetySystem unit, ResourceDefinition resource)
    {
        InitializeComponent();
        _data = data; _gate = gate; _unit = unit; _resource = resource;
        ResourceTitleText.Text = resource.Label;
        SystemText.Text = $"{gate.Display} - {unit.UnitDisplay}   •   {gate.Category}";
        DescriptionText.Text = string.IsNullOrWhiteSpace(resource.Description) ? DefaultDescription(resource) : resource.Description;
        UpdateButton.Visibility = resource.AllowUserUpload && resource.Kind.Equals("ManagedFiles", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.Content = resource.Label.Contains("Drawing", StringComparison.OrdinalIgnoreCase) ? "Upload / Update Drawing" : "Upload / Update File";
        if (resource.LaunchAction.Equals("Siemens", StringComparison.OrdinalIgnoreCase)) { LaunchButton.Content = "Launch Siemens 8WD46"; LaunchButton.Visibility = Visibility.Visible; }
        else if (resource.LaunchAction.Equals("UAM", StringComparison.OrdinalIgnoreCase)) { LaunchButton.Content = "Launch UAM Project Designer"; LaunchButton.Visibility = Visibility.Visible; }
        else LaunchButton.Visibility = Visibility.Collapsed;
        RefreshFiles();
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
        var files = Directory.GetFiles(folder).Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
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
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private sealed class ResourceFileItem { public string Name { get; set; } = ""; public string Path { get; set; } = ""; public string Modified { get; set; } = ""; public string Size { get; set; } = ""; }
}
