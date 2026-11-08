using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class AdminWindow : Window
{
    private readonly DataService _data;
    private List<SafetyGate> _gates = new();
    private List<SafetySystem> _units = new();
    private List<ResourceDefinition> _resources = new();
    private List<string> _areas = new();
    private List<HelpItem> _help = new();
    private List<PartRecord> _parts = new();
    private List<ManualRecord> _manuals = new();
    private List<TroubleshootingFlow> _flows = new();

    private SafetyGate? _selectedGate;
    private SafetySystem? _selectedUnit;
    private ResourceDefinition? _selectedResource;
    private HelpItem? _selectedHelp;
    private PartRecord? _selectedPart;
    private ManualRecord? _selectedManual;
    private TroubleshootingFlow? _selectedFlow;
    private TroubleshootingNode? _selectedNode;
    private bool _newGate, _newUnit, _newResource, _newHelp, _newPart, _newManual, _newFlow, _newNode;
    private string _pendingPhotoSource = "";
    private bool _pendingPhotoIsTemporary;
    private CancellationTokenSource? _photoPreviewCancellation;
    private bool _isClosed;
    private bool _areaSaveBusy;
    private bool _loadingSetup;
    private string _pendingPartDocumentSource = "";
    private string _pendingManualSource = "";

    public AdminWindow(DataService data, Func<string, string, CancellationToken, Task<StockroomService.Snapshot>>? importReader = null)
    {
        InitializeComponent();
        _data = data;
        StartLibraryPathBox.Text = _data.IsReady ? _data.SharedRoot : "";
        _importReader = importReader ?? StockroomWebViewReader.SearchAsync;
        ImportPlantBox.ItemsSource = StockroomPlant.All;
        ImportPlantBox.SelectedItem = StockroomPlant.Find(_data.Settings.StockroomPlantId);
        Closed += (_, _) => _importCancellation?.Cancel();
        ResourceKindBox.ItemsSource = new[] { "ManagedFiles", "Manuals", "Parts", "Photos", "Folder", "Url" };
        ResourceArchiveBox.ItemsSource = new[] { "SameExtension", "SameName", "All", "None" };
        ResourceLaunchBox.ItemsSource = new[] { "None", "Siemens", "UAM" };
        ResourceKindBox.SelectedIndex = 0; ResourceArchiveBox.SelectedIndex = 0; ResourceLaunchBox.SelectedIndex = 0;
        SharedRootBox.Text = _data.SharedRoot; SiemensBox.Text = _data.Settings.SiemensExePath; UamBox.Text = _data.Settings.UamExePath;
        IsEnabled = false; SetupTabs.IsEnabled = false; AdminStatusText.Text = "Loading shared setup data…";
        Loaded += async (_, _) => await LoadInitialSetupAsync();
    }

    private void ReloadAll()
    {
        ApplySetupSnapshot(ReadSetupSnapshot());
    }
    private sealed record SetupSnapshot(List<SafetyGate> Gates, List<SafetySystem> Units, List<ResourceDefinition> Resources, List<string> Areas,
        List<HelpItem> Help, List<PartRecord> Parts, List<ManualRecord> Manuals, List<TroubleshootingFlow> Flows, SharedContent Content, List<AuditEntry> Audit);
    private SetupSnapshot ReadSetupSnapshot()
    {
        var ready = _data.IsReady;
        if (ready) _data.EnsureSharedDefaults();
        var gates = ready ? _data.LoadGates() : new();
        return new(gates, ready ? _data.LoadSystems(gates) : new(), ready ? _data.LoadResources() : new(), ready ? _data.LoadCategories() : new(),
            ready ? _data.LoadHelpItems() : new(), ready ? _data.LoadParts() : new(), ready ? _data.LoadManualRecords() : new(),
            ready ? _data.LoadTroubleshootingFlows() : new(), ready ? _data.LoadSharedContent() : new(), ready ? _data.LoadRecentAudit(300) : new());
    }
    private async Task LoadInitialSetupAsync()
    {
        if (_loadingSetup || _isClosed) return;
        _loadingSetup = true;
        try { var snapshot = await Task.Run(ReadSetupSnapshot); if (!_isClosed) { ApplySetupSnapshot(snapshot); SetupTabs.IsEnabled = true; AdminStatusText.Text = "Setup ready."; } }
        catch (Exception error) { if (!_isClosed) { SetupTabs.IsEnabled = false; AdminStatusText.Text = "Unable to load Setup. Close and reopen Setup after checking shared-drive access. " + error.Message; } }
        finally { _loadingSetup = false; if (!_isClosed) IsEnabled = true; }
    }
    private void ApplySetupSnapshot(SetupSnapshot snapshot)
    {
        _gates = snapshot.Gates; _units = snapshot.Units; _resources = snapshot.Resources; _areas = snapshot.Areas;
        _help = snapshot.Help; _parts = snapshot.Parts; _manuals = snapshot.Manuals; _flows = snapshot.Flows;
        var content = snapshot.Content;
        BindInventoryLists();
        HelpList.ItemsSource = _help.OrderBy(h => h.Section).ThenBy(h => h.SortOrder).ThenBy(h => h.Title).ToList();
        AdminPartList.ItemsSource = _parts.OrderBy(p => p.PartNumber).ThenBy(p => p.Description).ToList();
        ManualListAdmin.ItemsSource = _manuals.OrderBy(m => m.Title).ToList();
        FlowListAdmin.ItemsSource = _flows.OrderBy(f => f.Title).ToList();
        HomeSubtitleBox.Text = content.HomeSubtitle; ManualsIntroBox.Text = content.ManualsIntro; PartsIntroBox.Text = content.PartsIntro; StoreroomUrlBox.Text = content.StoreroomUrl;
        if (_areas.Count > 0 && GateAreaFilterBox.SelectedItem is null) GateAreaFilterBox.SelectedIndex = 0;
        RefreshGateList(); RefreshResourceList(); AuditList.ItemsSource = snapshot.Audit; RefreshSetupGuide();
    }

    private void BindInventoryLists()
    {
        GateAreaFilterBox.ItemsSource = _areas; GateAreaBox.ItemsSource = _areas; AreaList.ItemsSource = _areas;
        ResourceGateBox.ItemsSource = _gates.OrderBy(g => g.Category).ThenBy(g => SortNum(g.AssetNumber)).ToList();
        ManualGateAssignments.ItemsSource = _gates.OrderBy(g => g.Category).ThenBy(g => SortNum(g.AssetNumber)).ToList();
        ManualUnitAssignments.ItemsSource = _units.OrderBy(u => u.Category).ThenBy(u => SortNum(u.AssetNumber)).ThenBy(u => SortNum(u.UnitNumber)).ToList();
        PartGateAssignments.ItemsSource = _gates.OrderBy(g => g.Category).ThenBy(g => SortNum(g.AssetNumber)).ToList();
        PartUnitAssignments.ItemsSource = _units.OrderBy(u => u.Category).ThenBy(u => SortNum(u.AssetNumber)).ThenBy(u => SortNum(u.UnitNumber)).ToList();
        FlowGateBox.ItemsSource = new[] { new GateChoice { Id = "", Display = "All gates" } }.Concat(_gates.OrderBy(g => g.Category).ThenBy(g => SortNum(g.AssetNumber)).Select(g => new GateChoice { Id = g.Id, Display = g.Display })).ToList();
    }

    private static int SortNum(string? s) => int.TryParse(s, out var n) ? n : int.MaxValue;

    // Gates + Units
    private void GateAreaFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshGateList();
    private void RefreshGateList()
    {
        var area = GateAreaFilterBox.SelectedItem as string;
        GateList.ItemsSource = string.IsNullOrWhiteSpace(area) ? new List<SafetyGate>() : _gates.Where(g => g.Category.Equals(area, StringComparison.OrdinalIgnoreCase)).OrderBy(g => SortNum(g.AssetNumber)).ThenBy(g => g.Manufacturer).ToList();
        GateList.SelectedItem = null; UnitListAdmin.ItemsSource = null;
    }
    private void GateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedGate = GateList.SelectedItem as SafetyGate; _newGate = false; PopulateGate(); RefreshUnitsForSelectedGate();
    }
    private void RefreshUnitsForSelectedGate()
    {
        UnitListAdmin.ItemsSource = _selectedGate is null ? null : _units.Where(u => u.GateId == _selectedGate.Id).OrderBy(u => SortNum(u.UnitNumber)).ThenBy(u => u.UnitDisplay).ToList();
        UnitListAdmin.SelectedItem = null; _selectedUnit = null; PopulateUnit();
    }
    private void UnitListAdmin_SelectionChanged(object sender, SelectionChangedEventArgs e) { _selectedUnit = UnitListAdmin.SelectedItem as SafetySystem; _newUnit = false; PopulateUnit(); }

    private void PopulateGate()
    {
        if (_selectedGate is null) { PlcAddressBox.Text = PlcModelBox.Text = PlcNotesBox.Text = ""; GateAreaBox.SelectedItem = GateAreaFilterBox.SelectedItem; GateAssetBox.Text = ""; GateManufacturerBox.Text = ""; GateModelBox.Text = GateLocationBox.Text = GateTypeBox.Text = GateKeywordsBox.Text = ""; return; }
        PlcAddressBox.Text = _selectedGate.PlcAddress; PlcModelBox.Text = _selectedGate.PlcModel; PlcNotesBox.Text = _selectedGate.PlcNotes;
        GateAreaBox.SelectedItem = _selectedGate.Category; GateAssetBox.Text = _selectedGate.AssetNumber; GateManufacturerBox.Text = _selectedGate.Manufacturer; GateModelBox.Text = _selectedGate.Model; GateLocationBox.Text = _selectedGate.Location; GateTypeBox.Text = _selectedGate.EquipmentType; GateKeywordsBox.Text = _selectedGate.Keywords;
    }
    private void PopulateUnit()
    {
        CleanupPendingPhotoTemp();
        UnitHasSignalColumnCheck.IsChecked = _selectedUnit?.ShowLightConfiguration ?? false; UnitHasScannerCheck.IsChecked = _selectedUnit?.ShowScannerConfiguration ?? false;
        UnitNumberBox.Text = _selectedUnit?.UnitNumber ?? "";
        MobileSupportUrlBox.Text = _selectedUnit?.MobileSupportUrl ?? ""; MobileOfflineBox.Text = _selectedUnit?.MobileOfflineInstructions ?? "";
        UnitPhotoText.Text = _selectedUnit?.PrimaryPhoto ?? "";
        GateUnitEditorScroll.ScrollToTop();
        _pendingPhotoSource = "";
        _pendingPhotoIsTemporary = false;
        LoadUnitPhotoPreview(_selectedUnit?.PrimaryPhoto ?? "", false);
    }

    private async void LoadUnitPhotoPreview(string path, bool pending, string? sourceLabel = null)
    {
        CancelPhotoPreview();
        if (_isClosed) return;
        UnitPhotoPreview.Source = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            UnitPhotoPreviewStatus.Text = "Choose, paste, or drop a photo to preview it before saving the unit.";
            return;
        }

        UnitPhotoPreviewStatus.Text = "Loading identification photo…";
        _photoPreviewCancellation = new CancellationTokenSource();
        var cancellationToken = _photoPreviewCancellation.Token;
        try
        {
            var fullPath = Path.IsPathRooted(path) ? path : _data.Resolve(path);
            var result = await IdentificationPhotoLoader.LoadAsync(fullPath, cancellationToken);
            if (_isClosed || cancellationToken.IsCancellationRequested) return;
            if (result.Status != IdentificationPhotoStatus.Loaded)
            {
                UnitPhotoPreviewStatus.Text = result.Status == IdentificationPhotoStatus.Missing
                    ? (pending ? "The selected photo could not be loaded." : "The saved identification photo could not be found.")
                    : "This image could not be previewed. Try a JPG or PNG file.";
                return;
            }
            UnitPhotoPreview.Source = result.Source;
            UnitPhotoPreviewStatus.Text = pending
                ? (sourceLabel is null
                    ? "Preview of the selected photo — it will be copied to the shared drive when you click Save Unit."
                    : $"Preview from {sourceLabel}. It has not been saved yet; click Save Unit to copy it to the shared drive.")
                : "Current saved identification photo.";
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (_isClosed || cancellationToken.IsCancellationRequested) return;
            UnitPhotoPreviewStatus.Text = "This image could not be previewed. Try a JPG or PNG file.";
        }
    }

    private void CancelPhotoPreview()
    {
        _photoPreviewCancellation?.Cancel();
        _photoPreviewCancellation?.Dispose();
        _photoPreviewCancellation = null;
    }

    private void OrganizeAssetFolders_Click(object sender, RoutedEventArgs e)
    {
        new StorageMigrationWindow(_data) { Owner = this }.ShowDialog(); ReloadAll();
    }

    private void NewGate_Click(object sender, RoutedEventArgs e) { _selectedGate = null; _selectedUnit = null; _newGate = true; _newUnit = false; PopulateGate(); PopulateUnit(); GateInformationExpander.IsExpanded = true; GateInformationExpander.UpdateLayout(); GateAssetBox.BringIntoView(); GateAssetBox.Focus(); AdminStatusText.Text = "Enter the gate-level information, then save the gate. Add its units afterward."; }
    private void SaveGate_Click(object sender, RoutedEventArgs e)
    {
        try { SaveGateCore(); }
        catch (Exception error) { AdminStatusText.Text = "Could not finish saving this asset: " + error.Message + " Check library access, then retry."; }
    }
    private void SaveGateCore()
    {
        if (!_data.IsReady) { MessageBox.Show("Initialize shared data first."); return; }
        var area = GateAreaBox.SelectedItem as string; if (string.IsNullOrWhiteSpace(area)) { MessageBox.Show("Choose an area."); return; }
        var asset = GateAssetBox.Text.Trim(); var manufacturer = GateManufacturerBox.Text.Trim(); if (string.IsNullOrWhiteSpace(manufacturer)) manufacturer = "Unspecified";
        bool creatingGate = _newGate;
        if (_newGate)
        {
            _selectedGate = new SafetyGate { Id = Guid.NewGuid().ToString("N") }; _gates.Add(_selectedGate); _newGate = false;
        }
        if (_selectedGate is null) return;
        _selectedGate.Category = area; _selectedGate.AssetNumber = asset; _selectedGate.Manufacturer = manufacturer; _selectedGate.Model = GateModelBox.Text.Trim(); _selectedGate.Location = GateLocationBox.Text.Trim(); _selectedGate.EquipmentType = GateTypeBox.Text.Trim(); _selectedGate.Keywords = GateKeywordsBox.Text.Trim();
        _selectedGate.PlcAddress = PlcAddressBox.Text.Trim(); _selectedGate.PlcModel = PlcModelBox.Text.Trim(); _selectedGate.PlcNotes = PlcNotesBox.Text.Trim();
        if (creatingGate)
        {
            string folder = AssetFileLayout.SuggestedRoot(_selectedGate);
            if (_gates.Any(g => g != _selectedGate && g.AssetFolder.Equals(folder, StringComparison.OrdinalIgnoreCase))) folder += " - " + AssetFileLayout.SafeName(area + " - " + manufacturer);
            if (_gates.Any(g => g != _selectedGate && g.AssetFolder.Equals(folder, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show("Use a distinct asset number or location before creating its file folder."); _gates.Remove(_selectedGate); _selectedGate = null; _newGate = true; return; }
            _selectedGate.AssetFolder = folder;
        }
        var savedGateId = _selectedGate.Id;
        _data.SaveGates(_gates); if (creatingGate) _data.WriteAssetFileIndex(_selectedGate); _data.LogAudit("Gate saved", savedGateId, details: _selectedGate.Display);
        ReloadAll(); SelectGateById(savedGateId); AdminStatusText.Text = "Gate information saved. Every unit under this gate uses this gate-level information."; SetupTaskText.Text = "Asset saved. Next: add a unit to this asset, give it a number and photo, then save.";
    }
    private void SelectGateById(string id)
    {
        var gate = _gates.FirstOrDefault(g => g.Id == id); if (gate is null) return; GateAreaFilterBox.SelectedItem = gate.Category; RefreshGateList(); GateList.SelectedItem = (GateList.ItemsSource as IEnumerable<SafetyGate>)?.FirstOrDefault(g => g.Id == id);
    }
    private void DeleteGate_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGate is null) return;
        var unitCount = _units.Count(u => u.GateId == _selectedGate.Id);
        if (MessageBox.Show($"Remove {_selectedGate.Display} and its {unitCount} unit record(s) from the app index?\n\nShared files will NOT be deleted.", "Remove gate", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var id = _selectedGate.Id; _gates.RemoveAll(g => g.Id == id); _units.RemoveAll(u => u.GateId == id); _resources.RemoveAll(r => r.GateId == id);
        foreach (var m in _manuals) m.AppliesToGateIds.RemoveAll(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
        foreach (var part in _parts) part.AppliesToGateIds.RemoveAll(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
        foreach (var flow in _flows) flow.AppliesToGateIds.RemoveAll(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
        _data.SaveGates(_gates); _data.SaveSystems(_units); _data.SaveResources(_resources); _data.SaveManualRecords(_manuals); _data.SaveParts(_parts); _data.SaveTroubleshootingFlows(_flows); _data.LogAudit("Gate removed from index", id, details: _selectedGate.Display); _selectedGate = null; ReloadAll();
    }

    private void NewUnit_Click(object sender, RoutedEventArgs e) { if (_selectedGate is null) { MessageBox.Show("Select or create a gate first."); return; } _selectedUnit = null; _newUnit = true; PopulateUnit(); GateUnitEditorScroll.ScrollToTop(); UnitNumberBox.Focus(); SetupTaskText.Text = "Enter the unit number, choose its photo and actual equipment, then Save Unit. Next: pilot checklist returns to the guided steps."; AdminStatusText.Text = "Enter a unit number and save the unit."; }
    private void SaveUnit_Click(object sender, RoutedEventArgs e)
    {
        try { SaveUnitCore(); }
        catch (Exception error) { AdminStatusText.Text = "Could not finish saving this unit: " + error.Message + " Check library access, then retry."; }
    }
    private void SaveUnitCore()
    {
        if (_selectedGate is null) { MessageBox.Show("Select a gate first."); return; }
        var number = UnitNumberBox.Text.Trim();
        string link = MobileSupportUrlBox.Text.Trim();
        if (link.Length > 0 && !MobileSupportExporter.ValidLink(link)) { AdminStatusText.Text = "Enter an approved HTTPS document link, or leave it blank for offline labels."; return; }
        if (_units.Any(u => u != _selectedUnit && u.GateId == _selectedGate.Id && string.Equals(u.UnitNumber ?? "", number, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show("That unit already exists under this gate."); return; }
        var wasNew = _newUnit || _selectedUnit is not null && !_data.LoadSystems().Any(u => u.Id == _selectedUnit.Id);
        if (_newUnit)
        {
            var id = Guid.NewGuid().ToString("N"); var root = string.IsNullOrWhiteSpace(_selectedGate.AssetFolder) ? $"Gates/{_selectedGate.Id}/Units/{id}" : _selectedGate.AssetFolder;
            _selectedUnit = new SafetySystem { Id = id, GateId = _selectedGate.Id, UnitNumber = string.IsNullOrWhiteSpace(number) ? null : number, SystemFolder = root, PhotosFolder = root + "/Photos", NotesFolder = root + "/Notes" };
            if (!string.IsNullOrWhiteSpace(_selectedGate.AssetFolder)) { _selectedUnit.PhotosFolder = root + "/Photos/" + AssetFileLayout.SafeName("Unit " + number); _selectedUnit.NotesFolder = root + "/Notes/" + AssetFileLayout.SafeName("Unit " + number); }
            _data.EnsureUnitFolders(_selectedGate, _selectedUnit); _units.Add(_selectedUnit); _newUnit = false;
        }
        if (_selectedUnit is null) return;
        _selectedUnit.UnitNumber = string.IsNullOrWhiteSpace(number) ? null : number;
        _selectedUnit.MobileSupportUrl = link; _selectedUnit.MobileOfflineInstructions = MobileOfflineBox.Text.Trim();
        _selectedUnit.ShowLightConfiguration = UnitHasSignalColumnCheck.IsChecked == true; _selectedUnit.ShowScannerConfiguration = UnitHasScannerCheck.IsChecked == true;
        if (!string.IsNullOrWhiteSpace(_pendingPhotoSource))
        {
            _data.SetIdentificationPhoto(_selectedUnit, _pendingPhotoSource);
            CleanupPendingPhotoTemp();
            _pendingPhotoSource = "";
        }
        _data.SynchronizeLegacyFields(_gates, _units); _data.SaveSystems(_units);
        if (wasNew) CreateDefaultResources(_selectedGate, _selectedUnit);
        var savedGateId = _selectedGate.Id;
        _data.LogAudit(wasNew ? "Unit created" : "Unit saved", savedGateId, _selectedUnit.Id, details: _selectedUnit.UnitDisplay);
        var uid = _selectedUnit.Id; ReloadAll(); SelectGateById(savedGateId); UnitListAdmin.SelectedItem = (UnitListAdmin.ItemsSource as IEnumerable<SafetySystem>)?.FirstOrDefault(u => u.Id == uid); PilotUnitBox.SelectedItem = _units.FirstOrDefault(u => u.Id == uid); AdminStatusText.Text = "Unit saved."; SetupTaskText.Text = "Unit saved. Use Next: pilot checklist to attach a PDF or Common Problem and test operator view.";
    }
    private void CreateDefaultResources(SafetyGate gate, SafetySystem unit)
    {
        ResourceDefinition Make(string label, string kind, int sort, string keywords, bool upload = false, string launch = "None", string archive = "SameExtension") => new()
        {
            Id = Guid.NewGuid().ToString("N"), GateId = gate.Id, Label = label, Kind = kind, AppliesToUnitIds = new() { unit.Id }, IsVisible = true, AllowUserUpload = upload, LaunchAction = launch, ArchiveMode = archive, SortOrder = sort, Keywords = keywords,
            RelativeFolder = kind == "Photos" ? unit.PhotosFolder : ""
        };
        if (!string.IsNullOrWhiteSpace(gate.AssetFolder))
        {
            foreach (string category in new[] { "Electrical Drawings", "Light Configurations", "Scanner Configurations" })
                if (!_resources.Any(r => r.GateId == gate.Id && r.StorageMode == "Asset" && AssetFileLayout.Category(r) == category)) _resources.Add(AssetFileLayout.Create(gate, category));
            _resources.Add(Make("Photos", "Photos", 60, "photos images pictures")); _resources.Add(Make("Manuals", "Manuals", 40, "manual documentation")); _resources.Add(Make("Parts / BOM", "Parts", 50, "parts bom"));
            _data.SaveResources(_resources); return;
        }
        _resources.Add(Make("Electrical Drawings", "ManagedFiles", 10, "drawing electrical wiring schematic print", true));
        _resources.Add(Make("Light Configuration", "ManagedFiles", 20, "light signal tower 8wd46 configuration", false, "Siemens", "All"));
        _resources.Add(Make("Scanner Configuration", "ManagedFiles", 30, "scanner safety laser uam configuration", false, "UAM", "All"));
        _resources.Add(Make("Manuals", "Manuals", 40, "manual documentation instructions"));
        _resources.Add(Make("Parts / BOM", "Parts", 50, "parts bom bill materials inventory"));
        _resources.Add(Make("Photos", "Photos", 60, "photos images pictures"));
        _data.SaveResources(_resources);
    }
    private void DeleteUnit_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGate is null || _selectedUnit is null) return;
        if (MessageBox.Show($"Remove {_selectedUnit.UnitDisplay} from {_selectedGate.Display}?\n\nShared files will NOT be deleted.", "Remove unit", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var uid = _selectedUnit.Id; _units.Remove(_selectedUnit);
        foreach (var r in _resources.ToList())
        {
            if (!r.AppliesToUnitIds.Contains(uid, StringComparer.OrdinalIgnoreCase)) continue;
            var originalCount = r.AppliesToUnitIds.Count; r.AppliesToUnitIds.RemoveAll(x => x.Equals(uid, StringComparison.OrdinalIgnoreCase)); if (originalCount == 1) _resources.Remove(r);
        }
        foreach (var m in _manuals) m.AppliesToUnitIds.RemoveAll(x => x.Equals(uid, StringComparison.OrdinalIgnoreCase));
        foreach (var part in _parts) part.AppliesToUnitIds.RemoveAll(x => x.Equals(uid, StringComparison.OrdinalIgnoreCase));
        foreach (var flow in _flows) flow.AppliesToUnitIds.RemoveAll(x => x.Equals(uid, StringComparison.OrdinalIgnoreCase));
        _data.SaveSystems(_units); _data.SaveResources(_resources); _data.SaveManualRecords(_manuals); _data.SaveParts(_parts); _data.SaveTroubleshootingFlows(_flows); _data.LogAudit("Unit removed from index", _selectedGate.Id, uid, details: _selectedUnit.UnitDisplay); RefreshUnitsForSelectedGate(); RefreshResourceList();
    }
    private static bool IsSupportedImageFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private void CleanupPendingPhotoTemp()
    {
        if (!_pendingPhotoIsTemporary || string.IsNullOrWhiteSpace(_pendingPhotoSource)) return;
        try { if (File.Exists(_pendingPhotoSource)) File.Delete(_pendingPhotoSource); } catch { }
        _pendingPhotoIsTemporary = false;
    }

    private void SetPendingPhoto(string path, string sourceLabel, bool temporary = false)
    {
        CleanupPendingPhotoTemp();
        _pendingPhotoSource = path;
        _pendingPhotoIsTemporary = temporary;
        UnitPhotoText.Text = $"{sourceLabel} (will copy when unit is saved)";
        LoadUnitPhotoPreview(path, true, sourceLabel);
    }

    private void ChoosePhoto_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp|All files|*.*", Title = "Choose identification photo" };
        if (d.ShowDialog() != true) return;
        SetPendingPhoto(d.FileName, Path.GetFileName(d.FileName));
    }

    private void PastePhoto_Click(object sender, RoutedEventArgs e) => PastePhotoFromClipboard();

    private void PastePhotoFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image is null) { MessageBox.Show("The clipboard image could not be read."); return; }
                var folder = Path.Combine(Path.GetTempPath(), "SafetyGateSupport", "Clipboard");
                Directory.CreateDirectory(folder);
                var temp = Path.Combine(folder, $"Identification_{Guid.NewGuid():N}.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(temp)) encoder.Save(stream);
                SetPendingPhoto(temp, "Clipboard image", true);
                return;
            }

            if (Clipboard.ContainsFileDropList())
            {
                var file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsSupportedImageFile);
                if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
                {
                    SetPendingPhoto(file, Path.GetFileName(file));
                    return;
                }
            }

            MessageBox.Show("There is no image on the clipboard. Use Win + Shift + S to make a snip, copy an image, or use Choose Photo.", "Paste identification photo");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The clipboard image could not be pasted.\n\n{ex.Message}", "Paste identification photo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UnitPhotoPreviewBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        UnitPhotoPreviewBorder.Focus();
    }

    private void UnitPhotoPreviewBorder_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            PastePhotoFromClipboard();
            e.Handled = true;
        }
    }

    private void UnitPhotoPreviewBorder_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(f => File.Exists(f) && IsSupportedImageFile(f)))
            e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void UnitPhotoPreviewBorder_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var file = files.FirstOrDefault(f => File.Exists(f) && IsSupportedImageFile(f));
        if (string.IsNullOrWhiteSpace(file))
        {
            MessageBox.Show("Drop a JPG, JPEG, PNG, WEBP, or BMP image here.");
            return;
        }
        SetPendingPhoto(file, Path.GetFileName(file));
        UnitPhotoPreviewBorder.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        CancelPhotoPreview();
        CleanupPendingPhotoTemp();
        base.OnClosed(e);
    }

    // Resources / buttons
    private void ResourceGateBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshResourceList();
    private void RefreshResourceList()
    {
        var gate = ResourceGateBox.SelectedItem as SafetyGate;
        ResourceList.ItemsSource = gate is null ? null : _resources.Where(r => r.GateId == gate.Id).OrderBy(r => r.SortOrder).ThenBy(r => r.Label).ToList();
        ResourceUnitsList.ItemsSource = gate is null ? null : _units.Where(u => u.GateId == gate.Id).OrderBy(u => SortNum(u.UnitNumber)).ToList();
        ResourceList.SelectedItem = null;
    }
    private void ResourceList_SelectionChanged(object sender, SelectionChangedEventArgs e) { _selectedResource = ResourceList.SelectedItem as ResourceDefinition; _newResource = false; PopulateResource(); }
    private void NewResource_Click(object sender, RoutedEventArgs e) { if (ResourceGateBox.SelectedItem is not SafetyGate) { MessageBox.Show("Choose a gate first."); return; } _selectedResource = null; _newResource = true; PopulateResource(); ResourceLabelBox.Focus(); }
    private void PopulateResource()
    {
        ResourceTargetBox.IsReadOnly = _selectedResource?.StorageMode == "Asset";
        ResourceLabelBox.Text = _selectedResource?.Label ?? ""; ResourceKindBox.SelectedItem = _selectedResource?.Kind ?? "ManagedFiles"; ResourceVisibleCheck.IsChecked = _selectedResource?.IsVisible ?? true; ResourceUploadCheck.IsChecked = _selectedResource?.AllowUserUpload ?? false; ResourceArchiveBox.SelectedItem = _selectedResource?.ArchiveMode ?? "SameExtension"; ResourceLaunchBox.SelectedItem = _selectedResource?.LaunchAction ?? "None"; ResourceTargetBox.Text = _selectedResource is null ? "" : (_selectedResource.Kind is "Url" or "Folder" ? _selectedResource.Target : _selectedResource.RelativeFolder); ResourceDescriptionBox.Text = _selectedResource?.Description ?? ""; ResourceKeywordsBox.Text = _selectedResource?.Keywords ?? ""; ResourceSortBox.Text = (_selectedResource?.SortOrder ?? 100).ToString();
        ResourceUnitsList.SelectedItems.Clear();
        if (_selectedResource is not null && ResourceUnitsList.ItemsSource is IEnumerable<SafetySystem> units)
            foreach (var u in units.Where(u => _selectedResource.AppliesToUnitIds.Contains(u.Id, StringComparer.OrdinalIgnoreCase))) ResourceUnitsList.SelectedItems.Add(u);
    }
    private void ResourceKindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var kind = ResourceKindBox.SelectedItem as string ?? "ManagedFiles"; ResourceUploadCheck.IsEnabled = kind == "ManagedFiles"; ResourceArchiveBox.IsEnabled = kind == "ManagedFiles"; ResourceLaunchBox.IsEnabled = kind == "ManagedFiles";
    }
    private void SaveResource_Click(object sender, RoutedEventArgs e)
    {
        if (ResourceGateBox.SelectedItem is not SafetyGate gate) { MessageBox.Show("Choose a gate first."); return; }
        var label = ResourceLabelBox.Text.Trim(); if (string.IsNullOrWhiteSpace(label)) { MessageBox.Show("Enter a button label."); return; }
        if (_newResource && !string.IsNullOrWhiteSpace(gate.AssetFolder) && (ResourceKindBox.SelectedItem as string) == "ManagedFiles")
        {
            var probe = new ResourceDefinition { Label = label, LaunchAction = ResourceLaunchBox.SelectedItem as string ?? "None" };
            if (_resources.Any(r => r.GateId == gate.Id && r.StorageMode == "Asset" && AssetFileLayout.Category(r) == AssetFileLayout.Category(probe))) { MessageBox.Show("This asset already has that file collection. Add or replace files in the existing collection instead of creating another button."); return; }
        }
        if (_newResource) { _selectedResource = new ResourceDefinition { Id = Guid.NewGuid().ToString("N"), GateId = gate.Id }; _resources.Add(_selectedResource); _newResource = false; }
        if (_selectedResource is null) return;
        _selectedResource.GateId = gate.Id; _selectedResource.Label = label; _selectedResource.Kind = ResourceKindBox.SelectedItem as string ?? "ManagedFiles"; _selectedResource.IsVisible = ResourceVisibleCheck.IsChecked == true; _selectedResource.AllowUserUpload = ResourceUploadCheck.IsChecked == true && _selectedResource.Kind == "ManagedFiles"; _selectedResource.ArchiveMode = ResourceArchiveBox.SelectedItem as string ?? "SameExtension"; _selectedResource.LaunchAction = ResourceLaunchBox.SelectedItem as string ?? "None"; _selectedResource.Description = ResourceDescriptionBox.Text.Trim(); _selectedResource.Keywords = ResourceKeywordsBox.Text.Trim(); _selectedResource.SortOrder = int.TryParse(ResourceSortBox.Text, out var sort) ? sort : 100; _selectedResource.AppliesToUnitIds = ResourceUnitsList.SelectedItems.Cast<SafetySystem>().Select(u => u.Id).ToList();
        if (_selectedResource.Kind == "ManagedFiles" && !string.IsNullOrWhiteSpace(gate.AssetFolder) && _selectedResource.StorageMode != "Asset")
        {
            _selectedResource.StorageCategory = AssetFileLayout.Category(_selectedResource); _selectedResource.StorageMode = "Asset";
            _selectedResource.RelativeFolder = gate.AssetFolder + "/" + _selectedResource.StorageCategory;
        }
        var target = ResourceTargetBox.Text.Trim(); if (_selectedResource.StorageMode == "Asset") { /* Folder changes use the reviewed organizer. */ } else if (_selectedResource.Kind is "Url" or "Folder") _selectedResource.Target = target; else { _selectedResource.RelativeFolder = target; if (_selectedResource.Kind == "ManagedFiles" && string.IsNullOrWhiteSpace(_selectedResource.RelativeFolder)) _selectedResource.RelativeFolder = $"Gates/{gate.Id}/Resources/{_selectedResource.Id}"; }
        if (_selectedResource.Kind is "ManagedFiles" or "Photos") _data.EnsureResourceFolders(_selectedResource, gate); _data.SaveResources(_resources); _data.LogAudit("Resource button saved", gate.Id, resourceId: _selectedResource.Id, details: $"{_selectedResource.Label} ({_selectedResource.Kind})"); RefreshResourceList(); AdminStatusText.Text = "Button/resource saved. Changes appear for users without rebuilding the EXE.";
    }
    private void DeleteResource_Click(object sender, RoutedEventArgs e) { if (_selectedResource is null) return; if (MessageBox.Show($"Delete the '{_selectedResource.Label}' button from the app index?\n\nIts files will NOT be deleted.", "Delete button", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; var id = _selectedResource.Id; var gateId = _selectedResource.GateId; _resources.Remove(_selectedResource); _data.SaveResources(_resources); _data.LogAudit("Resource button removed", gateId, resourceId: id, details: _selectedResource.Label); _selectedResource = null; RefreshResourceList(); }
    private void OpenResourceCurrent_Click(object sender, RoutedEventArgs e) { if (_selectedResource is null || ResourceGateBox.SelectedItem is not SafetyGate g || _selectedResource.Kind != "ManagedFiles") return; var p = _data.ResourceCurrentFolder(_selectedResource, g); Directory.CreateDirectory(p); FileLauncherService.Open(p); }
    private void OpenResourceArchive_Click(object sender, RoutedEventArgs e) { if (_selectedResource is null || ResourceGateBox.SelectedItem is not SafetyGate g || _selectedResource.Kind != "ManagedFiles") return; var p = _data.ResourceArchiveFolder(_selectedResource, g); Directory.CreateDirectory(p); FileLauncherService.Open(p); }

    // Troubleshooting tree
    private void FlowListAdmin_SelectionChanged(object sender, SelectionChangedEventArgs e) { _selectedFlow = FlowListAdmin.SelectedItem as TroubleshootingFlow; _newFlow = false; PopulateFlow(); RefreshNodeList(); }
    private void NewFlow_Click(object sender, RoutedEventArgs e) { _selectedFlow = null; _selectedNode = null; _newFlow = true; _newNode = false; PopulateFlow(); RefreshNodeList(); FlowTitleBox.Focus(); }
    private void PopulateFlow()
    {
        FlowTitleBox.Text = _selectedFlow?.Title ?? ""; FlowKeywordsBox.Text = _selectedFlow?.Keywords ?? ""; FlowManufacturerBox.Text = _selectedFlow?.ManufacturerFilter ?? "";
        var id = _selectedFlow?.AppliesToGateIds.FirstOrDefault() ?? ""; FlowGateBox.SelectedItem = (FlowGateBox.ItemsSource as IEnumerable<GateChoice>)?.FirstOrDefault(x => x.Id == id) ?? (FlowGateBox.ItemsSource as IEnumerable<GateChoice>)?.FirstOrDefault();
    }
    private void SaveFlow_Click(object sender, RoutedEventArgs e)
    {
        var title = FlowTitleBox.Text.Trim(); if (string.IsNullOrWhiteSpace(title)) { MessageBox.Show("Enter a troubleshooting flow title."); return; }
        if (_newFlow) { _selectedFlow = new TroubleshootingFlow { Id = Guid.NewGuid().ToString("N") }; _flows.Add(_selectedFlow); _newFlow = false; }
        if (_selectedFlow is null) return; _selectedFlow.Title = title; _selectedFlow.Keywords = FlowKeywordsBox.Text.Trim(); _selectedFlow.ManufacturerFilter = FlowManufacturerBox.Text.Trim(); var choice = FlowGateBox.SelectedItem as GateChoice; _selectedFlow.AppliesToGateIds = choice is null || string.IsNullOrWhiteSpace(choice.Id) ? new() : new() { choice.Id }; _data.SaveTroubleshootingFlows(_flows); _data.LogAudit("Troubleshooting flow saved", choice?.Id ?? "", details: title); ReloadTroubleshootingLists(_selectedFlow.Id); AdminStatusText.Text = "Troubleshooting flow saved.";
    }
    private void DeleteFlow_Click(object sender, RoutedEventArgs e) { if (_selectedFlow is null) return; if (MessageBox.Show($"Delete '{_selectedFlow.Title}'?", "Delete flow", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; _flows.Remove(_selectedFlow); _data.SaveTroubleshootingFlows(_flows); _selectedFlow = null; FlowListAdmin.ItemsSource = _flows.OrderBy(f => f.Title).ToList(); RefreshNodeList(); }
    private void ReloadTroubleshootingLists(string flowId) { FlowListAdmin.ItemsSource = null; FlowListAdmin.ItemsSource = _flows.OrderBy(f => f.Title).ToList(); FlowListAdmin.SelectedItem = _flows.FirstOrDefault(f => f.Id == flowId); }
    private void FlowGateBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshNodeResourceList();
    private void RefreshNodeList() { NodeListAdmin.ItemsSource = _selectedFlow?.Nodes; NodeListAdmin.SelectedItem = null; _selectedNode = null; PopulateNode(); RefreshNodeResourceList(); }
    private void NodeListAdmin_SelectionChanged(object sender, SelectionChangedEventArgs e) { _selectedNode = NodeListAdmin.SelectedItem as TroubleshootingNode; _newNode = false; PopulateNode(); }
    private void NewNode_Click(object sender, RoutedEventArgs e) { if (_selectedFlow is null) { MessageBox.Show("Create or select a troubleshooting flow first."); return; } _selectedNode = null; _newNode = true; PopulateNode(); NodePromptBox.Focus(); }
    private void PopulateNode()
    {
        NodeIdText.Text = _selectedNode is null ? "New step ID will be assigned when saved." : "Step ID: " + _selectedNode.Id; NodePromptBox.Text = _selectedNode?.Prompt ?? ""; NodeBodyBox.Text = _selectedNode?.Body ?? ""; NodeTerminalCheck.IsChecked = _selectedNode?.IsTerminal ?? false; NodeStartCheck.IsChecked = _selectedFlow is not null && _selectedNode is not null && _selectedFlow.StartNodeId == _selectedNode.Id; NodeChoicesBox.Text = _selectedNode is null ? "" : string.Join(Environment.NewLine, _selectedNode.Choices.Select(c => $"{c.Text} => {c.NextNodeId}")); RefreshNodeResourceList();
    }
    private void RefreshNodeResourceList()
    {
        if (NodeResourceList is null) return; var gateId = (FlowGateBox.SelectedItem as GateChoice)?.Id ?? ""; var list = string.IsNullOrWhiteSpace(gateId) ? _resources.OrderBy(r => r.Label).ToList() : _resources.Where(r => r.GateId == gateId).OrderBy(r => r.Label).ToList(); NodeResourceList.ItemsSource = list; NodeResourceList.SelectedItems.Clear(); if (_selectedNode is not null) foreach (var r in list.Where(r => _selectedNode.ResourceIds.Contains(r.Id, StringComparer.OrdinalIgnoreCase))) NodeResourceList.SelectedItems.Add(r);
    }
    private void SaveNode_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFlow is null) return; var prompt = NodePromptBox.Text.Trim(); if (string.IsNullOrWhiteSpace(prompt)) { MessageBox.Show("Enter a question or heading for this step."); return; }
        if (_newNode) { _selectedNode = new TroubleshootingNode { Id = Guid.NewGuid().ToString("N") }; _selectedFlow.Nodes.Add(_selectedNode); _newNode = false; }
        if (_selectedNode is null) return; _selectedNode.Prompt = prompt; _selectedNode.Body = NodeBodyBox.Text.Trim(); _selectedNode.IsTerminal = NodeTerminalCheck.IsChecked == true; _selectedNode.Choices = ParseChoices(NodeChoicesBox.Text); _selectedNode.ResourceIds = NodeResourceList.SelectedItems.Cast<ResourceDefinition>().Select(r => r.Id).ToList(); if (NodeStartCheck.IsChecked == true || string.IsNullOrWhiteSpace(_selectedFlow.StartNodeId)) _selectedFlow.StartNodeId = _selectedNode.Id; _data.SaveTroubleshootingFlows(_flows); _data.LogAudit("Troubleshooting step saved", details: $"{_selectedFlow.Title}: {_selectedNode.Prompt}"); var nid = _selectedNode.Id; NodeListAdmin.ItemsSource = null; NodeListAdmin.ItemsSource = _selectedFlow.Nodes; NodeListAdmin.SelectedItem = _selectedFlow.Nodes.FirstOrDefault(n => n.Id == nid); AdminStatusText.Text = "Troubleshooting step saved.";
    }
    private static List<TroubleshootingChoice> ParseChoices(string text)
    {
        var list = new List<TroubleshootingChoice>(); foreach (var raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)) { var parts = raw.Split(new[] { "=>" }, 2, StringSplitOptions.TrimEntries); if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0])) list.Add(new TroubleshootingChoice { Text = parts[0], NextNodeId = parts[1] }); } return list;
    }
    private void DeleteNode_Click(object sender, RoutedEventArgs e) { if (_selectedFlow is null || _selectedNode is null) return; if (MessageBox.Show($"Delete step '{_selectedNode.Prompt}'?", "Delete step", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; var id = _selectedNode.Id; _selectedFlow.Nodes.Remove(_selectedNode); foreach (var n in _selectedFlow.Nodes) n.Choices.RemoveAll(c => c.NextNodeId == id); if (_selectedFlow.StartNodeId == id) _selectedFlow.StartNodeId = _selectedFlow.Nodes.FirstOrDefault()?.Id ?? ""; _data.SaveTroubleshootingFlows(_flows); RefreshNodeList(); }

    // Home / Help
    private void HelpList_SelectionChanged(object sender, SelectionChangedEventArgs e) { _selectedHelp = HelpList.SelectedItem as HelpItem; _newHelp = false; PopulateHelp(); }
    private void PopulateHelp()
    {

        HelpTitleBox.Text = _selectedHelp?.Title ?? ""; HelpBodyBox.Text = _selectedHelp?.Body ?? "";
        HelpKeywordsBox.Text = _selectedHelp?.Keywords ?? ""; HelpSortBox.Text = (_selectedHelp?.SortOrder ?? 10).ToString();
        HelpSectionBox.SelectedIndex = _selectedHelp?.Section == "Common Problems" ? 2 : _selectedHelp?.Section == "Troubleshooting" ? 1 : 0;
        HelpVisibleCheck.IsChecked = _selectedHelp?.Visible ?? true; ResetHelpPhotos();
    }
    private void NewHelp_Click(object sender, RoutedEventArgs e) { _selectedHelp = null; _newHelp = true; PopulateHelp(); HelpTitleBox.Focus(); }
    private async void SaveHelp_Click(object sender, RoutedEventArgs e)
    {
        string title = HelpTitleBox.Text.Trim(), body = HelpBodyBox.Text.Trim();
        string section = (HelpSectionBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Quick Help";
        if (title.Length == 0 || section == "Common Problems" && body.Length == 0) { AdminStatusText.Text = "Enter a title and fix instructions for the common problem."; return; }
        if (!_newHelp && _selectedHelp is null) { AdminStatusText.Text = "Choose a topic or click + Topic first."; return; }
        var item = new HelpItem { Id = _selectedHelp?.Id ?? Guid.NewGuid().ToString("N"), Section = section, Title = title, Body = body,
            Keywords = HelpKeywordsBox.Text.Trim(), Visible = HelpVisibleCheck.IsChecked == true, SortOrder = int.TryParse(HelpSortBox.Text, out var n) ? n : 10 };
        var photos = _helpPhotos.Select(p => p.Path).ToList();
        var existingPhotos = _selectedHelp?.Photos ?? [];
        SetupTabs.IsEnabled = false;
        try
        {
            foreach (string photo in photos.Where(p => !existingPhotos.Contains(p)))
                if ((await IdentificationPhotoLoader.LoadAsync(photo)).Source is null) throw new IOException("Choose readable photos.");
            await Task.Run(() => _data.SaveHelpTopicPhotos(item, photos));
            if (_isClosed) return;
            _help = _data.LoadHelpItems(); _newHelp = false;
            HelpList.ItemsSource = _help.OrderBy(h => h.Section).ThenBy(h => h.SortOrder).ThenBy(h => h.Title).ToList();
            HelpList.SelectedItem = _help.Single(h => h.Id == item.Id); AdminStatusText.Text = "Help topic saved to shared data.";
        }
        catch (Exception error) { if (!_isClosed) AdminStatusText.Text = "Help topic not saved: " + error.Message; }
        finally { if (!_isClosed) SetupTabs.IsEnabled = true; }
    }
    private void DeleteHelp_Click(object sender, RoutedEventArgs e) { if (_selectedHelp is null) return; if (MessageBox.Show($"Delete '{_selectedHelp.Title}'?", "Delete", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; _help.Remove(_selectedHelp); _data.SaveHelpItems(_help); _selectedHelp = null; HelpList.ItemsSource = _help.OrderBy(h => h.Section).ThenBy(h => h.SortOrder).ToList(); }

    // Manuals
    private void ManualListAdmin_SelectionChanged(object sender, SelectionChangedEventArgs e) { _selectedManual = ManualListAdmin.SelectedItem as ManualRecord; _newManual = false; _pendingManualSource = ""; PopulateManual(); }
    private void NewManual_Click(object sender, RoutedEventArgs e) { _selectedManual = null; _newManual = true; _pendingManualSource = ""; PopulateManual(); ManualTitleBox.Focus(); }
    private void PopulateManual()
    {
        ManualTitleBox.Text = _selectedManual?.Title ?? ""; ManualManufacturerBox.Text = _selectedManual?.Manufacturer ?? ""; ManualModelBox.Text = _selectedManual?.Model ?? ""; ManualKeywordsBox.Text = _selectedManual?.Keywords ?? ""; ManualFileText.Text = _selectedManual?.FilePath ?? ""; ManualGateAssignments.SelectedItems.Clear(); ManualUnitAssignments.SelectedItems.Clear(); if (_selectedManual is not null && ManualGateAssignments.ItemsSource is IEnumerable<SafetyGate> gates) foreach (var g in gates.Where(g => _selectedManual.AppliesToGateIds.Contains(g.Id, StringComparer.OrdinalIgnoreCase))) ManualGateAssignments.SelectedItems.Add(g); if (_selectedManual is not null && ManualUnitAssignments.ItemsSource is IEnumerable<SafetySystem> units) foreach (var u in units.Where(u => _selectedManual.AppliesToUnitIds.Contains(u.Id, StringComparer.OrdinalIgnoreCase))) ManualUnitAssignments.SelectedItems.Add(u);
    }
    private void ChooseManualFile_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Title = "Choose manual", Filter = "Documents|*.pdf;*.doc;*.docx;*.txt|All files|*.*" }; if (d.ShowDialog() != true) return; _pendingManualSource = d.FileName; ManualFileText.Text = Path.GetFileName(d.FileName) + " (will copy when saved)"; if (string.IsNullOrWhiteSpace(ManualTitleBox.Text)) ManualTitleBox.Text = Path.GetFileNameWithoutExtension(d.FileName); }
    private void SaveManual_Click(object sender, RoutedEventArgs e) { var title = ManualTitleBox.Text.Trim(); if (string.IsNullOrWhiteSpace(title)) { MessageBox.Show("Enter a manual title."); return; } if (_newManual) { _selectedManual = new ManualRecord(); _manuals.Add(_selectedManual); _newManual = false; } if (_selectedManual is null) return; _selectedManual.Title = title; _selectedManual.Manufacturer = ManualManufacturerBox.Text.Trim(); _selectedManual.Model = ManualModelBox.Text.Trim(); _selectedManual.Keywords = ManualKeywordsBox.Text.Trim(); _selectedManual.AppliesToGateIds = ManualGateAssignments.SelectedItems.Cast<SafetyGate>().Select(g => g.Id).ToList(); _selectedManual.AppliesToUnitIds = ManualUnitAssignments.SelectedItems.Cast<SafetySystem>().Select(u => u.Id).ToList(); if (!string.IsNullOrWhiteSpace(_pendingManualSource)) { var dest = _data.AddOrUpdateManual(_pendingManualSource); _selectedManual.FilePath = _data.ToRelative(dest); _pendingManualSource = ""; } _data.SaveManualRecords(_manuals); _data.LogAudit("Manual saved", details: title); ManualListAdmin.ItemsSource = _manuals.OrderBy(m => m.Title).ToList(); AdminStatusText.Text = "Manual saved."; }
    private void DeleteManual_Click(object sender, RoutedEventArgs e) { if (_selectedManual is null) return; if (MessageBox.Show($"Remove '{_selectedManual.Title}' from the manual index?\n\nThe document file will be preserved.", "Delete manual", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; _manuals.Remove(_selectedManual); _data.SaveManualRecords(_manuals); _selectedManual = null; ManualListAdmin.ItemsSource = _manuals.OrderBy(m => m.Title).ToList(); }
    private void OpenManualAdmin_Click(object sender, RoutedEventArgs e) { if (_selectedManual is null || string.IsNullOrWhiteSpace(_selectedManual.FilePath)) return; var p = _data.Resolve(_selectedManual.FilePath); if (File.Exists(p)) FileLauncherService.Open(p); }

    // Parts
    private void AdminPartList_SelectionChanged(object sender, SelectionChangedEventArgs e) { _selectedPart = AdminPartList.SelectedItem as PartRecord; _newPart = false; _pendingPartDocumentSource = ""; PopulatePart(); }
    private void NewPart_Click(object sender, RoutedEventArgs e) { _selectedPart = null; _newPart = true; _pendingPartDocumentSource = ""; PopulatePart(); PartNumberBox.Focus(); }
    private void PopulatePart()
    {
        PartBinBox.Text = _selectedPart?.LocationValue ?? ""; PartQuantityBox.Text = _selectedPart?.QuantityValue is { } quantity ? PartRecord.FormatQuantity(quantity) : ""; PartSiteBox.Text = _selectedPart?.SiteValue ?? "";
        PartNumberBox.Text = _selectedPart?.PartNumber ?? ""; PartDescriptionBox.Text = _selectedPart?.Description ?? ""; PartManufacturerBox.Text = _selectedPart?.Manufacturer ?? ""; PartKeywordsBox.Text = _selectedPart?.Keywords ?? ""; PartAssetsBox.Text = _selectedPart?.RelatedAssets ?? ""; PartNotesBox.Text = _selectedPart?.Notes ?? ""; PartDocumentText.Text = _selectedPart?.DocumentPath ?? ""; PartGateAssignments.SelectedItems.Clear(); PartUnitAssignments.SelectedItems.Clear(); if (_selectedPart is not null && PartGateAssignments.ItemsSource is IEnumerable<SafetyGate> gates) foreach (var g in gates.Where(g => _selectedPart.AppliesToGateIds.Contains(g.Id, StringComparer.OrdinalIgnoreCase))) PartGateAssignments.SelectedItems.Add(g); if (_selectedPart is not null && PartUnitAssignments.ItemsSource is IEnumerable<SafetySystem> units) foreach (var u in units.Where(u => _selectedPart.AppliesToUnitIds.Contains(u.Id, StringComparer.OrdinalIgnoreCase))) PartUnitAssignments.SelectedItems.Add(u);
    }
    private void ChoosePartDocument_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Title = "Choose part document", Filter = "Documents|*.pdf;*.xlsx;*.xls;*.csv;*.doc;*.docx;*.txt|All files|*.*" }; if (d.ShowDialog() != true) return; _pendingPartDocumentSource = d.FileName; PartDocumentText.Text = Path.GetFileName(d.FileName) + " (will copy when saved)"; }
    private void SavePart_Click(object sender, RoutedEventArgs e) { decimal? quantity = null; if (!string.IsNullOrWhiteSpace(PartQuantityBox.Text)) { if (!decimal.TryParse(PartQuantityBox.Text.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed < 0) { MessageBox.Show("Enter a nonnegative reported quantity, or leave it blank."); return; } quantity = parsed; } var pn = PartNumberBox.Text.Trim(); var desc = PartDescriptionBox.Text.Trim(); if (string.IsNullOrWhiteSpace(pn) && string.IsNullOrWhiteSpace(desc)) { MessageBox.Show("Enter a part number or description."); return; } if (_newPart) { _selectedPart = new PartRecord(); _parts.Add(_selectedPart); _newPart = false; } if (_selectedPart is null) return; _selectedPart.BinLocation = PartBinBox.Text.Trim(); _selectedPart.ReportedQuantity = quantity; _selectedPart.Site = PartSiteBox.Text.Trim(); _selectedPart.PartNumber = pn; _selectedPart.Description = desc; _selectedPart.Manufacturer = PartManufacturerBox.Text.Trim(); _selectedPart.Keywords = PartKeywordsBox.Text.Trim(); _selectedPart.RelatedAssets = PartAssetsBox.Text.Trim(); _selectedPart.Notes = PartNotesBox.Text.Trim(); _selectedPart.Notes = _selectedPart.NotesDisplay; _selectedPart.AppliesToGateIds = PartGateAssignments.SelectedItems.Cast<SafetyGate>().Select(g => g.Id).ToList(); _selectedPart.AppliesToUnitIds = PartUnitAssignments.SelectedItems.Cast<SafetySystem>().Select(u => u.Id).ToList(); if (!string.IsNullOrWhiteSpace(_pendingPartDocumentSource)) { var dest = _data.AddOrUpdatePartDocument(_pendingPartDocumentSource); _selectedPart.DocumentPath = _data.ToRelative(dest); _pendingPartDocumentSource = ""; } _data.SaveParts(_parts); _data.LogAudit("Part saved", details: _selectedPart.Display); AdminPartList.ItemsSource = _parts.OrderBy(p => p.PartNumber).ThenBy(p => p.Description).ToList(); AdminStatusText.Text = "Part saved."; }
    private void DeletePart_Click(object sender, RoutedEventArgs e) { if (_selectedPart is null) return; if (MessageBox.Show($"Delete '{_selectedPart.Display}' from the parts index?", "Delete", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; _parts.Remove(_selectedPart); _data.SaveParts(_parts); _selectedPart = null; AdminPartList.ItemsSource = _parts.OrderBy(p => p.PartNumber).ThenBy(p => p.Description).ToList(); }

    // Shared content / areas / software
    private void SaveSharedContent_Click(object sender, RoutedEventArgs e) { if (!_data.IsReady) { MessageBox.Show("Initialize shared data first."); return; } var content = new SharedContent { HomeSubtitle = HomeSubtitleBox.Text.Trim(), ManualsIntro = ManualsIntroBox.Text.Trim(), PartsIntro = PartsIntroBox.Text.Trim(), StoreroomUrl = StoreroomUrlBox.Text.Trim() }; _data.SaveSharedContent(content); _data.LogAudit("Shared content saved", details: "Home/help text or storeroom URL updated"); AdminStatusText.Text = "Shared content settings saved."; }
    private void AreaList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (AreaList.SelectedItem is string a) AreaNameBox.Text = a; }
    private async void AddArea_Click(object sender, RoutedEventArgs e) => await ChangeAreaAsync(null, AreaNameBox.Text.Trim());
    private async void RenameArea_Click(object sender, RoutedEventArgs e)
    {
        if (AreaList.SelectedItem is not string old || old == AreaNameBox.Text.Trim()) return;
        await ChangeAreaAsync(old, AreaNameBox.Text.Trim());
    }
    private async void DeleteArea_Click(object sender, RoutedEventArgs e)
    {
        if (AreaList.SelectedItem is not string area) return;
        if (_gates.Any(g => g.Category.Equals(area, StringComparison.OrdinalIgnoreCase))) { AdminStatusText.Text = "Move the gates out of this area before deleting it."; return; }
        if (MessageBox.Show(this, $"Delete area '{area}'?", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) await ChangeAreaAsync(area, null);
    }
    private async Task ChangeAreaAsync(string? oldName, string? newName)
    {
        if (_areaSaveBusy) return;
        if (newName is not null && newName.Length == 0) { AdminStatusText.Text = "Enter an area name."; return; }
        string? filter = GateAreaFilterBox.SelectedItem as string, gateId = _selectedGate?.Id, unitId = _selectedUnit?.Id;
        _areaSaveBusy = true; IsEnabled = false; AdminStatusText.Text = "Saving area change…";
        try
        {
            var saved = await Task.Run(() => _data.ChangeArea(oldName, newName));
            if (_isClosed) return;
            _areas = saved.Areas; _gates = saved.Gates; _units = saved.Units;
            BindInventoryLists();
            GateAreaFilterBox.SelectedItem = string.Equals(filter, oldName, StringComparison.OrdinalIgnoreCase) ? newName : filter;
            if (GateAreaFilterBox.SelectedItem is null && _areas.Count > 0) GateAreaFilterBox.SelectedIndex = 0;
            RefreshGateList(); RefreshResourceList();
            if (gateId is not null && _gates.Any(g => g.Id == gateId))
            {
                SelectGateById(gateId);
                if (unitId is not null) UnitListAdmin.SelectedItem = _units.FirstOrDefault(u => u.Id == unitId);
            }
            AreaList.SelectedItem = newName; AreaNameBox.Text = newName ?? "";
            AdminStatusText.Text = oldName is null ? "Area added." : newName is null ? "Area deleted." : "Area renamed. Gate and unit assignments retained.";
        }
        catch (Exception error) { if (!_isClosed) AdminStatusText.Text = "Area change failed: " + error.Message; }
        finally { _areaSaveBusy = false; if (!_isClosed) IsEnabled = true; }
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_areaSaveBusy) { e.Cancel = true; AdminStatusText.Text = "Wait for the area save to finish before closing Setup."; }
        base.OnClosing(e);
    }

    private void ChooseSharedRoot_Click(object sender, RoutedEventArgs e) { var d = new OpenFolderDialog { Title = "Select the shared Safety Gate Support folder", Multiselect = false }; if (Directory.Exists(SharedRootBox.Text)) d.InitialDirectory = SharedRootBox.Text; if (d.ShowDialog(this) == true) SharedRootBox.Text = d.FolderName; }
    private void Initialize_Click(object sender, RoutedEventArgs e) { try { _data.SetSharedRoot(SharedRootBox.Text.Trim()); _data.InitializeSharedData(); ReloadAll(); AdminStatusText.Text = "Shared starter data initialized."; } catch (Exception ex) { MessageBox.Show(ex.Message, "Initialization failed", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private void BrowseSiemens_Click(object sender, RoutedEventArgs e) => BrowseExe(SiemensBox, "Select Siemens 8WD46 Configurator");
    private void BrowseUam_Click(object sender, RoutedEventArgs e) => BrowseExe(UamBox, "Select UAM Project Designer");
    private static void BrowseExe(System.Windows.Controls.TextBox box, string title) { var d = new OpenFileDialog { Title = title, Filter = "Programs|*.exe|All files|*.*" }; if (d.ShowDialog() == true) box.Text = d.FileName; }
    private void SaveLocalSettings_Click(object sender, RoutedEventArgs e) { try { _data.SetSharedRoot(SharedRootBox.Text.Trim()); _data.SetSoftwarePaths(SiemensBox.Text, UamBox.Text); AdminStatusText.Text = "Local software paths saved for this computer."; ReloadAll(); } catch (Exception ex) { MessageBox.Show(ex.Message, "Unable to save settings", MessageBoxButton.OK, MessageBoxImage.Warning); } }

    // Audit
    private async void RefreshAudit_Click(object sender, RoutedEventArgs e)
    {
        try { var entries = await Task.Run(() => _data.LoadRecentAudit(300)); if (!_isClosed) AuditList.ItemsSource = entries; }
        catch (Exception error) { if (!_isClosed) AdminStatusText.Text = "Cannot refresh audit history: " + error.Message; }
    }

    public sealed class GateChoice { public string Id { get; set; } = ""; public string Display { get; set; } = ""; }
}
