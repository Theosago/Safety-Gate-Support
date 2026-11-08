using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class AdminWindow
{
    private void ReturnStart_Click(object sender, RoutedEventArgs e)
    {
        StartSetupTab.IsSelected = true;
        SetupTaskText.Text = "Review the selected pilot’s saved content before opening operator view.";
        StartSetupTab.UpdateLayout();
        PilotContentStep.BringIntoView();
    }
    public string? OperatorPreviewUnitId { get; private set; }
    private IEnumerable<TabItem> AdvancedSetupTabs => [ResourcesSetupTab, FlowsSetupTab, ManualsSetupTab, AreasSetupTab, AuditSetupTab];
    private void AdvancedSetup_Click(object sender, RoutedEventArgs e)
    {
        bool show = AdvancedSetupCheck.IsChecked == true;
        if (!show && AdvancedSetupTabs.Any(t => t.IsSelected)) StartSetupTab.IsSelected = true;
        foreach (var tab in AdvancedSetupTabs) tab.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SetupTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, SetupTabs)) return;
        if (StartSetupTab?.IsSelected == true && _data is not null) RefreshSetupGuide();
        else if (StartGuideStatus is not null) StartGuideStatus.Text = "";
    }
    private void RefreshSetupGuide()
    {
        string? assetId = (PilotAssetBox.SelectedItem as SafetyGate)?.Id;
        string? selectedId = (PilotUnitBox.SelectedItem as SafetySystem)?.Id ?? InitialPilotUnitId;
        var assets = _gates.OrderBy(g => g.AssetNumber).ThenBy(g => g.Category).ToList();
        PilotUnitBox.ItemsSource = _units.OrderBy(u => u.AssetNumber).ThenBy(u => u.UnitNumber).ToList();
        var pilot = _units.FirstOrDefault(u => u.Id == selectedId) ?? (_units.Count == 1 ? _units[0] : null);
        PilotUnitBox.SelectedItem = pilot;
        PilotAssetBox.ItemsSource = assets;
        PilotAssetBox.SelectedItem = assets.FirstOrDefault(g => g.Id == (assetId ?? pilot?.GateId)) ??
            (assets.Count == 1 ? assets[0] : null);
        StartLibraryStatus.Text = _data.IsReady ? "Connected: " + _data.SharedRoot : "No library connected. Choose an existing library or create an empty one below.";
        StartInventoryStatus.Text = assets.Count == 0 ? "Add your first asset to get started." : $"{assets.Count} saved assets. Select the box that contains the new unit.";
        RefreshPilotContent();
    }

    private void PilotAssetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StartAddUnitButton is not null) StartAddUnitButton.IsEnabled = PilotAssetBox.SelectedItem is SafetyGate;
    }

    private void RefreshPilotContent()
    {
        if (StartContentStatus is null) return;
        if (PilotUnitBox.SelectedItem is not SafetySystem pilot)
        {
            StartContentStatus.Text = "Save a unit, then select it above to see what operators can use.";
            return;
        }

        int pdfs = _manuals.Count(manual =>
            string.Equals(Path.GetExtension(manual.FilePath), ".pdf", StringComparison.OrdinalIgnoreCase) &&
            (pilot.ManualIds is { } ids
                ? ids.Contains(manual.Id, StringComparer.OrdinalIgnoreCase)
                : manual.AppliesTo(pilot.GateId, pilot.Id)));
        bool hasProblemPhoto = _help.Any(topic => topic.Visible &&
            topic.Section.Equals("Common Problems", StringComparison.OrdinalIgnoreCase) && topic.Photos.Count > 0);
        string photo = string.IsNullOrWhiteSpace(pilot.PrimaryPhoto) ? "photo needed" : "photo assigned";
        string pdf = pdfs switch { 0 => "PDF needed", 1 => "1 PDF assigned", _ => $"{pdfs} PDFs assigned" };
        string problem = hasProblemPhoto ? "Common Problem with photo published" : "Common Problem photo needed";
        StartContentStatus.Text = $"{pilot.AdminDisplay}\n{photo}  ·  {pdf}  ·  {problem}";
    }
    private void StartChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose a plant library folder", Multiselect = false };
        if (picker.ShowDialog(this) == true) StartLibraryPathBox.Text = picker.FolderName;
    }
    private async void ConnectLibrary_Click(object sender, RoutedEventArgs e)
    {
        string path = StartLibraryPathBox.Text.Trim(); SetupTabs.IsEnabled = false;
        string previous = _data.SharedRoot;
        try
        {
            await Task.Run(() =>
            {
                if (path.Length == 0) throw new IOException("Choose the existing library folder first.");
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                if (JsonSerializer.Deserialize<List<SafetySystem>>(File.ReadAllText(Path.Combine(path, "Data", "systems.json")), options) is null ||
                    JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Path.Combine(path, "Data", "categories.json")), options) is null)
                    throw new InvalidDataException("This folder does not contain a readable plant library.");
                _data.SetSharedRoot(path);
            });
            await RefreshLibraryGuideAsync();
            StartGuideStatus.Text = "Existing library connected. Choose a pilot unit below; its saved content has been retained.";
        }
        catch (Exception error)
        {
            _data.Settings.SharedRoot = previous;
            try { _data.SaveLocalSettings(); } catch (Exception settingsError) { if (!_isClosed) AdminStatusText.Text = "Previous folder retained in this session, but PC settings could not be saved: " + settingsError.Message; }
            if (!_isClosed) StartGuideStatus.Text = "Could not connect: " + error.Message + " Choose the folder containing Data, not the Data folder itself.";
        }
        finally { if (!_isClosed) SetupTabs.IsEnabled = true; }
    }
    private async void CreateBlankLibrary_Click(object sender, RoutedEventArgs e)
    {
        string path = StartLibraryPathBox.Text.Trim(), area = StartAreaBox.Text.Trim();
        SetupTabs.IsEnabled = false;
        try
        {
            await Task.Run(() => _data.CreateBlankPlantLibrary(path, area)); await RefreshLibraryGuideAsync();
            if (!_isClosed)
            {
                StartGuideStatus.Text = "Blank library created. Add one asset / box to start the pilot.";
                PilotEquipmentStep.BringIntoView();
            }
        }
        catch (Exception error) { if (!_isClosed) StartGuideStatus.Text = "Setup could not finish: " + error.Message; }
        finally { if (!_isClosed) SetupTabs.IsEnabled = true; }
    }
    private async Task RefreshLibraryGuideAsync()
    {
        var snapshot = await Task.Run(ReadSetupSnapshot); if (_isClosed) return;
        ApplySetupSnapshot(snapshot); SharedRootBox.Text = StartLibraryPathBox.Text = _data.SharedRoot;
    }
    private bool NeedLibrary()
    {
        if (_data.IsReady) return false;
        StartGuideStatus.Text = "Connect or create a library in step 1 first."; StartSetupTab.IsSelected = true; return true;
    }
    private SafetySystem? Pilot()
    {
        if (PilotUnitBox.SelectedItem is SafetySystem unit) return unit;
        StartGuideStatus.Text = "Save a unit and select it as the pilot in step 2 first."; return null;
    }
    private void StartAddAsset_Click(object sender, RoutedEventArgs e)
    {
        if (NeedLibrary()) return;
        UnitsSetupTab.IsSelected = true; NewGate_Click(sender, e);
        SetupTaskText.Text = "Use the asset number on the physical label and a location the crew will recognize. Then choose Save asset / box.";
    }
    private void StartAddUnit_Click(object sender, RoutedEventArgs e)
    {
        if (NeedLibrary()) return;
        if (PilotAssetBox.SelectedItem is not SafetyGate gate)
        {
            StartGuideStatus.Text = "Choose the saved asset / box that contains the new unit.";
            return;
        }

        UnitsSetupTab.IsSelected = true;
        SelectGateById(gate.Id);
        NewUnit_Click(sender, e);
    }
    private void StartEditPilot_Click(object sender, RoutedEventArgs e)
    {
        if (NeedLibrary() || Pilot() is not { } pilot) return;
        UnitsSetupTab.IsSelected = true; SelectGateById(pilot.GateId);
        UnitListAdmin.SelectedItem = (UnitListAdmin.ItemsSource as IEnumerable<SafetySystem>)?.FirstOrDefault(u => u.Id == pilot.Id);
        GateUnitEditorScroll.ScrollToTop(); SetupTaskText.Text = "This is the saved pilot unit. Choose its identification photo, review equipment, then Save Unit.";
    }
    private async void StartAttachPdf_Click(object sender, RoutedEventArgs e)
    {
        if (NeedLibrary() || Pilot() is not { } pilot) return;
        var picker = new OpenFileDialog { Title = "Attach an approved PDF to " + pilot.AdminDisplay, Filter = "PDF document|*.pdf" };
        if (picker.ShowDialog(this) != true) return;
        SetupTabs.IsEnabled = false;
        try
        {
            await Task.Run(() => _data.AddPilotPdf(pilot.Id, picker.FileName));
            await RefreshLibraryGuideAsync(); StartGuideStatus.Text = "PDF copied into the library and assigned to the pilot. Open Manuals / documents in operator view to check it.";
        }
        catch (Exception error) { if (!_isClosed) StartGuideStatus.Text = "PDF not attached: " + error.Message; }
        finally { if (!_isClosed) SetupTabs.IsEnabled = true; }
    }
    private void StartProblem_Click(object sender, RoutedEventArgs e)
    { if (NeedLibrary()) return; HelpSetupTab.IsSelected = true; NewCommonProblem_Click(sender, e); SetupTaskText.Text = "Name a visible problem, add its photos and fix instructions, then Save Topic. Published entries appear for all units."; }
    private void StartParts_Click(object sender, RoutedEventArgs e)
    { if (NeedLibrary()) return; PartsSetupTab.IsSelected = true; NewPart_Click(sender, e); SetupTaskText.Text = "Item ID lookup needs one of the configured stockroom plants and network access. Optional details can be entered manually; this step can wait until after the pilot."; }
    private void StartAreas_Click(object sender, RoutedEventArgs e)
    { AdvancedSetupCheck.IsChecked = true; AdvancedSetup_Click(sender, e); AreasSetupTab.IsSelected = true; SetupTaskText.Text = "Area names group equipment for operators. Add a new name or rename a saved area without rebuilding the application."; }
    private void StartPhoneGuides_Click(object sender, RoutedEventArgs e)
    {
        if (NeedLibrary() || Pilot() is null) return;
        StartEditPilot_Click(sender, e);
        MobileSettingsExpander.IsExpanded = true;
        UnitsSetupTab.UpdateLayout();
        MobileSettingsExpander.BringIntoView();
        MobileOfflineBox.Focus();
        SetupTaskText.Text = "Edit the QR settings, click Save Unit, then Return to phone pilot to export.";
    }
    private void ReturnPhonePilot_Click(object sender, RoutedEventArgs e)
    {
        StartSetupTab.IsSelected = true;
        SetupTaskText.Text = "Export uses the pilot’s saved settings.";
        StartSetupTab.UpdateLayout();
        PilotPhoneStep.BringIntoView();
    }
    private void PilotUnitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshPilotContent();
        if (PilotQrStatus is null || PilotExportStatus is null) return;
        PilotExportStatus.Text = "";
        if (PilotUnitBox.SelectedItem is not SafetySystem pilot)
        {
            PilotQrStatus.Text = "Choose a saved pilot unit in step 2.";
            return;
        }

        string mode = pilot.MobileSupportUrl.Length > 0
            ? MobileSupportExporter.ValidLink(pilot.MobileSupportUrl) ? "QR opens the document link and needs internet. Offline text stays in the printable guide. Test the link signed out before printing." : "invalid document link. Edit or clear it."
            : string.IsNullOrWhiteSpace(pilot.MobileOfflineInstructions) ? "no QR configured. Export creates a guide only." : "offline text saved. No internet required to read the text.";
        PilotQrStatus.Text = $"{pilot.AdminDisplay} — {mode}";
    }
    private void StartOperator_Click(object sender, RoutedEventArgs e)
    {
        if (NeedLibrary() || Pilot() is not { } pilot) return;
        OperatorPreviewUnitId = pilot.Id; Close();
    }
}
