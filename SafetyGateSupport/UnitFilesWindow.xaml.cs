using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class UnitFilesWindow : Window
{
    private readonly DataService _data;
    private readonly SafetyGate _gate;
    private readonly SafetySystem _unit;
    private readonly List<ResourceDefinition> _resources;
    private List<AssignedFile> _files = [];
    private string _filter;
    private bool _closed;
    private string _unavailable = "";
    private sealed record AssignedFile(string Name, string Path, string Category, string Kind);

    public UnitFilesWindow(DataService data, SafetyGate gate, SafetySystem unit, IEnumerable<ResourceDefinition> resources, string filter = "all")
    {
        InitializeComponent();
        _data = data; _gate = gate; _unit = unit; _filter = filter;
        _resources = data.ResourcesFor(gate, unit, resources).ToList();
        UnitText.Text = $"{gate.Display} / {unit.UnitDisplay} • {gate.Category} • {gate.Location}";
        ColumnButton.Visibility = _resources.Any(SignalColumnService.IsLightResource) ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) => await LoadFilesAsync();
        Closed += (_, _) => _closed = true;
    }

    private async Task LoadFilesAsync()
    {
        try
        {
            var result = await Task.Run(() =>
            {
                var files = new List<AssignedFile>();
                var unavailable = new List<string>();
                var currentResources = _data.ResourcesFor(_gate, _unit, _data.LoadResources().Concat(_resources).GroupBy(r => r.Id).Select(g => g.First()));
                foreach (var resource in currentResources.Where(r => r.Kind.Equals("ManagedFiles", StringComparison.OrdinalIgnoreCase) || r.Kind.Equals("Photos", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        string folder = resource.Kind.Equals("Photos", StringComparison.OrdinalIgnoreCase) ? _data.ResourceBaseFolder(resource, _gate) : _data.ResourceCurrentFolder(resource, _gate);
                        // Enumerate directly: a missing or inaccessible assignment must remain visible as unavailable.
                        foreach (string path in _data.GetResourceFiles(resource, _gate).Where(p => !Path.GetFileName(p).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                        {
                            string kind = SignalColumnService.IsLightResource(resource) ? "light" : resource.LaunchAction.Equals("UAM", StringComparison.OrdinalIgnoreCase) ? "scanner" : resource.Label.Contains("Drawing", StringComparison.OrdinalIgnoreCase) || resource.Label.Contains("Schematic", StringComparison.OrdinalIgnoreCase) ? "drawing" : "other";
                            files.Add(new(Path.GetFileName(path), path, resource.Label, kind));
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { unavailable.Add(resource.Label); }
                }
                foreach (var manual in _data.ManualsFor(_gate, _unit))
                    files.Add(new(Path.GetFileName(manual.FilePath), _data.Resolve(manual.FilePath), manual.Title, "manual"));
                return (Files: files.DistinctBy(f => (f.Path.ToUpperInvariant(), f.Kind)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList(), Unavailable: unavailable);
            });
            if (_closed) return;
            _files = result.Files;
            _unavailable = result.Unavailable.Count == 0 ? "" : " Unavailable folders: " + string.Join(", ", result.Unavailable) + ". Check the shared drive or file assignment.";
            RefreshFiles();

        }
        catch (Exception error) { if (!_closed) StatusText.Text = "Could not load assigned files: " + error.Message; }
    }

    private void RefreshFiles()
    {
        if (FileList is null) return;
        string query = FileSearchBox.Text.Trim();
        var matches = _files.Where(f => (_filter == "all" || f.Kind == _filter) && (query.Length == 0 || $"{f.Name} {f.Category}".Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        FileList.ItemsSource = matches;
        foreach (var button in FilterPanel.Children.OfType<Button>())
        {
            bool selected = Equals(button.Tag, _filter);
            button.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
            button.Background = selected ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 242, 247)) : System.Windows.Media.Brushes.White;
        }
        StatusText.Text = matches.Count == 0 ? "No matching current files assigned. Try All files, or use Import / assign files to review this unit’s assignments." : $"{matches.Count} current file(s) • {_filter}";
        StatusText.Text += _unavailable;
    }
    private void Filter_Click(object sender, RoutedEventArgs e) { _filter = (string)((Button)sender).Tag; RefreshFiles(); }
    private void Search_Changed(object sender, TextChangedEventArgs e) => RefreshFiles();
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) { OpenButton.IsEnabled = FolderButton.IsEnabled = FileList.SelectedItem is AssignedFile; }
    private void File_DoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected(false);
    private void Open_Click(object sender, RoutedEventArgs e) => OpenSelected(false);
    private void Folder_Click(object sender, RoutedEventArgs e) => OpenSelected(true);
    private void OpenSelected(bool folder)
    {
        if (FileList.SelectedItem is not AssignedFile file) return;
        if (!File.Exists(file.Path)) { StatusText.Text = "This assigned file is unavailable. Reconnect the shared drive or review its assignment."; return; }
        try { FileLauncherService.Open(folder ? Path.GetDirectoryName(file.Path)! : file.Path); }
        catch (Exception error) { StatusText.Text = "Unable to open: " + error.Message; }
    }
    private async void Column_Click(object sender, RoutedEventArgs e) => await SignalColumnService.OpenAsync(this, _data, _gate, _unit, _resources);
    private void Resources_Click(object sender, RoutedEventArgs e) => new SupportLibraryWindow(_data, _gate, _unit) { Owner = this }.ShowDialog();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
