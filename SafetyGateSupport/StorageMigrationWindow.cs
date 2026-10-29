using System.Windows;
using System.Windows.Controls;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public sealed class StorageMigrationWindow : Window
{
    private readonly DataService _data;
    private AssetStorageMigration.Plan? _plan;
    private readonly DataGrid _files = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBox _status = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _apply = new() { Content = "Apply reviewed layout", IsEnabled = false };
    private readonly Button _preview = new() { Content = "Build / refresh preview" };
    private bool _busy;
    private CancellationTokenSource? _operation;
    public StorageMigrationWindow(DataService data)
    {
        _data = data; Title = "Organize asset folders"; Width = 1180; Height = 780; MinWidth = 850; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(20) }; grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel(); header.Children.Add(new TextBlock { Text = "One readable folder per asset", FontSize = 26, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Preview first. Identical files merge; different revisions require a choice. Close other copies of the app while applying. Metadata and original managed files are preserved in _AppData/Storage Backups. Source import packages stay available.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) }); grid.Children.Add(header);
        _files.Columns.Add(new DataGridTextColumn { Header = "Current source(s)", Binding = new System.Windows.Data.Binding("Source"), Width = new(2, DataGridLengthUnitType.Star) });
        _files.Columns.Add(new DataGridTextColumn { Header = "Planned destination", Binding = new System.Windows.Data.Binding("PreviewTarget"), Width = new(2, DataGridLengthUnitType.Star) });
        _files.Columns.Add(new DataGridTextColumn { Header = "Unit scope", Binding = new System.Windows.Data.Binding("Scope"), Width = new(1, DataGridLengthUnitType.Star) });
        _files.Columns.Add(new DataGridTextColumn { Header = "Choice", Binding = new System.Windows.Data.Binding("Decision"), Width = 120 });
        Grid.SetRow(_files, 1); grid.Children.Add(_files);
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; bottom.Children.Add(_status); var buttons = new WrapPanel();
        buttons.Children.Add(_preview); Add(buttons, "Archive selected older copy", () => Choose(AssetStorageMigration.Disposition.Archive)); Add(buttons, "Keep selected as separate sheet", () => Choose(AssetStorageMigration.Disposition.SeparateSheet)); Add(buttons, "Keep selected current", () => Choose(AssetStorageMigration.Disposition.Current));
        buttons.Children.Add(_apply); Add(buttons, "Close", Close); bottom.Children.Add(buttons); Grid.SetRow(bottom, 2); grid.Children.Add(bottom); Content = grid;
        _preview.Click += async (_, _) => await PreviewAsync(); _apply.Click += async (_, _) => await ApplyAsync();
        Loaded += async (_, _) => await PreviewAsync(); Closing += (_, e) => { if (_busy) { e.Cancel = true; _status.Text = "Wait for the current operation to finish before closing."; } };
    }
    private static void Add(Panel panel, string label, Action action) { var button = new Button { Content = label }; button.Click += (_, _) => action(); panel.Children.Add(button); }
    private void Choose(AssetStorageMigration.Disposition choice)
    {
        if (_busy || _plan is null || _files.SelectedItem is not AssetStorageMigration.FileEntry entry || entry.Historical) return;
        entry.Decision = choice; _files.Items.Refresh(); ShowPlan();
    }
    private void ShowPlan()
    {
        if (_plan is null) return; var errors = AssetStorageMigration.Validate(_data, _plan);
        foreach (var file in _plan.Files) file.PreviewTarget = file.Target(_plan); _files.Items.Refresh();
        _status.Text = _plan.Summary + "\n" + string.Join("\n", errors.Concat(_plan.Warnings).Take(30)); _apply.IsEnabled = !_busy && errors.Count == 0;
    }
    private async Task PreviewAsync()
    {
        if (_busy) return; _busy = true; _preview.IsEnabled = _apply.IsEnabled = false; _status.Text = "Reading file locations and checking file hashes… No files are being moved.";
        _operation = new CancellationTokenSource();
        try { _plan = await Task.Run(() => AssetStorageMigration.Build(_data, _operation.Token)); _files.ItemsSource = _plan.Files; }
        catch (Exception error) { _plan = null; _status.Text = "Preview failed: " + error.Message; }
        finally { _busy = false; _operation.Dispose(); _operation = null; _preview.IsEnabled = true; if (_plan is not null) ShowPlan(); }
    }
    private async Task ApplyAsync()
    {
        if (_busy || _plan is null) return;
        if (MessageBox.Show(this, "Apply this reviewed file layout?\n\nClose other app copies first. Files will be copied and hash-verified before links change. Old managed copies and metadata are retained in a recovery backup. Resolve every conflict shown in the preview before continuing.", "Organize asset folders", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _busy = true; _preview.IsEnabled = _apply.IsEnabled = false; _files.IsEnabled = false; _status.Text = "Copying and verifying files, then updating links…";
        try { string report = await Task.Run(() => AssetStorageMigration.Apply(_data, _plan)); _status.Text = "Organization complete. Active files are under Assets.\nRecovery report: " + report; _plan = null; }
        catch (Exception error) { _status.Text = "Organization did not complete: " + error.Message + "\nOriginal files and recovery metadata are retained. Refresh the preview before retrying."; }
        finally { _busy = false; _preview.IsEnabled = true; _files.IsEnabled = true; }
    }
}
