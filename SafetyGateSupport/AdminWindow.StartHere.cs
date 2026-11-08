using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SafetyGateSupport.Models;

namespace SafetyGateSupport;

public partial class AdminWindow
{
    private void ReturnStart_Click(object sender, RoutedEventArgs e) { StartSetupTab.IsSelected = true; SetupTaskText.Text = "Choose the saved pilot below, attach a PDF or Common Problem, then try operator view."; }
    public string? OperatorPreviewUnitId { get; private set; }
    private IEnumerable<TabItem> AdvancedSetupTabs => [ResourcesSetupTab, FlowsSetupTab, ManualsSetupTab, AreasSetupTab, AuditSetupTab];
    private void AdvancedSetup_Click(object sender, RoutedEventArgs e)
    {
        bool show = AdvancedSetupCheck.IsChecked == true;
        if (!show && AdvancedSetupTabs.Any(t => t.IsSelected)) StartSetupTab.IsSelected = true;
        foreach (var tab in AdvancedSetupTabs) tab.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SetupTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (ReferenceEquals(e.Source, SetupTabs) && StartSetupTab?.IsSelected == true && _data is not null) RefreshSetupGuide(); }
    private void RefreshSetupGuide()
    {
        string? selectedId = (PilotUnitBox.SelectedItem as SafetySystem)?.Id;
        PilotUnitBox.ItemsSource = _units.OrderBy(u => u.AssetNumber).ThenBy(u => u.UnitNumber).ToList();
        PilotUnitBox.SelectedItem = _units.FirstOrDefault(u => u.Id == selectedId) ?? _units.FirstOrDefault();
        StartLibraryStatus.Text = _data.IsReady ? "Connected: " + _data.SharedRoot : "No library connected. Choose an existing library or create an empty one below.";
        StartInventoryStatus.Text = $"Saved records: {_areas.Count} areas · {_gates.Count} assets · {_units.Count} units · {_units.Count(u => u.PrimaryPhoto.Length > 0)} photo assignments.";
        StartContentStatus.Text = $"Saved content: {_manuals.Count} manuals · {_parts.Count} parts · {_help.Count(h => h.Visible && h.Section == "Common Problems")} published Common Problems. Open operator view to confirm the actual files and photos work.";
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
            if (!_isClosed) StartGuideStatus.Text = "Blank library created. Next: add one asset or box, then its unit and photo.";
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
        SetupTaskText.Text = "Add the asset number and location, then Save Gate Information. A unit is added after the asset is saved.";
    }
    private void StartAddUnit_Click(object sender, RoutedEventArgs e)
    {
        if (NeedLibrary()) return;
        var gate = _gates.FirstOrDefault(g => g.Id == (PilotUnitBox.SelectedItem as SafetySystem)?.GateId) ?? _gates.FirstOrDefault();
        if (gate is null) { StartGuideStatus.Text = "Add and save an asset / box before adding its unit."; return; }
        UnitsSetupTab.IsSelected = true; SelectGateById(gate.Id); NewUnit_Click(sender, e);
        GateUnitEditorScroll.ScrollToTop(); SetupTaskText.Text = "Enter the unit number, choose a photo, select only equipment it has, then Save Unit.";
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
        var picker = new OpenFileDialog { Title = "Attach an approved PDF to " + pilot.UnitDisplay, Filter = "PDF document|*.pdf" };
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
    { LibrarySetupTab.IsSelected = true; ExportPhoneGuidesButton.BringIntoView(); SetupTaskText.Text = "Start with printed photo guides and offline text labels. An approved document link can be added later."; }
    private void StartOperator_Click(object sender, RoutedEventArgs e)
    {
        if (NeedLibrary() || Pilot() is not { } pilot) return;
        OperatorPreviewUnitId = pilot.Id; Close();
    }
}
