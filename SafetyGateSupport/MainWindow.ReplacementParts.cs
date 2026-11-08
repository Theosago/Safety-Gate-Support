using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;
namespace SafetyGateSupport;

public partial class MainWindow
{
    private void SaveStockroomReport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Save stockroom lookup report", Filter = "Stockroom report|*.zip", FileName = "Stockroom-lookup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip" };
        if (dialog.ShowDialog(this) != true) return;
        var snapshot = _stockroomSnapshot; var plant = SelectedStockroomPlant;
        var report = new { Plant = plant?.Name, Source = plant?.Url, Status = _stockroomMessage, Snapshot = snapshot, Items = _parts.Select(p => new { ItemId = p.PartNumber, Matches = snapshot?.Rows.Where(r => StockroomService.StockKey(r.Stock) == StockroomService.StockKey(p.PartNumber)).ToArray(), Matched = snapshot is not null && StockroomService.Find(snapshot, p, true) is not null }).ToArray() };
        try
        {
            using var file = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write); using var zip = new ZipArchive(file, ZipArchiveMode.Create); using var entry = zip.CreateEntry("stockroom-lookup.json").Open();
            JsonSerializer.Serialize(entry, report, new JsonSerializerOptions { WriteIndented = true });
            MessageBox.Show(this, "Stockroom report saved. It contains returned inventory table text and lookup results, without browser cookies or sign-in fields.");
        }
        catch (Exception error) { MessageBox.Show(this, "Report could not be saved: " + error.Message); }
    }
    private readonly DispatcherTimer _inventoryTimer = new() { Interval = TimeSpan.FromMinutes(2) };
    private void InitializeReplacementParts()
    {
        _inventoryTimer.Tick += async (_, _) => { if (SelectedStockroomPlant is not null && _sharedDataReady) await RefreshStockroomAsync(); };
        _inventoryTimer.Start(); Closed += (_, _) => _inventoryTimer.Stop();
    }
    private void RailPlant_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_data is not null && !Equals(StockroomPlantBox.SelectedItem, RailPlantBox.SelectedItem)) StockroomPlantBox.SelectedItem = RailPlantBox.SelectedItem;
    }
    private void SetPartsRail(bool visible)
    {
        if (visible) SetCommonProblemsExpanded(false);
        PartsRail.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PartsRailColumn.Width = visible ? new GridLength(330) : new GridLength(0);
        if (visible) RefreshReplacementParts();
    }
    private void ClosePartsRail_Click(object sender, RoutedEventArgs e) => SetPartsRail(false);
    private List<PartRecord> ReplacementPartsFor(SafetySystem unit) => _parts.Where(p => unit.ReplacementPartIds is { } ids ? ids.Contains(p.Id, StringComparer.OrdinalIgnoreCase) : p.AppliesTo(unit.GateId, unit.Id)).OrderBy(p => p.Description).ToList();
    private void RefreshReplacementParts()
    {
        var unit = SelectedUnit;
        var unitParts = unit is null ? [] : ReplacementPartsFor(unit);
        UnitPartsButton.Content = unit is null ? "Replacement parts" : $"Replacement parts ({unitParts.Count})";
        bool unitView = SystemsView.Visibility == Visibility.Visible && unit is not null;
        PartsRailTitle.Text = unitView ? $"Replacement parts\n{GateFor(unit!)?.AssetDisplay} · {unit!.UnitDisplay}" : "Replacement parts";
        ConfigureUnitPartsButton.Visibility = unitView ? Visibility.Visible : Visibility.Collapsed;
        ConfigureUnitPartsButton.IsEnabled = _parts.Count > 0;
        var parts = unitView ? unitParts : _parts.OrderBy(p => p.Description).ToList();
        RailPartsList.ItemsSource = parts;
        RailPartsEmpty.Text = parts.Count > 0 ? "" : _parts.Count == 0
            ? "Add the first part in Plant setup → Parts."
            : "No parts assigned to this unit. Choose from the catalog.";
    }
    private async void ChooseUnitManuals_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUnit is not { } unit) { MessageBox.Show(this, "Select a unit first to choose its manuals."); return; }
        var list = new ListBox { ItemsSource = _manuals.OrderBy(m => m.Title).ToList(), DisplayMemberPath = "Title", SelectionMode = SelectionMode.Multiple };
        foreach (var manual in _manuals.Where(m => unit.ManualIds is { } ids ? ids.Contains(m.Id) : m.AppliesTo(unit.GateId, unit.Id))) list.SelectedItems.Add(manual);
        var save = new Button { Content = "Save unit manuals", IsDefault = true }; var cancel = new Button { Content = "Cancel", IsCancel = true };
        var buttons = new WrapPanel(); buttons.Children.Add(save); buttons.Children.Add(cancel);
        var panel = new DockPanel { Margin = new Thickness(20) }; DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var label = new TextBlock { Text = unit.AdminDisplay + " — select the manuals used on this unit", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) }; DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label); panel.Children.Add(list);
        var dialog = new Window { Owner = this, Title = "Choose unit manuals", Width = 650, Height = 650, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner }; save.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return;
        var idsToSave = list.SelectedItems.Cast<ManualRecord>().Select(m => m.Id).ToList();
        try { var result = await Task.Run(() => _data.SaveUnitManuals(unit.Id, idsToSave)); unit.ManualIds = result.ManualIds; _manualFilterGateId = unit.GateId; _manualFilterUnitId = unit.Id; RefreshManuals(); }
        catch (Exception error) { MessageBox.Show(this, "Manual choices were not saved: " + error.Message); }
    }
    private async void ConfigureUnitParts_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUnit is not { } unit) return;
        var chosen = ReplacementPartsFor(unit).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var search = new TextBox { Margin = new Thickness(0, 8, 0, 12), MinHeight = 34, ToolTip = "Search descriptions, locations or Item IDs" };
        var checks = _parts.OrderBy(p => p.Description).Select(p => new CheckBox { Tag = p, Content = new TextBlock { Text = p.Description + " · " + p.LocationDisplay + " · Item ID: " + p.PartNumber, TextWrapping = TextWrapping.Wrap }, IsChecked = chosen.Contains(p.Id), Margin = new Thickness(0, 7, 0, 7) }).ToList();
        var options = new StackPanel(); foreach (var check in checks) options.Children.Add(check);
        search.TextChanged += (_, _) => { foreach (var check in checks) { var p = (PartRecord)check.Tag; check.Visibility = (p.Description + " " + p.PartNumber + " " + p.LocationValue).Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed; } };
        var save = new Button { Content = "Save unit parts", IsDefault = true }; var cancel = new Button { Content = "Cancel", IsCancel = true };
        var buttons = new WrapPanel(); buttons.Children.Add(save); buttons.Children.Add(cancel);
        var panel = new DockPanel { Margin = new Thickness(20) }; DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var header = new StackPanel(); header.Children.Add(new TextBlock { Text = unit.AdminDisplay, FontSize = 20, TextWrapping = TextWrapping.Wrap }); header.Children.Add(new TextBlock { Text = "Choose only the parts used on this unit. Saved to shared data.", TextWrapping = TextWrapping.Wrap }); header.Children.Add(search); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header); panel.Children.Add(new ScrollViewer { Content = options, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var dialog = new Window { Owner = this, Title = "Choose replacement parts", Content = panel, Width = 650, Height = 700, MinWidth = 450, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        save.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return;
        var ids = checks.Where(c => c.IsChecked == true).Select(c => ((PartRecord)c.Tag).Id).ToList();
        try
        {
            ConfigureUnitPartsButton.IsEnabled = false;
            var saved = await Task.Run(() => _data.SaveUnitReplacementParts(unit.Id, ids));
            unit.ReplacementPartIds = saved.ReplacementPartIds;
            if (!_isClosed) RefreshReplacementParts();
        }
        catch (Exception error) { if (!_isClosed) MessageBox.Show(this, "Unit parts were not saved: " + error.Message); }
        finally { if (!_isClosed) ConfigureUnitPartsButton.IsEnabled = true; }
    }
}
