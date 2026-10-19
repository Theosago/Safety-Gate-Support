using System.Threading;
using System.Windows;
using System.Windows.Controls;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class TroubleshootPickerWindow : Window
{
    private readonly DataService _data;
    private readonly List<SafetyGate> _gates;
    private readonly List<SafetySystem> _units;
    private string _currentArea = "";
    private CancellationTokenSource? _photoPreviewCancellation;
    private bool _isClosed;

    public TroubleshootPickerWindow(DataService data, IEnumerable<SafetyGate> gates, IEnumerable<SafetySystem> units, IEnumerable<string> categories)
    {
        InitializeComponent();
        _data = data;
        _gates = gates.ToList();
        _units = units.ToList();
        var areas = categories.ToList();
        AreaBox.ItemsSource = areas;
        ClearPreview();
        if (areas.Count > 0)
            AreaBox.SelectedItem = areas.FirstOrDefault(a => a.Equals("East Side", StringComparison.OrdinalIgnoreCase))
                ?? areas.FirstOrDefault(a => a.Equals("West Side", StringComparison.OrdinalIgnoreCase))
                ?? areas.FirstOrDefault(a => a.Equals("906 / Outside", StringComparison.OrdinalIgnoreCase))
                ?? areas[0];
    }

    private SafetyGate? SelectedGate => AssetList.SelectedItem as SafetyGate;
    private SafetySystem? SelectedUnit => UnitList.SelectedItem as SafetySystem;
    public SafetySystem? ChosenSystem { get; private set; }

    private void AreaBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentArea = AreaBox.SelectedItem as string ?? "";
        RefreshGates();
    }

    private void AssetSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshGates();

    private void RefreshGates()
    {
        if (AssetList is null || AssetSearchBox is null || string.IsNullOrWhiteSpace(_currentArea)) return;
        var q = AssetSearchBox.Text.Trim();
        AssetList.ItemsSource = _gates.Where(g => g.Category.Equals(_currentArea, StringComparison.OrdinalIgnoreCase))
            .Where(g => string.IsNullOrWhiteSpace(q) || $"{g.AssetNumber} {g.Display} {g.Manufacturer} {g.Model} {g.Location}".Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => int.TryParse(g.AssetNumber, out var n) ? n : int.MaxValue)
            .ThenBy(g => g.Manufacturer)
            .ToList();
        UnitList.ItemsSource = null;
        StartButton.IsEnabled = false;
        SelectedText.Text = "Select a safety gate and unit.";
        ClearPreview();
    }

    private void AssetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedGate is not { } gate)
        {
            UnitList.ItemsSource = null;
            StartButton.IsEnabled = false;
            ClearPreview();
            return;
        }

        UnitList.ItemsSource = _units.Where(u => u.GateId == gate.Id)
            .OrderBy(u => int.TryParse(u.UnitNumber, out var n) ? n : int.MaxValue)
            .ThenBy(u => u.UnitDisplay)
            .ToList();
        UnitList.SelectedItem = null;
        StartButton.IsEnabled = false;
        SelectedText.Text = $"{gate.Display} - choose a unit.";
        ClearPreview($"{gate.Display}\nChoose a unit to preview it.");
    }

    private void UnitList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var unit = SelectedUnit;
        StartButton.IsEnabled = unit is not null;
        SelectedText.Text = unit is null || SelectedGate is null
            ? "Select a safety gate and unit."
            : $"Selected: {SelectedGate.Display} - {unit.UnitDisplay} • {SelectedGate.Category}";

        if (unit is not null && SelectedGate is { } gate)
            ShowUnitPreview(gate, unit);
        else
            ClearPreview();
    }

    private async void ShowUnitPreview(SafetyGate gate, SafetySystem unit)
    {
        CancelPhotoPreview();
        if (_isClosed) return;
        PreviewImage.Source = null;
        PreviewTitleText.Text = $"{gate.AssetDisplay} • {unit.UnitDisplay}";

        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(gate.Manufacturer)) details.Add(gate.Manufacturer.Trim());
        if (!string.IsNullOrWhiteSpace(gate.Model)) details.Add($"Model: {gate.Model.Trim()}");
        if (!string.IsNullOrWhiteSpace(gate.Location)) details.Add(gate.Location.Trim());
        if (!string.IsNullOrWhiteSpace(gate.EquipmentType)) details.Add(gate.EquipmentType.Trim());
        PreviewDetailText.Text = details.Count == 0 ? gate.Category : string.Join(" • ", details);

        if (string.IsNullOrWhiteSpace(unit.PrimaryPhoto))
        {
            PreviewPlaceholderText.Visibility = Visibility.Visible;
            PreviewPlaceholderText.Text = "No identification photo has been saved for this unit.";
            PreviewStatusText.Text = "Confirm the asset and unit number before starting troubleshooting.";
            return;
        }

        PreviewPlaceholderText.Visibility = Visibility.Visible;
        PreviewPlaceholderText.Text = "Loading identification photo…";
        PreviewStatusText.Text = "Confirm the asset and unit number before starting troubleshooting.";
        _photoPreviewCancellation = new CancellationTokenSource();
        var cancellationToken = _photoPreviewCancellation.Token;
        try
        {
            var result = await IdentificationPhotoLoader.LoadAsync(_data.Resolve(unit.PrimaryPhoto), cancellationToken);
            if (_isClosed || cancellationToken.IsCancellationRequested) return;
            if (result.Status != IdentificationPhotoStatus.Loaded)
            {
                PreviewPlaceholderText.Text = result.Status == IdentificationPhotoStatus.Missing
                    ? "The saved identification photo could not be found."
                    : "The identification photo could not be previewed.";
                return;
            }
            PreviewImage.Source = result.Source;
            PreviewPlaceholderText.Visibility = Visibility.Collapsed;
            PreviewStatusText.Text = "Identification photo • verify this is the unit you are working on.";
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (_isClosed || cancellationToken.IsCancellationRequested) return;
            PreviewPlaceholderText.Visibility = Visibility.Visible;
            PreviewPlaceholderText.Text = "The identification photo could not be previewed.";
            PreviewStatusText.Text = "Confirm the asset and unit number before starting troubleshooting.";
        }
    }

    private void ClearPreview(string? message = null)
    {
        CancelPhotoPreview();
        if (PreviewImage is null) return;
        PreviewImage.Source = null;
        PreviewTitleText.Text = "Select a unit";
        PreviewDetailText.Text = "";
        PreviewStatusText.Text = "";
        PreviewPlaceholderText.Text = message ?? "Choose a safety gate and unit to see its identification photo.";
        PreviewPlaceholderText.Visibility = Visibility.Visible;
    }

    private void CancelPhotoPreview()
    {
        _photoPreviewCancellation?.Cancel();
        _photoPreviewCancellation?.Dispose();
        _photoPreviewCancellation = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        CancelPhotoPreview();
        base.OnClosed(e);
    }

    private void Start_Click(object sender, RoutedEventArgs e) => StartTroubleshooting();

    private void StartTroubleshooting()
    {
        if (SelectedUnit is not { } unit) return;
        ChosenSystem = unit;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
