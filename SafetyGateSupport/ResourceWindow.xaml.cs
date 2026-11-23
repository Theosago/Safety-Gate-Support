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
    public bool IsEmbeddedBusy => _fileUpdateBusy;
    public Action? EmbeddedColumnRequested { get; set; }
    public Action? EmbeddedScannerRequested { get; set; }
    private bool _fileUpdateBusy;
    private readonly DataService _data;
    private readonly SafetyGate _gate;
    private readonly SafetySystem _unit;
    private readonly ResourceDefinition _resource;
    private readonly bool _assetWideDrawings;
    internal sealed record DrawingUploadReceipt(string Status, IReadOnlyList<string> UnitIds);

    public ResourceWindow(DataService data, SafetyGate gate, SafetySystem unit, ResourceDefinition resource, string? preselectedFile = null, bool assetWideDrawings = false)
    {
        InitializeComponent();
        resource = data.LoadResources().FirstOrDefault(r => r.Id == resource.Id) ?? resource;
        _assetWideDrawings = assetWideDrawings && resource.StorageMode == "Asset" && AssignedDrawings.IsDrawing(resource);
        _data = data;
        _gate = gate;
        _unit = unit;
        _resource = resource.ForUnit(_assetWideDrawings ? "" : unit.Id);
        ResourceTitleText.Text = _assetWideDrawings ? $"{gate.AssetDisplay} drawings" : resource.Label;
        SystemText.Text = _assetWideDrawings ? $"{gate.Display}   •   {gate.Category}" : $"{gate.Display} - {unit.UnitDisplay}   •   {gate.Category}";
        DescriptionText.Text = _assetWideDrawings ? "Select a drawing to see or change which units it covers."
            : string.IsNullOrWhiteSpace(resource.Description) ? DefaultDescription(resource) : resource.Description;
        UpdateButton.Visibility = resource.AllowUserUpload && resource.Kind.Equals("ManagedFiles", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.Content = resource.StorageMode == "Asset" ? "Add files…" : resource.Label.Contains("Drawing", StringComparison.OrdinalIgnoreCase) ? "Upload / Update Drawing" : "Upload / Update File";
        if (AssignedDrawings.IsDrawing(resource))
        {
            UpdateButton.Content = "Add drawing pair…";
            ReplaceFileButton.Content = "Replace selected drawing…";
        }
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
        var units = _resource.StorageMode == "Asset" && AssignedDrawings.IsDrawing(_resource)
            ? _data.AssetFileUnits(_gate, _resource) : [];
        var files = _data.GetResourceFiles(_resource, _gate)
            .Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            .Where(f => !_assetWideDrawings || _resource.FileScope(Path.GetFileName(f)) is not null)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .Select(f =>
            {
                string name = Path.GetFileName(f);
                var scope = _resource.FileScope(name);
                return new ResourceFileItem
                {
                    Name = name, Path = f, Modified = File.GetLastWriteTime(f).ToString("g"),
                    Size = FormatSize(new FileInfo(f).Length),
                    Coverage = units.Count > 0 && scope is not null ? DrawingUnitsWindow.UnitNames(units, scope) : ""
                };
            }).ToList();
        string? selected = (FileList.SelectedItem as ResourceFileItem)?.Path;
        FileList.ItemsSource = files;
        FileList.SelectedItem = files.FirstOrDefault(f => f.Path.Equals(selected, StringComparison.OrdinalIgnoreCase));
        EmptyText.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = files.Count == 0 ? "No current files." : $"{files.Count} current files. Double-click a file to open it.";
    }

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024L ? $"{bytes / (1024d * 1024d):0.0} MB" : bytes >= 1024L ? $"{bytes / 1024d:0.0} KB" : $"{bytes} B";
    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        FileUnitsButton.IsEnabled = ReplaceFileButton.IsEnabled = FileList.SelectedItem is ResourceFileItem;

    private void FileList_DoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();
    private void OpenSelected_Click(object sender, RoutedEventArgs e) => OpenSelected();
    private void OpenSelected() { if (FileList.SelectedItem is ResourceFileItem item && File.Exists(item.Path)) FileLauncherService.Open(item.Path); }
    private void OpenCurrentFolder_Click(object sender, RoutedEventArgs e) { var folder = CurrentFolder(); Directory.CreateDirectory(folder); FileLauncherService.Open(folder); }
    private void OpenArchive_Click(object sender, RoutedEventArgs e) { var folder = ArchiveFolder(); Directory.CreateDirectory(folder); FileLauncherService.Open(folder); }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (!_resource.AllowUserUpload) return;
        if (AssignedDrawings.IsDrawing(_resource)) { await UploadDrawingPairAsync(null); return; }
        var filter = _resource.Label.Contains("Drawing", StringComparison.OrdinalIgnoreCase)
            ? "Drawings|*.pdf;*.dwg;*.dxf|PDF|*.pdf|AutoCAD|*.dwg;*.dxf|All files|*.*"
            : "All files|*.*";
        var dialog = new OpenFileDialog { Title = $"Upload {_resource.Label} for {_gate.Display} / {_unit.UnitDisplay}", Filter = filter, Multiselect = true };
        if (dialog.ShowDialog(Owner ?? this) != true) return;
        var names = string.Join("\n", dialog.FileNames.Select(Path.GetFileName));
        var applies = _resource.AppliesToUnitIds.Count > 1
            ? "\n\nThis resource is shared by multiple units. The update will be visible from every linked unit."
            : "";
        if (MessageBox.Show($"Update {_resource.Label}?\n\n{names}{applies}\n\nMatching current files will be archived according to this resource's archive rule.", "Confirm update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { _data.AddOrUpdateResourceFiles(_resource, _gate, _unit, dialog.FileNames); RefreshFiles(); StatusText.Text = "Update complete. Previous matching file(s) were archived."; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Unable to update resource", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void ReplaceFile_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not ResourceFileItem item) { StatusText.Text = "Select the sheet or file you are replacing first."; return; }
        if (AssignedDrawings.IsDrawing(_resource)) { await UploadDrawingPairAsync(item.Path); return; }
        var dialog = new OpenFileDialog { Title = "Choose the replacement for " + item.Name, Multiselect = false, Filter = "All files|*.*" };
        if (dialog.ShowDialog(Owner ?? this) != true) return;
        if (MessageBox.Show(Owner ?? this, $"Replace '{item.Name}' with '{Path.GetFileName(dialog.FileName)}'?\n\nThe previous file will be archived. Every unit linked to this same file will use the replacement; unrelated sheets stay current.", "Replace selected file", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { _data.ReplaceAssetFile(_resource, _gate, item.Path, dialog.FileName); RefreshFiles(); StatusText.Text = "Replacement saved. Previous revision archived; existing unit links preserved."; }
        catch (Exception error) { MessageBox.Show(Owner ?? this, error.Message, "Unable to replace file", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    internal async Task<DrawingUploadReceipt?> UploadDrawingPairAsync(string? replacing)
    {
        if (_fileUpdateBusy) return null;
        try
        {
            var owner = Owner ?? this;
            var dialog = new OpenFileDialog
            {
                Title = $"Choose the PDF and DWG for {_gate.AssetDisplay}",
                Multiselect = true,
                Filter = "Drawing pair (*.pdf, *.dwg)|*.pdf;*.dwg"
            };
            if (dialog.ShowDialog(owner) != true) return null;
            if (dialog.FileNames.Length != 2 ||
                dialog.FileNames.Count(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) != 1 ||
                dialog.FileNames.Count(p => Path.GetExtension(p).Equals(".dwg", StringComparison.OrdinalIgnoreCase)) != 1)
            {
                MessageBox.Show(owner, "Select one PDF and its matching DWG together.", "Both drawing files are required",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            var record = _data.LoadResources().FirstOrDefault(r => r.Id == _resource.Id) ?? _resource;
            var eligible = _data.AssetFileUnits(_gate, record);
            IReadOnlyList<string>? selected = null, expected = null;
            if (replacing is null)
            {
                selected = DrawingUnitsWindow.PickForNewPair(owner, _gate, _unit, eligible, dialog.FileNames);
                if (selected is null) return null;
            }
            else
            {
                string oldName = Path.GetFileName(replacing);
                var scope = record.FileScope(oldName) ?? throw new InvalidOperationException("Select a current drawing from the file list.");
                expected = eligible.Where(unit => scope.Count == 0 || scope.Contains(unit.Id, StringComparer.OrdinalIgnoreCase))
                    .Select(unit => unit.Id).ToArray();
                string message = $"Replace {oldName} for {_gate.AssetDisplay}?\n\nThe revision will apply to {DrawingUnitsWindow.UnitNames(eligible, scope)}.";
                if (MessageBox.Show(owner, message, "Replace drawing pair", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return null;
            }

            _fileUpdateBusy = true;
            IsEnabled = false;
            StatusText.Text = "Saving both drawing files…";
            var result = await Task.Run(() => _data.SaveDrawingPair(_resource, _gate, _unit, dialog.FileNames,
                replacing, unitIds: selected, expectedReplacementUnits: expected));
            string drive = await OperatorDriveRunner.AfterDrawingSavedAsync(_data, result.UnitIds);
            string action = result.Reused ? "Existing drawing linked to" : "Drawing pair saved for";
            string coverage = DrawingUnitsWindow.UnitNames(eligible, result.UnitIds);
            string status = string.Join(" ", new[] { $"{action} {coverage}.", result.ReviewNote, drive }
                .Where(part => part.Length > 0));
            RefreshFiles();
            StatusText.Text = status;
            return new(status, result.UnitIds);
        }
        catch (Exception error)
        {
            MessageBox.Show(Owner ?? this, error.Message, "Drawing pair could not be saved", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally { _fileUpdateBusy = false; IsEnabled = true; }
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_fileUpdateBusy) { e.Cancel = true; StatusText.Text = "Wait for the paired drawing update to finish before closing."; }
        base.OnClosing(e);
    }

    private async void FileUnits_Click(object sender, RoutedEventArgs e)
    {
        if (_fileUpdateBusy || FileList.SelectedItem is not ResourceFileItem item) return;
        try
        {
            var record = _data.LoadResources().Single(r => r.Id == _resource.Id);
            var linked = record.FileScope(item.Name) ?? throw new InvalidOperationException("This file is no longer in the asset index.");
            var eligible = _data.AssetFileUnits(_gate, record);
            var pair = record.DrawingPairs.FirstOrDefault(p => p.Contains(item.Name));
            string files = pair is null ? item.Name : pair.Pdf + Environment.NewLine + pair.Dwg;
            var selected = linked.Count == 0 ? eligible.Select(unit => unit.Id) : linked;
            var picker = new DrawingUnitsWindow(_gate, eligible, files, selected, "Save unit links") { Owner = Owner ?? this };
            if (picker.ShowDialog() != true) return;

            _fileUpdateBusy = true;
            IsEnabled = false;
            var affected = await Task.Run(() => _data.SetAssetFileUnits(_resource, _gate, item.Name, picker.UnitIds, linked));
            string drive = AssignedDrawings.IsDrawing(record) ? await OperatorDriveRunner.AfterDrawingSavedAsync(_data, affected) : "";
            RefreshFiles();
            StatusText.Text = $"Unit links saved for {DrawingUnitsWindow.UnitNames(eligible, picker.UnitIds)}. {drive}".TrimEnd();
        }
        catch (Exception error) { MessageBox.Show(Owner ?? this, error.Message, "Units covered", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _fileUpdateBusy = false; IsEnabled = true; }
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
    private void UamDiagnostics_Click(object sender, RoutedEventArgs e) { if (EmbeddedScannerRequested is { } show) show(); else new UamDiagnosticsWindow(_data, _gate, _unit) { Owner = Owner ?? this }.ShowDialog(); }
    private async void SignalColumn_Click(object sender, RoutedEventArgs e) { if (EmbeddedColumnRequested is { } show) show(); else await SignalColumnService.OpenAsync(this, _data, _gate, _unit, new[] { _resource }); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private sealed class ResourceFileItem { public string Name { get; set; } = ""; public string Path { get; set; } = ""; public string Modified { get; set; } = ""; public string Size { get; set; } = ""; public string Coverage { get; set; } = ""; }
}
