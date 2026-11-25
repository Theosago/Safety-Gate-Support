using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading;
using System.Windows.Threading;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class MainWindow : Window
{
    private readonly DataService _data;
    private List<SafetyGate> _gates = new();
    private List<SafetySystem> _systems = new();
    private List<ResourceDefinition> _resources = new();
    private List<string> _categories = new();
    private List<HelpItem> _help = new();
    private List<PartRecord> _parts = new();
    private List<ManualRecord> _manuals = new();
    private List<TroubleshootingFlow> _flows = new();
    private SharedContent _content = new();
    private string _currentArea = "";
    private string _manualFilterGateId = "";
    private string _manualFilterUnitId = "";

    private readonly DispatcherTimer _operatorDriveTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _notesSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly DispatcherTimer _notesRefreshTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _searchIndexRefreshTimer = new() { Interval = TimeSpan.FromSeconds(45) };
    private SafetySystem? _notesSystem;
    private bool _loadingSharedNotes;
    private bool _sharedNotesDirty;
    private DateTime _sharedNotesLastWriteUtc = DateTime.MinValue;
    private string _sharedNotesBase = "";
    private List<SearchIndexEntry> _searchIndex = new();
    private bool _searchIndexReady;
    private readonly Dictionary<string, string> _searchNotesUpdates = new(StringComparer.Ordinal);
    private CancellationTokenSource? _searchIndexCancellation;
    private CancellationTokenSource? _photoLoadCancellation;
    private bool _sharedDataReady;
    private bool _loadingData;
    private bool _setupModeOpen;
    private bool _isClosed;
    private int _dataGeneration;

    public MainWindow() : this(HomePreviewService.IsRequested(Environment.GetCommandLineArgs(), Environment.ProcessPath)
        ? HomePreviewService.Create() : new DataService(initializeSharedDefaults: false)) { }

    public MainWindow(DataService data, Func<string, CancellationToken, Task<StockroomService.Snapshot>>? stockroomReader = null)
    {
        InitializeComponent();
        _data = data; _stockroomReader = stockroomReader ?? ((url, token) =>
        {
            var ids = _parts.Select(part => part.PartNumber).Where(id => id.Length > 0).Distinct().ToArray();
            return Task.Run(() => StockroomWebViewReader.FetchCatalogAsync(url, ids, token), token);
        });
        InitializeStockroomPlants(); InitializeReplacementParts(); InitializeTheme();
        InitializeUnitGallery(); InitializeDrawingMonitor();
        if (data.IsOnboardingDemo)
        {
            Title = "Safety Gate Support — Fictional operator example";
            HomeHeadingText.Text = "Try the operator workflow";
            HomeSubtitleText.Visibility = Visibility.Collapsed;
            SupportLibraryNav.Visibility = Visibility.Collapsed;
            CloseDemoButton.Visibility = Visibility.Visible;
        }
        else if (data.IsHomePreview) Title = "Safety Gate Support — Home Preview (local data)";
        _notesSaveTimer.Tick += (_, _) => { _notesSaveTimer.Stop(); SaveSharedNotesNow(); };
        _notesRefreshTimer.Tick += NotesRefreshTimer_Tick;
        _searchDebounceTimer.Tick += (_, _) => { _searchDebounceTimer.Stop(); RunGlobalSearch(); };
        _searchIndexRefreshTimer.Tick += (_, _) => _ = RefreshSearchIndexAsync();
        _notesRefreshTimer.Start();
        _searchIndexRefreshTimer.Start();
        if (!data.IsHomePreview && !data.IsOnboardingDemo)
        {
            _operatorDriveTimer.Tick += async (_, _) =>
            {
                if (!_sharedDataReady) return;
                await OperatorDriveRunner.RunAsync(_data);
                await RefreshDrawingPublishStatusAsync();
            };
            _operatorDriveTimer.Start();
        }
        Closing += (_, args) => { if (InlineBusy) { args.Cancel = true; MessageBox.Show(this, "Finish or cancel the current USB/file operation before closing."); } else { CloseInlineTool(); SaveSharedNotesNow(); } };
        Closed += (_, _) =>
        {
            _isClosed = true;
            _dataGeneration++;
            CancelSearchIndexRefresh();
            CancelPhotoLoad(); CancelPartPhotos(); _commonProblemPhotos?.Cancel(); _problemDetailPhotos?.Cancel(); _stockroomRefresh?.Cancel();
            _drawingListing?.Cancel(); _drawingRendering?.Cancel(); _drawingOrientationCancellation?.Cancel(); _drawingPairCheck?.Cancel(); _drawingWatch.Stop();
            _galleryCancellation?.Cancel(); _unitSearchTimer.Stop();
            _notesSaveTimer.Stop();
            _notesRefreshTimer.Stop();
            _searchDebounceTimer.Stop();
            _searchIndexRefreshTimer.Stop();
            _operatorDriveTimer.Stop();
        };
        ShowHome();
        HomeSubtitleText.Text = _data.IsHomePreview ? "HOME PREVIEW • Local starter photos and units. Select a photo card, or search Asset DEMO to explore the sample light configuration. USB is disabled." : _content.HomeSubtitle;
        ConnectionText.Text = "Connecting to shared data…";
        SearchHintText.Text = "Loading shared data…";
        IsEnabled = false;
        Loaded += async (_, _) => await ReloadDataAsync();
    }

    private SafetyGate? SelectedGate => AssetList.SelectedItem as SafetyGate;
    private SafetySystem? SelectedUnit => UnitList.SelectedItem as SafetySystem;
    private PartRecord? SelectedPart => PartList.SelectedItem as PartRecord;

    private async Task ReloadDataAsync()
    {
        if (_loadingData || _isClosed) return;
        _loadingData = true;
        _sharedDataReady = false;
        IsEnabled = false;
        _dataGeneration++;
        CancelSearchIndexRefresh();
        CancelPhotoLoad();
        try
        {
            // Network-drive probes, migrations and JSON reads must not hold up the WPF dispatcher.
            var snapshot = await Task.Run(() =>
            {
                if (_data.IsHomePreview && !_data.IsOnboardingDemo) HomePreviewService.Initialize(_data);
                if (!_data.IsReady) return null;
                _data.EnsureSharedDefaults();
                var gates = _data.LoadGates();
                return new SharedDataSnapshot(gates, _data.LoadSystems(gates), _data.LoadResources(),
                    _data.LoadCategories(), _data.LoadHelpItems(), _data.LoadParts(),
                    _data.LoadManualRecords(), _data.LoadTroubleshootingFlows(), _data.LoadSharedContent());
            });
            if (_isClosed) return;
            if (snapshot is null)
            {
                _gates = new(); _systems = new(); _resources = new(); _categories = new(); _help = new(); _parts = new(); _manuals = new(); _flows = new(); _content = new();
                _searchIndex = new(); _searchIndexReady = false;
                CommonProblemsList.ItemsSource = null; _commonProblemPhotos?.Cancel(); CommonProblemsEmpty.Visibility = Visibility.Visible;
                AreaBox.ItemsSource = null; HomeCategoryPanel.Children.Clear(); QuickHelpPanel.Children.Clear(); TroubleshootingPanel.Children.Clear();
                ShowSearchResults(null);
                SearchHintText.Text = "Shared data is unavailable.";
                UpdateConnectionStatus();
                RefreshUnitGallery();
                return;
            }
            _gates = snapshot.Gates; _systems = snapshot.Systems; _resources = snapshot.Resources;
            _categories = snapshot.Categories; _help = snapshot.Help; _parts = snapshot.Parts;
            _manuals = snapshot.Manuals; _flows = snapshot.Flows; _content = snapshot.Content;
            _sharedDataReady = true;
            UpdateConnectionStatus();
            AreaBox.ItemsSource = _categories;
            if (_data.IsOnboardingDemo) GalleryIntroText.Text = _content.HomeSubtitle;
            else if (!_data.IsHomePreview) HomeSubtitleText.Text = _content.HomeSubtitle;
            ManualsIntroText.Text = _content.ManualsIntro;
            PartsIntroText.Text = _content.PartsIntro;
            BuildHomeCategories();
            RefreshUnitGallery();
            BuildHelpPanels();
            RefreshManuals();
            RefreshParts(); RefreshReplacementParts();
            if (!_data.IsHomePreview && _parts.Count > 0 && SelectedStockroomPlant is { } plant && _stockroomRequestedUrl != plant.Url) { _stockroomRequestedUrl = plant.Url; _ = RefreshStockroomAsync(); }
            BuildFastSearchIndex();
            _ = RefreshSearchIndexAsync();
            if (!string.IsNullOrWhiteSpace(_currentArea) && _categories.Contains(_currentArea)) AreaBox.SelectedItem = _currentArea;
        }
        catch (Exception ex)
        {
            if (!_isClosed)
            {
                UpdateConnectionStatus();
                SearchHintText.Text = "Unable to load shared data.";
                MessageBox.Show(ex.Message, "Unable to load shared data", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally { _loadingData = false; if (!_isClosed) { IsEnabled = true; if (HomeShell.IsVisible) UnitSearchBox.Focus(); } }
    }

    private void PublishDrive_Click(object sender, RoutedEventArgs e)
    {
        if (!_sharedDataReady || !OperatorDriveRunner.IsSupported(_data)) return;
        var publishing = new OperatorDriveWindow(_data, SelectedUnit?.Id) { Owner = this };
        publishing.ShowDialog();
        if (publishing.GuidePdfsChanged && SelectedGate is { } gate && SelectedUnit is { } unit) LoadDrawings(gate, unit);
    }

    private void MaintainXWorkOrders_Click(object sender, RoutedEventArgs e)
    {
        if (!_sharedDataReady || _data.IsHomePreview || _data.IsOnboardingDemo || SelectedUnit is not { } unit) return;
        new MaintainXWorkOrdersWindow(_data, unit) { Owner = this }.ShowDialog();
    }

    private void UpdateConnectionStatus()
    {
        PublishDriveNav.Visibility = _sharedDataReady && OperatorDriveRunner.IsSupported(_data) ? Visibility.Visible : Visibility.Collapsed;
        ImportManualsButton.Visibility = _data.IsHomePreview ? Visibility.Collapsed : Visibility.Visible;
        ImportManualsButton.IsEnabled = _sharedDataReady;
        if (_data.IsOnboardingDemo)
        {
            ConnectionText.Text = "Fictional example";
            SetupButton.Visibility = Visibility.Collapsed;
            return;
        }
        SetupButton.Visibility = Visibility.Visible;
        if (_sharedDataReady)
        {
            ConnectionText.Text = (_data.IsHomePreview ? "● HOME PREVIEW • local data\n" : "● Connected\n") + _data.SharedRoot;
            ConnectionText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(132, 218, 193));
        }
        else
        {
            ConnectionText.Text = "● Shared data unavailable\n" + _data.SharedRoot;
            ConnectionText.Foreground = System.Windows.Media.Brushes.LightSalmon;
        }
    }

    private void BuildHomeCategories()
    {
        HomeCategoryPanel.Children.Clear();
        foreach (var category in _categories)
        {
            var button = new Button { Tag = category, MinWidth = 90, MinHeight = 36, Content = category, Margin = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Center };
            button.Click += (_, _) => SelectGalleryArea(category);
            HomeCategoryPanel.Children.Add(button);
        }
    }

    private void BuildHelpPanels()
    {
        QuickHelpPanel.Children.Clear(); TroubleshootingPanel.Children.Clear();
        BuildCommonProblems();
        foreach (var item in _help.Where(h => h.Visible && !h.Section.Equals("Common Problems", StringComparison.OrdinalIgnoreCase)).OrderBy(h => h.SortOrder).ThenBy(h => h.Title))
        {
            var button = new Button { Content = item.Title, Tag = item.Id, HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) => new HelpWindow(item, _data) { Owner = this }.ShowDialog();
            if (item.Section.Equals("Troubleshooting", StringComparison.OrdinalIgnoreCase)) TroubleshootingPanel.Children.Add(button);
            else QuickHelpPanel.Children.Add(button);
        }
        if (QuickHelpPanel.Children.Count == 0) QuickHelpPanel.Children.Add(new TextBlock { Text = "No quick help topics configured.", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(6) });
        if (TroubleshootingPanel.Children.Count == 0) TroubleshootingPanel.Children.Add(new TextBlock { Text = "No troubleshooting topics configured.", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(6) });
    }

    private void HomeNav_Click(object sender, RoutedEventArgs e) => ShowHome();
    private void SystemsNav_Click(object sender, RoutedEventArgs e) { if (SelectedUnit is null) ShowHome(); else ShowSystems(); }
    private void TroubleshootNav_Click(object sender, RoutedEventArgs e) { if (CloseInlineTool()) ShowTroubleshoot(); }
    private void ManualsNav_Click(object sender, RoutedEventArgs e) { if (!CloseInlineTool()) return; _manualFilterGateId = _manualFilterUnitId = ""; ShowManuals(); RefreshManuals(); }
    private void PartsNav_Click(object sender, RoutedEventArgs e) { if (!CloseInlineTool()) return; ShowParts(); RefreshParts(); }

    private void HideAllViews() { SetPartsRail(false); _problemDetailPhotos?.Cancel(); CommonProblemView.Visibility = CommonProblemsRail.Visibility = Visibility.Collapsed; HomeShell.Visibility = SystemsView.Visibility = ManualsView.Visibility = PartsView.Visibility = TroubleshootView.Visibility = Visibility.Collapsed; }
    private void ShowHome() { HideAllViews(); HomeShell.Visibility = CommonProblemsRail.Visibility = Visibility.Visible; SetActiveNavigation(HomeNav); }
    private void ShowSystems() { HideAllViews(); SystemsView.Visibility = CommonProblemsRail.Visibility = Visibility.Visible; SetActiveNavigation(SystemsNav); if (SelectedUnit is null) EnsureSystemAreaSelected(); }
    private void ShowTroubleshoot() { HideAllViews(); TroubleshootView.Visibility = CommonProblemsRail.Visibility = Visibility.Visible; SetActiveNavigation(TroubleshootNav); }
    private void ShowManuals() { HideAllViews(); ManualsView.Visibility = Visibility.Visible; SetActiveNavigation(ManualsNav); }
    private void ShowParts() { HideAllViews(); PartsView.Visibility = Visibility.Visible; SetActiveNavigation(PartsNav); if (SelectedPart is { } part) LoadPartPhoto(part); }

    private void SetActiveNavigation(Button active)
    {
        foreach (var button in new[] { HomeNav, SystemsNav, TroubleshootNav, ManualsNav, PartsNav })
            button.Background = ReferenceEquals(button, active) ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 69, 88)) : System.Windows.Media.Brushes.Transparent;
    }
    private void EnsureSystemAreaSelected()
    {
        if (_categories.Count == 0) return;
        var target = !string.IsNullOrWhiteSpace(_currentArea) && _categories.Contains(_currentArea) ? _currentArea
            : _categories.FirstOrDefault(a => a.Equals("East Side", StringComparison.OrdinalIgnoreCase))
              ?? _categories.FirstOrDefault(a => a.Equals("West Side", StringComparison.OrdinalIgnoreCase))
              ?? _categories.FirstOrDefault(a => a.Equals("906 / Outside", StringComparison.OrdinalIgnoreCase))
              ?? _categories[0];
        if (!Equals(AreaBox.SelectedItem, target)) AreaBox.SelectedItem = target;
        else { _currentArea = target; RefreshGates(false); }
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var needed = ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !_loadingData) { e.Handled = true; FocusSearch(); return; }
        if (key != Key.A || (Keyboard.Modifiers & needed) != needed) return;
        e.Handled = true;
        if (_loadingData) return;
        await OpenSetupAsync();
    }
    private async Task OpenSetupAsync()
    {
        if (_data.IsOnboardingDemo || _setupModeOpen || _loadingData || _isClosed || !CloseInlineTool()) return;
        SaveSharedNotesNow();
        _setupModeOpen = true;
        CancelSearchIndexRefresh();
        _notesRefreshTimer.Stop();
        _searchIndexRefreshTimer.Stop();
        string? pilotId = null;
        try
        {
            var setup = new AdminWindow(_data) { Owner = this, InitialPilotUnitId = SelectedUnit?.Id };
            setup.ShowDialog();
            pilotId = setup.OperatorPreviewUnitId;
        }
        finally { _setupModeOpen = false; }
        await ReloadDataAsync();
        if (!_isClosed) { _notesRefreshTimer.Start(); _searchIndexRefreshTimer.Start(); }
        if (_sharedDataReady) RefreshGates(true);
        if (pilotId is not null && _systems.FirstOrDefault(u => u.Id == pilotId) is { } pilot) NavigateToSystem(pilot);
    }

    private async void SetupSharedData_Click(object sender, RoutedEventArgs e) => await OpenSetupAsync();
    private void CloseDemo_Click(object sender, RoutedEventArgs e) => Close();

    private const string SearchHintDefault = "Type an asset number, location, part, manual or note. Press Ctrl+F from any page.";

    // The one search bar drives both the unit photo gallery and the "Matches" list.
    private void QueueGlobalSearch()
    {
        _searchDebounceTimer.Stop();
        var q = UnitSearchBox.Text.Trim();
        if (q.Length < 2)
        {
            ShowSearchResults(null);
            SearchHintText.Text = q.Length == 0 ? SearchHintDefault : "Type at least 2 characters to search documents, parts and notes.";
            return;
        }
        SearchHintText.Text = _searchIndexReady ? "Searching…" : "Searching available data while shared files finish indexing in the background…";
        _searchDebounceTimer.Start();
    }
    private void ShowSearchResults(List<GlobalSearchResult>? results)
    {
        GlobalSearchResults.ItemsSource = results;
        GlobalSearchResults.Visibility = SearchResultsPanel.Visibility = results is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        SearchResultsHeading.Text = results is { Count: > 0 } ? $"Matches ({results.Count})" : "Matches";
    }
    private void FocusSearch() { if (HomeShell.Visibility != Visibility.Visible) ShowHome(); UnitSearchBox.Focus(); UnitSearchBox.SelectAll(); }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { UnitSearchBox.Clear(); UnitSearchBox.Focus(); }
    private void SearchResultItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: GlobalSearchResult result }) { e.Handled = true; OpenSearchResult(result); }
    }
    private void GlobalSearchResults_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && GlobalSearchResults.SelectedItem is GlobalSearchResult result) { e.Handled = true; OpenSearchResult(result); }
        else if (e.Key == Key.Escape) { e.Handled = true; UnitSearchBox.Focus(); }
    }

    private void BuildFastSearchIndex()
    {
        var entries = new List<SearchIndexEntry>();
        foreach (var s in _systems)
        {
            var g = GateFor(s); if (g is null) continue;
            entries.Add(SystemIndexEntry(g, s, ""));
        }
        foreach (var h in _help.Where(h => h.Visible))
            entries.Add(new SearchIndexEntry { Title = h.Title, BaseSubtitle = h.Section, Kind = "Help", Body = h.Body, SearchText = $"{h.Title} {h.Body} {h.Keywords} {h.Section}" });
        foreach (var p in _parts)
            entries.Add(new SearchIndexEntry { Title = p.Display, BaseSubtitle = string.IsNullOrWhiteSpace(p.Manufacturer) ? "Part" : "Part • " + p.Manufacturer, Kind = "Part", Target = p.Id, SearchText = $"{p.PartNumber} {p.Description} {p.Manufacturer} {p.Keywords} {p.Notes} {p.RelatedAssets} {p.LocationValue} {p.SiteValue} part parts" });
        foreach (var m in _manuals)
            entries.Add(new SearchIndexEntry { Title = m.Display, BaseSubtitle = "Manual", Kind = "Manual", Target = m.Id, SearchText = $"{m.Title} {m.Manufacturer} {m.Model} {m.Keywords} manual documentation" });
        foreach (var r in _resources.Where(r => r.IsVisible))
        {
            var g = _gates.FirstOrDefault(x => x.Id == r.GateId);
            entries.Add(new SearchIndexEntry { Title = r.Label, BaseSubtitle = g is null ? "Resource" : $"Resource • {g.Display}", Kind = "Resource", Target = r.Id, SearchText = $"{r.Label} {r.Kind} {r.Keywords} {r.Description} {g?.Display}" });
        }
        _searchIndex = entries; _searchIndexReady = false;
        SearchHintText.Text = "Search is ready. Shared notes and file names are being indexed in the background.";
    }

    private SearchIndexEntry SystemIndexEntry(SafetyGate gate, SafetySystem unit, string notes) => new()
    {
        Title = $"{gate.Display} / {unit.UnitDisplay}",
        BaseSubtitle = BuildSystemSubtitle(gate), Kind = "System", SystemId = unit.Id, NotesText = notes,
        SearchText = BuildSystemSearchText(gate, unit, notes)
    };

    private void CancelSearchIndexRefresh()
    {
        var previous = _searchIndexCancellation;
        _searchIndexCancellation = null;
        previous?.Cancel();
    }

    private async Task RefreshSearchIndexAsync()
    {
        if (!_sharedDataReady || _setupModeOpen || _isClosed || _searchIndexCancellation is not null) return;
        var cancellation = new CancellationTokenSource();
        _searchIndexCancellation = cancellation;
        var token = cancellation.Token;
        var generation = _dataGeneration;
        var systems = _systems.ToList(); var gates = _gates.ToList(); var help = _help.ToList();
        var parts = _parts.ToList(); var manuals = _manuals.ToList(); var resources = _resources.ToList();
        _searchNotesUpdates.Clear();
        try
        {
            var entries = await Task.Run(() =>
            {
                var built = new List<SearchIndexEntry>();
                var gatesById = gates.ToLookup(g => g.Id, StringComparer.Ordinal);
                foreach (var s in systems)
                {
                    token.ThrowIfCancellationRequested();
                    var g = gatesById[s.GateId].FirstOrDefault(); if (g is null) continue;
                    string notes = ""; try { notes = _data.LoadSystemNotes(s); } catch { }
                    built.Add(SystemIndexEntry(g, s, notes));
                }
                foreach (var h in help.Where(h => h.Visible)) built.Add(new SearchIndexEntry { Title = h.Title, BaseSubtitle = h.Section, Kind = "Help", Body = h.Body, SearchText = $"{h.Title} {h.Body} {h.Keywords} {h.Section}" });
                foreach (var p in parts) built.Add(new SearchIndexEntry { Title = p.Display, BaseSubtitle = string.IsNullOrWhiteSpace(p.Manufacturer) ? "Part" : "Part • " + p.Manufacturer, Kind = "Part", Target = p.Id, SearchText = $"{p.PartNumber} {p.Description} {p.Manufacturer} {p.Keywords} {p.Notes} {p.RelatedAssets} {p.LocationValue} {p.SiteValue} part parts" });
                foreach (var m in manuals) built.Add(new SearchIndexEntry { Title = m.Display, BaseSubtitle = "Manual", Kind = "Manual", Target = m.Id, SearchText = $"{m.Title} {m.Manufacturer} {m.Model} {m.Keywords} manual documentation" });
                foreach (var r in resources.Where(x => x.IsVisible))
                {
                    var g = gatesById[r.GateId].FirstOrDefault();
                    built.Add(new SearchIndexEntry { Title = r.Label, BaseSubtitle = g is null ? "Resource" : $"Resource • {g.Display}", Kind = "Resource", Target = r.Id, SearchText = $"{r.Label} {r.Kind} {r.Keywords} {r.Description} {g?.Display}" });
                }
                try
                {
                    // Refresh file metadata once per scan so changes made by other PCs remain visible.
                    foreach (var f in _data.GetCurrentSystemFiles(systems, cancellationToken: token))
                    {
                        var g = gatesById[f.System.GateId].FirstOrDefault();
                        built.Add(new SearchIndexEntry { Title = Path.GetFileName(f.File), BaseSubtitle = $"{g?.Display} / {f.System.UnitDisplay} • {f.Resource}", Kind = "File", Target = f.File, SystemId = f.System.Id, SearchText = $"{Path.GetFileName(f.File)} {f.Resource} {g?.Display} {f.System.UnitDisplay}" });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* Keep searchable inventory and notes when a shared resource is unreadable. */ }
                return built;
            }, token);
            if (_isClosed || token.IsCancellationRequested || generation != _dataGeneration) return;
            // A note saved while the scan was running must not be replaced by its earlier copy.
            foreach (var entry in entries.Where(e => e.Kind == "System"))
            {
                if (!_searchNotesUpdates.TryGetValue(entry.SystemId, out var notes)) continue;
                var unit = systems.FirstOrDefault(s => s.Id == entry.SystemId);
                var gate = unit is null ? null : GateFor(unit);
                if (unit is null || gate is null) continue;
                entry.NotesText = notes;
                entry.SearchText = BuildSystemSearchText(gate, unit, notes);
            }
            _searchIndex = entries; _searchIndexReady = true;
            if (UnitSearchBox.Text.Trim().Length >= 2) RunGlobalSearch();
            else SearchHintText.Text = SearchHintDefault;
        }
        catch (OperationCanceledException) { }
        catch { if (!_isClosed && generation == _dataGeneration) _searchIndexReady = false; }
        finally
        {
            if (ReferenceEquals(_searchIndexCancellation, cancellation)) _searchIndexCancellation = null;
            cancellation.Dispose();
        }
    }

    private void RunGlobalSearch()
    {
        if (!_sharedDataReady) { SearchHintText.Text = "Shared data is unavailable."; ShowSearchResults(null); return; }
        var q = UnitSearchBox.Text.Trim(); if (q.Length < 2) { ShowSearchResults(null); return; }
        var results = SearchService.Search(_searchIndex, q);
        ShowSearchResults(results);
        SearchHintText.Text = results.Count == 0 ? $"No documents, parts or notes match '{q}'." : "Click a match to open it. Press ↓ to move into the list, Esc to clear.";
    }

    private static bool Contains(string? value, string q) => !string.IsNullOrWhiteSpace(value) && value.Contains(q, StringComparison.OrdinalIgnoreCase);
    private static string BuildSystemSearchText(SafetyGate g, SafetySystem u, string notes) => $"{g.AssetNumber} {g.AssetDisplay} {g.Manufacturer} {g.Model} {g.Location} {g.EquipmentType} {g.Keywords} {u.UnitNumber} {u.UnitDisplay} {g.Category} {notes}";
    private static string BuildSystemSubtitle(SafetyGate g) => string.Join(" • ", new[] { g.Category, g.Model, g.Location, g.EquipmentType }.Where(x => !string.IsNullOrWhiteSpace(x)));
    private void OpenSearchResult(GlobalSearchResult r)
    {
        switch (r.Kind)
        {
            case "System":
                var u = _systems.FirstOrDefault(x => x.Id == r.SystemId); if (u is not null) NavigateToSystem(u, r.MatchSource == "SharedNotes" ? r.MatchText : null); break;
            case "File": if (File.Exists(r.Target)) FileLauncherService.Open(r.Target); break;
            case "Help": var h = _help.FirstOrDefault(x => x.Title == r.Title && x.Body == r.Body); if (h is not null) { if (h.Section.Equals("Common Problems", StringComparison.OrdinalIgnoreCase)) OpenCommonProblem(h); else new HelpWindow(h, _data) { Owner = this }.ShowDialog(); } break;
            case "Part": var p = _parts.FirstOrDefault(x => x.Id == r.Target); ShowParts(); if (p is not null) { PartList.SelectedItem = p; PartList.ScrollIntoView(p); } break;
            case "Manual": var m = _manuals.FirstOrDefault(x => x.Id == r.Target); if (m is not null) { var path = _data.Resolve(m.FilePath); if (File.Exists(path)) FileLauncherService.Open(path); } break;
            case "Resource": var resource = _resources.FirstOrDefault(x => x.Id == r.Target); if (resource is not null) NavigateToResource(resource); break;
        }
    }

    private SafetyGate? GateFor(SafetySystem unit) => _gates.FirstOrDefault(g => g.Id.Equals(unit.GateId, StringComparison.OrdinalIgnoreCase));

    private void NavigateToSystem(SafetySystem unit, string? highlightNoteText = null)
    {
        if (InlineBusy) { MessageBox.Show(this, "Finish or cancel the current operation before changing units."); return; }
        var gate = GateFor(unit); if (gate is null) return;
        ShowSystems(); _currentArea = gate.Category; AreaBox.SelectedItem = gate.Category; RefreshGates(false);
        var gateItem = (AssetList.ItemsSource as IEnumerable<SafetyGate>)?.FirstOrDefault(g => g.Id == gate.Id); if (gateItem is null) return;
        AssetList.SelectedItem = gateItem; AssetList.ScrollIntoView(gateItem);
        var units = UnitsForGate(gate.Id); UnitList.ItemsSource = units;
        var selected = units.FirstOrDefault(x => x.Id == unit.Id); if (selected is null) return;
        UnitList.SelectedItem = selected; UnitList.ScrollIntoView(selected);
        if (!string.IsNullOrWhiteSpace(highlightNoteText)) HighlightSharedNoteMatch(highlightNoteText);
    }

    private void NavigateToResource(ResourceDefinition resource)
    {
        var gate = _gates.FirstOrDefault(g => g.Id == resource.GateId); if (gate is null) return;
        var unit = _systems.FirstOrDefault(u => u.GateId == gate.Id && resource.AppliesTo(u.Id));
        if (unit is null) return;
        NavigateToSystem(unit);
        OpenResource(resource, gate, unit);
    }

    private void HighlightSharedNoteMatch(string q)
    {
        var text = SharedNotesBox.Text ?? ""; var i = text.IndexOf(q, StringComparison.OrdinalIgnoreCase); var len = q.Length;
        if (i < 0) { var token = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(); if (!string.IsNullOrWhiteSpace(token)) { i = text.IndexOf(token, StringComparison.OrdinalIgnoreCase); len = token.Length; } }
        if (i < 0) return; SharedNotesBox.Focus(); SharedNotesBox.Select(i, Math.Min(len, text.Length - i)); var line = SharedNotesBox.GetLineIndexFromCharacterIndex(i); if (line >= 0) SharedNotesBox.ScrollToLine(line); SharedNotesStatusText.Text = "Search match shown in shared notes • changes still auto-save";
    }

    private void AreaBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (AreaBox.SelectedItem is string area) { _currentArea = area; RefreshGates(false); } }

    private void RefreshGates(bool preserveSelection)
    {
        if (AssetList is null || string.IsNullOrWhiteSpace(_currentArea)) return;
        var oldId = preserveSelection ? SelectedGate?.Id : null;
        var gates = _gates.Where(g => g.Category.Equals(_currentArea, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => AssetSortKey(g.AssetNumber)).ThenBy(g => g.Manufacturer, StringComparer.OrdinalIgnoreCase).ToList();
        AssetList.ItemsSource = gates; UnitList.ItemsSource = null; ClearUnit();
        if (oldId is not null) AssetList.SelectedItem = gates.FirstOrDefault(g => g.Id == oldId);
    }
    private static int AssetSortKey(string? asset) => int.TryParse(asset, out var n) ? n : int.MaxValue;
    private static int UnitSortKey(SafetySystem u) => int.TryParse(u.UnitNumber, out var n) ? n : int.MaxValue;
    private List<SafetySystem> UnitsForGate(string gateId) => _systems.Where(u => u.GateId.Equals(gateId, StringComparison.OrdinalIgnoreCase)).OrderBy(UnitSortKey).ThenBy(u => u.UnitDisplay).ToList();

    private void AssetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var gate = SelectedGate; if (gate is null) { UnitList.ItemsSource = null; ClearUnit(); return; }
        UnitList.ItemsSource = UnitsForGate(gate.Id); UnitList.SelectedItem = null; ClearUnit();
    }
    private void UnitList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (SelectedUnit is { } u) ShowUnitDetails(u); else ClearUnit(); }

    private void ShowUnitDetails(SafetySystem unit)
    {
        CloseInlineTool();
        SaveSharedNotesNow();
        var gate = GateFor(unit); if (gate is null) { ClearUnit(); return; }
        NoUnitPanel.Visibility = Visibility.Collapsed; UnitDetailPanel.Visibility = Visibility.Visible;
        DrawingPublishStatus.Text = "";
        _drawingPublicationUnits = [];
        string asset = gate.AssetKey == "Unassigned" ? gate.Display : gate.AssetDisplay;
        UnitTitleText.Text = $"{asset} · {unit.UnitDisplay}";
        UnitAreaText.Text = string.Join(" • ", new[] { gate.Category, gate.Location }.Where(s => !string.IsNullOrWhiteSpace(s)));
        UnitPartsButton.IsEnabled = true;
        RefreshReplacementParts(); LoadPhoto(unit); LoadSharedNotes(unit); UpdateEquipmentActions(unit); LoadDrawings(gate, unit);
    }

    private void ClearUnit()
    {
        CancelPhotoLoad();
        ClearDrawings();
        UnitTitleText.Text = "Select a unit"; UnitAreaText.Text = ""; DrawingPublishStatus.Text = ""; _drawingPublicationUnits = [];
        SaveSharedNotesNow(); _notesSystem = null; _sharedNotesDirty = false; _loadingSharedNotes = true; SharedNotesBox.Text = ""; _loadingSharedNotes = false;
        NoUnitPanel.Visibility = Visibility.Visible; UnitDetailPanel.Visibility = Visibility.Collapsed; GateImage.Source = null;
        MaintainXWorkOrdersButton.Visibility = Visibility.Collapsed;
        UnitPartsButton.IsEnabled = false;
        UnitPartsButton.Content = "Replacement parts";
    }

    private void UnitOverview_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        bool stack = e.NewSize.Width < 620;
        UnitCardGap.Width = new GridLength(stack ? 0 : 16);
        UnitDrawingColumn.Width = stack ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(UnitDrawingCard, stack ? 1 : 0);
        Grid.SetColumn(UnitDrawingCard, stack ? 0 : 2);
        UnitDrawingCard.Margin = stack ? new Thickness(0, 12, 0, 0) : new Thickness(0);
    }
    private void UpdateEquipmentActions(SafetySystem unit)
    {
        MaintainXWorkOrdersButton.Visibility = _sharedDataReady && !_data.IsHomePreview && !_data.IsOnboardingDemo
            ? Visibility.Visible : Visibility.Collapsed;
        LightColumnButton.Visibility = unit.ShowLightConfiguration ? Visibility.Visible : Visibility.Collapsed;
        ScannerButton.Visibility = unit.ShowScannerConfiguration ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UnitManuals_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGate is not { } gate || SelectedUnit is not { } unit) return;
        _manualFilterGateId = gate.Id; _manualFilterUnitId = unit.Id; ShowManuals(); RefreshManuals();
    }
    private void UnitParts_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGate is not { } gate || SelectedUnit is not { } unit) return;
        SetPartsRail(PartsRail.Visibility != Visibility.Visible);
    }

    private async void SupportLibrary_Click(object sender, RoutedEventArgs e)
    {
        new SupportLibraryWindow(_data, SelectedGate, SelectedUnit) { Owner = this }.ShowDialog();
        await ReloadDataAsync();
    }

    private void OpenResource(ResourceDefinition resource, SafetyGate gate, SafetySystem unit)
    {
        switch (resource.Kind.ToLowerInvariant())
        {
            case "manuals": _manualFilterGateId = gate.Id; _manualFilterUnitId = unit.Id; ShowManuals(); RefreshManuals(); break;
            case "parts": SetPartsRail(PartsRail.Visibility != Visibility.Visible); break;
            case "url": if (!string.IsNullOrWhiteSpace(resource.Target)) FileLauncherService.Open(resource.Target); break;
            case "folder": var folder = _data.Resolve(resource.Target); Directory.CreateDirectory(folder); FileLauncherService.Open(folder); break;
            default: new ResourceWindow(_data, gate, unit, resource) { Owner = this }.ShowDialog(); _ = RefreshSearchIndexAsync(); break;
        }
    }

    private void LoadSharedNotes(SafetySystem system)
    {
        _notesSystem = system; _sharedNotesDirty = false; _notesSaveTimer.Stop(); _loadingSharedNotes = true;
        try { SharedNotesBox.Text = _sharedNotesBase = _data.LoadSystemNotes(system); _sharedNotesLastWriteUtc = _data.GetSystemNotesLastWriteUtc(system); SharedNotesStatusText.Text = "Shared notes • saved to Q: and visible to other users"; }
        catch (Exception ex) { SharedNotesBox.Text = _sharedNotesBase = ""; _sharedNotesLastWriteUtc = DateTime.MinValue; SharedNotesStatusText.Text = "Unable to load shared notes: " + ex.Message; }
        finally { _loadingSharedNotes = false; }
    }
    private void SharedNotesBox_TextChanged(object sender, TextChangedEventArgs e) { if (_loadingSharedNotes || _notesSystem is null) return; _sharedNotesDirty = true; SharedNotesStatusText.Text = "Saving shared notes…"; _notesSaveTimer.Stop(); _notesSaveTimer.Start(); }
    private void SaveSharedNotesNow()
    {
        if (!_sharedNotesDirty || _notesSystem is null || _loadingSharedNotes) return;
        try
        {
            string draft = SharedNotesBox.Text;
            if (!_data.TryUpdateSystemNotes(_notesSystem, _sharedNotesBase, draft))
            {
                _data.SaveNotesRecoveryCopy(_notesSystem, draft, draft: true);
                LoadSharedNotes(_notesSystem);
                UpdateSearchIndexSystemNotes(_notesSystem, _sharedNotesBase);
                SharedNotesStatusText.Text = "Another edit arrived. Your draft is saved in the notes Archive folder.";
                return;
            }
            _sharedNotesBase = draft;
            _sharedNotesLastWriteUtc = _data.GetSystemNotesLastWriteUtc(_notesSystem);
            _sharedNotesDirty = false;
            UpdateSearchIndexSystemNotes(_notesSystem, draft);
            SharedNotesStatusText.Text = "Shared notes • saved to Q: and visible to other users";
        }
        catch (Exception ex) { SharedNotesStatusText.Text = "Shared notes could not be saved: " + ex.Message; }
    }

    private void NotesRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_notesSystem is null || _sharedNotesDirty || _loadingSharedNotes) return;
        try
        {
            var latest = _data.GetSystemNotesLastWriteUtc(_notesSystem);
            if (latest <= _sharedNotesLastWriteUtc) return;
            _loadingSharedNotes = true;
            int caret = SharedNotesBox.CaretIndex;
            SharedNotesBox.Text = _sharedNotesBase = _data.LoadSystemNotes(_notesSystem);
            SharedNotesBox.CaretIndex = Math.Min(caret, SharedNotesBox.Text.Length);
            _sharedNotesLastWriteUtc = latest;
            UpdateSearchIndexSystemNotes(_notesSystem, SharedNotesBox.Text);
            SharedNotesStatusText.Text = "Shared notes • updated from another user";
        }
        catch { }
        finally { _loadingSharedNotes = false; }
    }

    private void UpdateSearchIndexSystemNotes(SafetySystem unit, string notes)
    {
        var entry = _searchIndex.FirstOrDefault(x => x.Kind == "System" && x.SystemId == unit.Id); var gate = GateFor(unit); if (entry is null || gate is null) return;
        if (_searchIndexCancellation is not null) _searchNotesUpdates[unit.Id] = notes ?? "";
        entry.NotesText = notes ?? ""; entry.SearchText = BuildSystemSearchText(gate, unit, entry.NotesText); if (UnitSearchBox.Text.Trim().Length >= 2) { _searchDebounceTimer.Stop(); _searchDebounceTimer.Start(); }
    }

    private void CancelPhotoLoad()
    {
        var previous = _photoLoadCancellation;
        _photoLoadCancellation = null;
        previous?.Cancel();
        previous?.Dispose();
    }

    private async void LoadPhoto(SafetySystem system)
    {
        CancelPhotoLoad();
        GateImage.Source = null;
        UnitPhotoMessage.Visibility = Visibility.Visible;
        UnitPhotoMessage.Text = "Loading identification photo…";
        if (string.IsNullOrWhiteSpace(system.PrimaryPhoto) || _isClosed) { UnitPhotoMessage.Text = "No identification photo saved for this unit."; return; }
        var cancellation = new CancellationTokenSource();
        _photoLoadCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            var result = await IdentificationPhotoLoader.LoadAsync(_data.Resolve(system.PrimaryPhoto), token);
            if (!_isClosed && !token.IsCancellationRequested)
            {
                GateImage.Source = result.Source;
                UnitPhotoMessage.Visibility = result.Source is null ? Visibility.Visible : Visibility.Collapsed;
                UnitPhotoMessage.Text = result.Status == IdentificationPhotoStatus.Missing ? "Saved photo unavailable. Check the shared drive or photo assignment." : "This photo could not be previewed.";
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!_isClosed && !token.IsCancellationRequested)
            {
                UnitPhotoMessage.Visibility = Visibility.Visible;
                UnitPhotoMessage.Text = "This photo could not be previewed. Check its assignment.";
            }
        }
    }

    private void FindFiles_Click(object sender, RoutedEventArgs e) => ShowInlineResource("Electrical Drawings", assetWideDrawings: true);

    private void StartGuidedTroubleshooting_Click(object sender, RoutedEventArgs e)
    {
        if (_systems.Count == 0)
        {
            MessageBox.Show(this, "Connect a plant library with units or add one in Plant setup.", "No units available", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new TroubleshootPickerWindow(_data, _gates, _systems, _categories) { Owner = this };
        if (picker.ShowDialog() == true && picker.ChosenSystem is { } unit && GateFor(unit) is { } gate)
            new TroubleshootWindow(_data, gate, unit, _resources, _flows, "builtin-triage-v7") { Owner = this }.ShowDialog();
    }
    private void LightColumn_Click(object sender, RoutedEventArgs e) => ShowInlineResource("Light Configurations");
    private void Scanner_Click(object sender, RoutedEventArgs e) => ShowInlineResource("Scanner Configurations");
    private void Plc_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGate is { } gate && SelectedUnit is { } unit) new PlcWindow(_data, gate, unit) { Owner = this }.ShowDialog();
    }

    private void RefreshManuals()
    {
        if (ManualList is null) return;
        IEnumerable<ManualRecord> source = _manuals;
        if (!string.IsNullOrWhiteSpace(_manualFilterGateId) && !string.IsNullOrWhiteSpace(_manualFilterUnitId)) source = source.Where(m => SelectedUnit?.ManualIds is { } ids ? ids.Contains(m.Id, StringComparer.OrdinalIgnoreCase) : m.AppliesTo(_manualFilterGateId, _manualFilterUnitId));
        var items = source
            .Select(m => new ManualFileItem { Id = m.Id, Name = m.Display, Path = _data.Resolve(m.FilePath) }).OrderBy(m => m.Name).ToList();
        ManualList.ItemsSource = items;
        ManualsIntroText.Text = string.IsNullOrWhiteSpace(_manualFilterGateId) ? _content.ManualsIntro : "Showing manuals linked to the selected gate/unit. Use the Manuals navigation button to return to the full library.";
    }
    private void ManualList_DoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedManual();
    private void OpenManual_Click(object sender, RoutedEventArgs e) => OpenSelectedManual();
    private void OpenSelectedManual() { if (ManualList.SelectedItem is ManualFileItem m && File.Exists(m.Path)) FileLauncherService.Open(m.Path); }
    private void OpenManualFolder_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(_data.ManualLibraryFolder); FileLauncherService.Open(_data.ManualLibraryFolder); }

    private async void ImportManuals_Click(object sender, RoutedEventArgs e)
    {
        if (!_sharedDataReady || _data.IsHomePreview) return;
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose documents to add to the shared library",
            Filter = "PDF and Word documents|*.pdf;*.docx",
            Multiselect = true
        };
        if (picker.ShowDialog(this) != true) return;

        IsEnabled = false;
        ManualImportStatus.Text = "Importing documents into the shared library…";
        try
        {
            string[] files = picker.FileNames;
            var result = await Task.Run(() => SupportLibraryService.ImportDocuments(_data, files));
            if (_isClosed) return;
            _manualFilterGateId = _manualFilterUnitId = "";
            await ReloadDataAsync();
            if (_isClosed) return;
            var summary = $"{result.Added} imported. {result.Existing} already in the library.";
            ManualImportStatus.Text = string.Join(Environment.NewLine, result.Errors.Prepend(summary));
        }
        catch (Exception error)
        {
            if (!_isClosed) ManualImportStatus.Text = "Document import could not finish. " + error.Message;
        }
        finally { if (!_isClosed) IsEnabled = true; }
    }

    private void RefreshParts()
    {
        if (PartList is null) return;
        string? selected = SelectedPart?.Id;
        var items = _parts.OrderBy(p => p.Description).ThenBy(p => p.LocationValue).ThenBy(p => p.PartNumber).ToList();
        PartList.ItemsSource = items; PartList.SelectedItem = items.FirstOrDefault(p => p.Id == selected) ?? items.FirstOrDefault();
        PartCatalogEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PartCatalogEmpty.Text = !_sharedDataReady ? "Connect a plant library in Plant setup to see its parts."
            : _parts.Count == 0 ? "No parts saved. Add a part in Plant setup → Parts."
            : "No parts to show.";
    }
    private void PartList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var p = SelectedPart;
        UploadPartPhotoButton.IsEnabled = p is not null && !_partPhotoSaving;
        if (p is null)
        {
            PartTitleText.Text = "Select a part."; PartLocationText.Text = PartStockText.Text = PartManufacturerText.Text = PartRelatedText.Text = PartNotesText.Text = PartReportedQuantityText.Text = "";
            _partPhotoLoad?.Cancel(); PartPhotoImage.Source = null; PartPhotoMessage.Text = "Select a part to see its photo."; PartPhotoMessage.Visibility = Visibility.Visible; EnlargePartPhotoButton.IsEnabled = false; UpdatePartQuantity(); return;
        }
        PartTitleText.Text = p.Description; PartLocationText.Text = p.LocationDisplay; PartStockText.Text = "Item ID: " + p.PartNumber;
        PartManufacturerText.Text = string.IsNullOrWhiteSpace(p.Manufacturer) ? "" : "Manufacturer: " + p.Manufacturer;
        var related = new List<string>(); related.AddRange(_gates.Where(g => p.AppliesToGateIds.Contains(g.Id)).Select(g => g.Display)); related.AddRange(_systems.Where(u => p.AppliesToUnitIds.Contains(u.Id)).Select(u => $"{GateFor(u)?.Display} / {u.UnitDisplay}"));
        PartRelatedText.Text = related.Count == 0 ? (string.IsNullOrWhiteSpace(p.RelatedAssets) ? "" : "Related: " + p.RelatedAssets) : "Applies to: " + string.Join(", ", related);
        PartNotesText.Text = string.Join(Environment.NewLine + Environment.NewLine, new[] { p.StockroomDescription, p.NotesDisplay }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct()); PartReportedQuantityText.Text = "Reported quantity (snapshot): " + p.QuantityDisplay + (p.SiteValue.Length > 0 ? " • Site: " + p.SiteValue : "");
        PartDetailScroll.ScrollToTop(); if (PartsView.IsVisible) LoadPartPhoto(p); UpdatePartQuantity();
    }
    private void OpenPartDocument_Click(object sender, RoutedEventArgs e) { var p = SelectedPart; if (p is null || string.IsNullOrWhiteSpace(p.DocumentPath)) { MessageBox.Show("No document is attached to this part."); return; } var path = _data.Resolve(p.DocumentPath); if (File.Exists(path)) FileLauncherService.Open(path); else MessageBox.Show("The attached part document could not be found."); }
    private void OpenStoreroom_Click(object sender, RoutedEventArgs e) { if (SelectedStockroomPlant is { } plant) FileLauncherService.Open(plant.Url); else StockroomPlantBox.Focus(); }
    private async void CheckStoreroom_Click(object sender, RoutedEventArgs e) => await RefreshStockroomAsync();

    private sealed record SharedDataSnapshot(List<SafetyGate> Gates, List<SafetySystem> Systems,
        List<ResourceDefinition> Resources, List<string> Categories, List<HelpItem> Help,
        List<PartRecord> Parts, List<ManualRecord> Manuals, List<TroubleshootingFlow> Flows,
        SharedContent Content);

    private sealed class ManualFileItem { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Path { get; set; } = ""; }
}
