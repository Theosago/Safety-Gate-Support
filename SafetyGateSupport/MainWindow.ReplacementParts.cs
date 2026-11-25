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
    private void SetPartsRail(bool visible)
    {
        if (visible) SetCommonProblemsExpanded(false);
        PartsRail.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PartsRailColumn.Width = visible ? new GridLength(330) : new GridLength(0);
        if (visible) RefreshReplacementParts();
    }
    private void ClosePartsRail_Click(object sender, RoutedEventArgs e) => SetPartsRail(false);
    private List<PartRecord> ReplacementPartsFor(SafetySystem unit) => DataService.ReplacementPartsFor(unit, _parts).ToList();
    private void RefreshReplacementParts()
    {
        var unit = SelectedUnit;
        var unitParts = unit is null ? [] : ReplacementPartsFor(unit);
        UnitPartsButton.Content = unit is null ? "Replacement parts" : $"Replacement parts ({unitParts.Count})";
        bool unitView = SystemsView.Visibility == Visibility.Visible && unit is not null;
        PartsRailTitle.Text = unitView ? "Replacement parts" : "All parts";
        ConfigureUnitPartsButton.Visibility = unitView ? Visibility.Visible : Visibility.Collapsed;
        ConfigureUnitPartsButton.IsEnabled = _parts.Count > 0;
        var parts = unitView ? unitParts : _parts.OrderBy(p => p.Description).ToList();
        RailPartsList.ItemsSource = parts;
        RailPartsEmpty.Text = parts.Count > 0 ? "" : _parts.Count == 0
            ? "Add the first part in Plant setup → Parts."
            : "No parts assigned to this unit yet. Use Choose spare parts.";
        DrawingCardPartsList.ItemsSource = unitParts;
        DrawingCardPartsEmpty.Text = unitParts.Count > 0 ? "" : "No spare parts assigned yet. Use Replacement parts → Choose spare parts.";
    }
    private async void ChooseUnitManuals_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUnit is not { } unit || GateFor(unit) is not { } gate) { MessageBox.Show(this, "Select a unit first to choose its manuals."); return; }
        var current = DataService.ManualIdsFor(unit, _manuals);
        var options = _manuals.OrderBy(m => m.Title).Select(m => new AssignmentPickerWindow.Option(m.Id, m.Title, string.Join(" · ", new[] { m.Manufacturer, m.Model }.Where(s => !string.IsNullOrWhiteSpace(s))))).ToList();
        var siblings = UnitsForGate(gate.Id);
        var picked = AssignmentPickerWindow.Show(this, "Manuals for " + UnitTitle(gate, unit), "Tick the manuals and documents used on this equipment.", options, current, unit.UnitDisplay, gate.AssetDisplay, siblings.Count);
        if (picked is null) return;
        var targets = picked.WholeAsset ? siblings.Select(u => u.Id).ToList() : [unit.Id];
        try
        {
            var saved = await Task.Run(() => _data.SaveUnitsManuals(targets, picked.SelectedIds));
            foreach (var record in saved) if (_systems.FirstOrDefault(u => u.Id == record.Id) is { } local) local.ManualIds = record.ManualIds;
            _manualFilterGateId = unit.GateId; _manualFilterUnitId = unit.Id; RefreshManuals();
        }
        catch (Exception error) { MessageBox.Show(this, "Manual choices were not saved: " + error.Message); }
    }
    private async void ConfigureUnitParts_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUnit is not { } unit || GateFor(unit) is not { } gate) return;
        var current = ReplacementPartsFor(unit).Select(p => p.Id).ToList();
        var options = _parts.OrderBy(p => p.Description).Select(p => new AssignmentPickerWindow.Option(p.Id, p.Description,
            string.Join(" · ", new[] { "Item " + p.PartNumber, p.LocationValue.Length > 0 ? "Bin " + p.LocationValue : "" }.Where(s => s.Length > 0)))).ToList();
        var siblings = UnitsForGate(gate.Id);
        var picked = AssignmentPickerWindow.Show(this, "Spare parts for " + UnitTitle(gate, unit), "Tick the parts used on this equipment.", options, current, unit.UnitDisplay, gate.AssetDisplay, siblings.Count);
        if (picked is null) return;
        var targets = picked.WholeAsset ? siblings.Select(u => u.Id).ToList() : [unit.Id];
        try
        {
            ConfigureUnitPartsButton.IsEnabled = false;
            var saved = await Task.Run(() => _data.SaveUnitsReplacementParts(targets, picked.SelectedIds));
            foreach (var record in saved) if (_systems.FirstOrDefault(u => u.Id == record.Id) is { } local) local.ReplacementPartIds = record.ReplacementPartIds;
            unit.ReplacementPartIds = saved.First(u => u.Id == unit.Id).ReplacementPartIds;
            if (!_isClosed) RefreshReplacementParts();
        }
        catch (Exception error) { if (!_isClosed) MessageBox.Show(this, "Unit parts were not saved: " + error.Message); }
        finally { if (!_isClosed) ConfigureUnitPartsButton.IsEnabled = _parts.Count > 0; }
    }
}
