using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;
using SafetyGateSupport.Siemens;
using UsbColumnPort = SiemensUsbCheck.Services.UsbColumnPort;
using UsbSession = SiemensUsbCheck.Services.UsbCheckSession;

namespace SafetyGateSupport.WindowsValidation;

// Runs the real WPF windows and Windows image codecs. Every DataService uses its own
// temporary settings directory and shared root; the developer's settings and Q: are never used.
internal static class Program
{
    private static string _runRoot = "";
    private static int _fixtureNumber;
    private static int _exitCode = 2;
    private static readonly List<Window> OpenWindows = [];

    [STAThread]
    private static int Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("REFUSED: WPF validation must run on Windows.");
            return 2;
        }

        _runRoot = Directory.CreateTempSubdirectory("safetygate-windows-validation-").FullName;
        // A blocked dispatcher (for example, an unexpected modal dialog) must fail CI,
        // rather than leave an unattended runner waiting indefinitely.
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("FAIL: Windows validation exceeded its three-minute deadline.");
            Environment.Exit(1);
        }, null, TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan);

        try
        {
            var app = new App();
            app.InitializeComponent();
            // WPF rejects setting StartupUri to null and queues startup even without
            // Application.Run. Cancel that navigation through its public event so
            // loading the real styles cannot create a window using the user's settings.
            app.Navigating += (_, navigation) => navigation.Cancel = true;
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            _ = RunChecksAsync();
            Dispatcher.Run();
            return _exitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SETUP FAILED: {exception}");
            ReportWorkflowError("Windows validation setup", exception);
            return 2;
        }
        finally
        {
            if (_exitCode == 0)
                Directory.Delete(_runRoot, recursive: true);
            else
                Console.Error.WriteLine($"Failure evidence retained at {_runRoot}");
        }
    }

    private static async Task RunChecksAsync()
    {
        (string Name, Func<Task> Check)[] checks =
        [
            ("Landscape and portrait photos decode within the preview pixel limit", BoundedPhotoDecoding),
            ("Small photos retain their dimensions and can be read from a worker thread", SmallFrozenPhoto),
            ("Decoded photos release the original file and reload replacement pixels", PhotoReplacement),
            ("Missing and corrupt photos return distinct nonfatal results", PhotoFailures),
            ("Pre-canceled photo requests cannot return a photo or a missing-file result", PhotoCancellation),
            ("Main window loads the real inventory after construction and searches it", MainWindowStartup),
            ("An invalid resource folder cannot discard indexed shared notes", NotesIndexSurvivesInvalidResource),
            ("Main window rapid selection keeps the final photo and clears deselection", MainWindowPhotoSelection),
            ("Troubleshooting picker rapid selection and missing-photo states stay correct", PickerPhotoSelection),
            ("Setup preview rapid selection and deselection stay correct", AdminPhotoSelection),
            ("Guided troubleshooting choices, back, and restart still work", TroubleshootingNavigation),
            ("Integrated column tool requires explicit assigned file selection and keeps observation state accurate", IntegratedColumnWindow),
            ("Unit light resource exposes the built-in USB action", IntegratedLightResource),
            ("Support library loads imported documentation on demand and filters filenames", SupportLibraryWindowStartup),
            ("Siemens USB check window renders with read-only controls and idle connection", SiemensWindowStartup),
            ("Siemens USB check saves an exact backup using only the two permitted GETs", SiemensBackupSession),
            ("Siemens USB check reads the backup after numeric/chunked identity with firstConfig false", SiemensFirstConfigFalse),
            ("Siemens USB check refuses a different model before requesting configuration", SiemensModelMismatch),
            ("Siemens USB check preserves an HTTP error without retries or reset requests", SiemensHttpFailure),
            ("A canceled Siemens USB check saves its report without sending requests", SiemensCancellation)
        ];

        var passed = 0;
        try
        {
            foreach (var (name, check) in checks)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    await check().WaitAsync(TimeSpan.FromSeconds(25));
                    Console.WriteLine($"PASS {name} ({watch.ElapsedMilliseconds} ms)");
                    passed++;
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"FAIL {name}: {exception}");
                    ReportWorkflowError(name, exception);
                }
                finally
                {
                    foreach (var window in OpenWindows.ToArray()) window.Close();
                    OpenWindows.Clear();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                }
            }
            Console.WriteLine($"Windows validation: {passed}/{checks.Length} passed; {checks.Length - passed} failed.");
            _exitCode = passed == checks.Length ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"VALIDATION FAILED: {exception}");
            ReportWorkflowError("Windows validation runner", exception);
            _exitCode = 1;
        }
        finally
        {
            Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }
    }

    private static async Task BoundedPhotoDecoding()
    {
        var root = NewDirectory();
        foreach (var (width, height) in new[] { (3000, 1500), (1500, 3000) })
        {
            var path = Path.Combine(root, $"{width}x{height}.png");
            WriteImage(path, width, height, Colors.Red);
            var result = await IdentificationPhotoLoader.LoadAsync(path);
            Require(result.Status == IdentificationPhotoStatus.Loaded && result.Source is not null, "Large photo did not load.");
            var bitmap = result.Source!;
            Require(bitmap.IsFrozen, "Decoded photo must be frozen before it crosses threads.");
            Equal(IdentificationPhotoLoader.MaxPixelDimension, Math.Max(bitmap.PixelWidth, bitmap.PixelHeight), "Decoded long edge");
            Require(Math.Abs((double)bitmap.PixelWidth / bitmap.PixelHeight - (double)width / height) < 0.01, "Downsampling changed the aspect ratio.");
        }
    }

    private static async Task SmallFrozenPhoto()
    {
        var path = Path.Combine(NewDirectory(), "small.png");
        WriteImage(path, 17, 23, Colors.Blue);
        var result = await IdentificationPhotoLoader.LoadAsync(path);
        Require(result.Status == IdentificationPhotoStatus.Loaded && result.Source is { IsFrozen: true }, "Small photo should load as a frozen bitmap.");
        Equal(17, result.Source!.PixelWidth, "Small photo width (no upscale)");
        Equal(23, result.Source.PixelHeight, "Small photo height (no upscale)");
        await Task.Run(() => AssertColor(result.Source, Colors.Blue));
    }

    private static async Task PhotoReplacement()
    {
        var root = NewDirectory();
        var path = Path.Combine(root, "replace.png");
        WriteImage(path, 31, 19, Colors.Red);
        var first = await IdentificationPhotoLoader.LoadAsync(path);
        Require(first.Source is not null, "Original photo did not load.");
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Require(exclusive.Length > 0, "Original photo could not be opened exclusively after decoding.");
        File.Move(path, Path.Combine(root, "old.png"));
        WriteImage(path, 31, 19, Colors.Blue);
        var replacement = await IdentificationPhotoLoader.LoadAsync(path);
        Require(replacement.Source is not null, "Replacement photo did not load.");
        AssertColor(first.Source!, Colors.Red);
        AssertColor(replacement.Source!, Colors.Blue);
        File.Delete(path);
        AssertColor(replacement.Source!, Colors.Blue);
    }

    private static async Task PhotoFailures()
    {
        var root = NewDirectory();
        var missing = await IdentificationPhotoLoader.LoadAsync(Path.Combine(root, "missing.png"));
        Require(missing.Status == IdentificationPhotoStatus.Missing && missing.Source is null, "Missing photo should return Missing without a bitmap.");
        var corruptPath = Path.Combine(root, "corrupt.png");
        File.WriteAllText(corruptPath, "This is deliberately not an image.");
        var corrupt = await IdentificationPhotoLoader.LoadAsync(corruptPath);
        Require(corrupt.Status == IdentificationPhotoStatus.Unreadable && corrupt.Source is null, "Corrupt photo should return Unreadable without a bitmap.");
    }

    private static async Task PhotoCancellation()
    {
        var path = Path.Combine(NewDirectory(), "cancel.png");
        WriteImage(path, 3000, 1500, Colors.Red);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await ExpectCancellation(IdentificationPhotoLoader.LoadAsync(path, canceled.Token));

        await ExpectCancellation(IdentificationPhotoLoader.LoadAsync(path + ".missing", canceled.Token));
    }

    private static async Task MainWindowStartup()
    {
        Require(Application.Current.Windows.Count == 0, "Application startup unexpectedly opened a window outside the isolated fixtures.");
        var fixture = NewFixture();
        var categoryCount = fixture.Data.LoadCategories().Count;
        var watch = Stopwatch.StartNew();
        var window = Track(new MainWindow(fixture.Data));
        Equal(0, Control<Panel>(window, "HomeCategoryPanel").Children.Count, "Constructor must not synchronously load shared inventory");
        var rendered = false;
        window.ContentRendered += (_, _) => rendered = true;
        ShowOffscreen(window);
        var heartbeat = false;
        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => heartbeat = true));
        await WaitUntil(() => rendered && heartbeat && Control<Panel>(window, "HomeCategoryPanel").Children.Count == categoryCount && window.IsEnabled,
            "The window did not render and finish asynchronous inventory startup.");
        Console.WriteLine($"INFO Main window rendered, dispatched input, and loaded seed inventory in {watch.ElapsedMilliseconds} ms.");
        Require(Control<TextBlock>(window, "ConnectionText").Text.Contains(fixture.Data.SharedRoot, StringComparison.OrdinalIgnoreCase), "Connection status does not identify the isolated root.");
        Control<TextBox>(window, "GlobalSearchBox").Text = fixture.Gate.AssetNumber;
        var results = Control<ListBox>(window, "GlobalSearchResults");
        await WaitUntil(() => results.Items.Count > 0, "Searching a seeded asset number returned no results.");
        Require(results.Items.Cast<GlobalSearchResult>().Any(result => result.Kind == "System" && result.Title.Contains(fixture.Gate.AssetNumber, StringComparison.OrdinalIgnoreCase)), "Search omitted the seeded gate/unit.");
    }

    private static async Task IntegratedColumnWindow()
    {
        var root = NewDirectory();
        string first = Path.Combine(root, "Unit 1.signal"), second = Path.Combine(root, "Unit 2.signal");
        File.WriteAllBytes(first, SyntheticColumnArchive()); File.Copy(first, second);
        var window = Track(new global::SiemensUsbCheck.MainWindow("Asset validation / Unit 2", new[] { first, second }));
        ShowOffscreen(window);
        await WaitUntil(() => Control<Button>(window, "RefreshButton").IsEnabled, "Integrated column detection did not settle.");
        Require(Control<TextBlock>(window, "ContextText").Text.Contains("Unit 2"), "Unit identity missing from tool.");
        var assigned = Control<ComboBox>(window, "AssignedFilesCombo");
        Require(assigned.Items.Count == 2 && assigned.SelectedIndex == -1, "Assigned configuration was selected automatically.");
        Require(!Control<Button>(window, "UploadButton").IsEnabled, "Upload enabled before an explicit file choice.");
        assigned.SelectedIndex = 1;
        await WaitUntil(() => Control<TextBlock>(window, "SelectedSignalText").Text.Contains(second), "Explicit unit configuration did not load.");
        Require(Control<TextBox>(window, "ResultText").Text.Contains("not been checked"), "File choice incorrectly reported a device configuration match.");
        Control<ComboBox>(window, "StateCombo").SelectedIndex = 7;
        Require(Control<TextBox>(window, "PreviewText").Text.Contains("RGB 255, 0, 0"), "Expected output did not use the selected state's stored values.");
        Control<ComboBox>(window, "ObservationCombo").SelectedIndex = 1;
        Require(Control<TextBlock>(window, "ObservationText").Text.Contains("this input state only"), "Observation implied complete functional validation.");
        Control<ComboBox>(window, "StateCombo").SelectedIndex = 0;
        Equal(0, Control<ComboBox>(window, "ObservationCombo").SelectedIndex, "Changing the state must clear an obsolete observation");
        Require(!Control<Button>(window, "UploadButton").IsEnabled, "A valid file alone enabled upload without a port.");
    }

    private static Task IntegratedLightResource()
    {
        var fixture = NewFixture();
        var resource = fixture.Data.ResourcesFor(fixture.Gate, fixture.BlueUnit).First(SignalColumnService.IsLightResource);
        var window = Track(new ResourceWindow(fixture.Data, fixture.Gate, fixture.BlueUnit, resource));
        Require(Control<Button>(window, "SignalColumnButton").Visibility == Visibility.Visible, "Unit light resource has no integrated USB action.");
        return Task.CompletedTask;
    }

    private static async Task SupportLibraryWindowStartup()
    {
        var fixture = NewFixture();
        string manual = Path.Combine(NewDirectory(), "scanner-manual.pdf"); File.WriteAllText(manual, "not opened by this test");
        await Task.Run(() => SupportLibraryService.Import(SupportLibraryService.Root(fixture.Data), manual));
        var window = Track(new SupportLibraryWindow(fixture.Data, fixture.Gate, fixture.BlueUnit));
        ShowOffscreen(window);
        await WaitUntil(() => Descendants<ListBox>(window).Any(l => l.Items.Count == 1), "Imported manual did not appear in library.");
        var list = Descendants<ListBox>(window).Single();
        var search = Descendants<TextBox>(window).Single(); search.Text = "unmatched filename";
        Equal(0, list.Items.Count, "Library filename filter");
        search.Text = "scanner"; Equal(1, list.Items.Count, "Library matching filename filter");
        Require(Descendants<ComboBox>(window).Single().SelectedItem is SafetySystem unit && unit.Id == fixture.BlueUnit.Id, "Library lost the selected unit context.");
    }

    private static async Task SiemensWindowStartup()
    {
        var window = Track(new global::SiemensUsbCheck.MainWindow());
        Require(!Control<ComboBox>(window, "DeviceCombo").IsEditable, "The USB selector must not accept arbitrary COM ports.");
        Require(!Control<Button>(window, "ReadButton").IsEnabled && !Control<Button>(window, "CancelButton").IsEnabled,
            "The tool must start idle, without opening a column.");
        Require(!Control<Button>(window, "UploadButton").IsEnabled, "Upload must require a selected device and validated file.");
        Require(window.Title.Contains("v0.3.0", StringComparison.Ordinal), "The upload tool version was not updated.");
        Require(Control<TextBox>(window, "ResultText").IsReadOnly, "The report preview must not be an editable command field.");
        var rendered = false;
        window.ContentRendered += (_, _) => rendered = true;
        ShowOffscreen(window);
        await WaitUntil(() => rendered && Control<Button>(window, "RefreshButton").IsEnabled,
            "The USB check window did not render and finish passive device discovery.");
        Require(!Control<Button>(window, "CancelButton").IsEnabled, "Passive discovery unexpectedly started a USB session.");
        Require(Control<TextBlock>(window, "StatusText").Text.Contains("does not open USB", StringComparison.Ordinal),
            "The idle tool should explain that detection has not opened USB.");
        var actions = Descendants<Button>(window).Select(button => button.Name).Order().ToArray();
        Require(actions.SequenceEqual(new[] { "BrowseButton", "CancelButton", "ReadButton", "RefreshButton", "UploadButton" }),
            "The configuration window exposed an unexpected action.");
        Control<ComboBox>(window, "DeviceCombo").ItemsSource = new[] { TestColumnPort() };
        Control<ComboBox>(window, "DeviceCombo").SelectedIndex = 0;
        Require(!Control<Button>(window, "UploadButton").IsEnabled, "Selecting a port without a valid file enabled upload.");
    }

    private static Task SiemensBackupSession() => SiemensBackup(firstConfig: true);
    private static Task SiemensFirstConfigFalse() => SiemensBackup(firstConfig: false, numericChunkedInfo: true);

    private static Task SiemensBackup(bool firstConfig, bool numericChunkedInfo = false)
    {
        var info = numericChunkedInfo ? JsonSerializer.SerializeToUtf8Bytes(new
        {
            articleNumber = 65710155, firmwareVersion = "synthetic-test", firstConfig,
            hardware = new { hardwareVariant = 21, hardwareRevision = 1, mac = "synthetic-device" }
        }) : SyntheticColumnInfo(firstConfig: firstConfig);
        var backup = SyntheticColumnArchive();
        using var stream = new ColumnScriptStream(numericChunkedInfo,
            (SiemensWire.GetInfoRequest(), info, 200),
            (SiemensWire.GetConfigRequest(), backup, 200));
        var reportPath = Path.Combine(NewDirectory(), "usb-report.zip");
        var result = UsbSession.RunOnStream(stream, TestColumnPort(), reportPath);
        Require(result.Success, $"The synthetic backup failed: {result.Summary}");
        Require(stream.Completed, "The session did not consume both expected responses.");
        Require(stream.SentBytes.SequenceEqual(SiemensWire.GetInfoRequest().Concat(SiemensWire.GetConfigRequest())),
            "The backup session sent something besides the two exact GET requests.");
        using var report = ZipFile.OpenRead(reportPath);
        Require(report.Entries.Select(entry => entry.FullName).Order().SequenceEqual(
            new[] { "column-backup.signal", "device-info.json", "report.json" }), "The successful report has unexpected files.");
        Require(ReportBytes(report, "column-backup.signal").SequenceEqual(backup), "The saved configuration differs from the original response bytes.");
        Require(ReportBytes(report, "device-info.json").SequenceEqual(info), "The saved identity differs from the original response bytes.");
        using var json = JsonDocument.Parse(ReportBytes(report, "report.json"));
        var root = json.RootElement;
        Require(root.GetProperty("success").GetBoolean(), "The report incorrectly marks a successful backup as failed.");
        Require(root.GetProperty("requests").EnumerateArray().Select(item => item.GetString())
            .SequenceEqual(new[] { "GET /api/v1/info", "GET /api/v1/config" }), "The report request list differs from the wire exchanges.");
        Equal(2, root.GetProperty("responses").GetArrayLength(), "Recorded HTTP responses");
        Require(root.GetProperty("responses").EnumerateArray().All(item => item.GetProperty("StatusCode").GetInt32() == 200),
            "The report lost a successful response status.");
        var device = root.GetProperty("device");
        Equal(firstConfig, device.GetProperty("FirstConfig").GetBoolean(), "Preserved firstConfig flag");
        Equal(numericChunkedInfo ? "65710155" : "657.101.55", device.GetProperty("ArticleNumber").GetString(), "Preserved article identifier");
        Equal("8WD4613-5JH37", device.GetProperty("Model").GetString(), "Normalized column model");
        if (numericChunkedInfo)
        {
            var headers = root.GetProperty("responses")[0].GetProperty("Headers");
            Equal("chunked", headers.GetProperty("Transfer-Encoding").GetString(), "Preserved observed response framing");
            Equal("text/html", headers.GetProperty("Content-Type").GetString(), "Preserved observed response content type");
        }
        return Task.CompletedTask;
    }

    private static Task SiemensModelMismatch()
    {
        var info = SyntheticColumnInfo(article: "8WD4615-5JH37", variant: 51);
        using var stream = new ColumnScriptStream((SiemensWire.GetInfoRequest(), info, 200));
        var reportPath = Path.Combine(NewDirectory(), "usb-model-mismatch.zip");
        var result = UsbSession.RunOnStream(stream, TestColumnPort(), reportPath);
        Require(!result.Success, "A different column model was incorrectly accepted.");
        Require(stream.Completed && stream.SentBytes.SequenceEqual(SiemensWire.GetInfoRequest()),
            "A mismatched identity must not trigger a configuration request.");
        using var report = ZipFile.OpenRead(reportPath);
        Require(report.GetEntry("column-backup.signal") is null && report.GetEntry("config-response.bin") is null,
            "A mismatched column unexpectedly produced a configuration backup.");
        Require(ReportBytes(report, "device-info.json").SequenceEqual(info), "The mismatch report lost the device identity.");
        Require(ReportBytes(report, "received-prefix.bin").Length > 0, "The mismatch report lost the received diagnostic bytes.");
        using var json = JsonDocument.Parse(ReportBytes(report, "report.json"));
        Require(!json.RootElement.GetProperty("success").GetBoolean(), "The mismatch report incorrectly indicates success.");
        Require(json.RootElement.GetProperty("error").GetString()!.Contains("8WD4615-5JH37", StringComparison.Ordinal),
            "The mismatch report does not identify the incompatible model.");
        return Task.CompletedTask;
    }

    private static Task SiemensHttpFailure()
    {
        var failure = Encoding.UTF8.GetBytes("{\"error\":\"synthetic column busy\"}");
        using var stream = new ColumnScriptStream(
            (SiemensWire.GetInfoRequest(), SyntheticColumnInfo(), 200),
            (SiemensWire.GetConfigRequest(), failure, 503));
        var reportPath = Path.Combine(NewDirectory(), "usb-http-error.zip");
        var result = UsbSession.RunOnStream(stream, TestColumnPort(), reportPath);
        Require(!result.Success && stream.Completed, "An HTTP error was incorrectly accepted or its response was left unread.");
        Require(stream.SentBytes.SequenceEqual(SiemensWire.GetInfoRequest().Concat(SiemensWire.GetConfigRequest())),
            "An HTTP failure triggered an extra request, retry, or reset.");
        using var report = ZipFile.OpenRead(reportPath);
        Require(report.GetEntry("column-backup.signal") is null, "An HTTP error response was labeled as a valid configuration backup.");
        Require(ReportBytes(report, "config-response.bin").SequenceEqual(failure), "The report lost or altered the full HTTP error body.");
        Require(ReportBytes(report, "received-prefix.bin").Length > 0, "The HTTP failure report lost the raw response prefix.");
        using var json = JsonDocument.Parse(ReportBytes(report, "report.json"));
        var responses = json.RootElement.GetProperty("responses");
        Equal(503, responses[1].GetProperty("StatusCode").GetInt32(), "Preserved HTTP failure status");
        Require(json.RootElement.GetProperty("error").GetString()!.Contains("503", StringComparison.Ordinal),
            "The failure report does not explain the HTTP status.");
        return Task.CompletedTask;
    }

    private static Task SiemensCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new ColumnScriptStream();
        var reportPath = Path.Combine(NewDirectory(), "usb-canceled.zip");
        var result = UsbSession.RunOnStream(stream, TestColumnPort(), reportPath, cancellation.Token);
        Require(!result.Success && stream.SentBytes.Length == 0, "A canceled check sent a USB request or incorrectly succeeded.");
        using var report = ZipFile.OpenRead(reportPath);
        Require(report.Entries.Select(entry => entry.FullName).SequenceEqual(new[] { "report.json" }),
            "A pre-canceled session should only save the diagnostic summary.");
        using var json = JsonDocument.Parse(ReportBytes(report, "report.json"));
        Equal(0, json.RootElement.GetProperty("requests").GetArrayLength(), "Canceled request count");
        Require(json.RootElement.GetProperty("error").GetString()!.Contains("cancel", StringComparison.OrdinalIgnoreCase),
            "The diagnostic report does not explain cancellation.");
        return Task.CompletedTask;
    }

    private static UsbColumnPort TestColumnPort() => new("COM99", @"USB\VID_303A&PID_80F7\CI-FAKE", "Synthetic column (COM99)");

    private static byte[] SyntheticColumnInfo(string article = "657.101.55", int variant = 21, bool firstConfig = true) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            articleNumber = article, firmwareVersion = "synthetic-test", firstConfig,
            hardware = new { hardwareVariant = variant, hardwareRevision = 1, mac = "synthetic-device" }
        });

    private static byte[] ReportBytes(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidOperationException($"Diagnostic report is missing {name}.");
        using var source = entry.Open();
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static async Task MainWindowPhotoSelection()
    {
        var fixture = NewFixture();
        var window = Track(new MainWindow(fixture.Data));
        ShowOffscreen(window);
        await WaitUntil(() => Control<Panel>(window, "HomeCategoryPanel").Children.Count > 0 && window.IsEnabled, "Main window inventory did not load.");
        Click(Control<Panel>(window, "HomeCategoryPanel").Children.OfType<Button>().Single(button => Equals(button.Tag, fixture.Gate.Category)));
        var assets = Control<ListBox>(window, "AssetList");
        assets.SelectedItem = assets.Items.Cast<SafetyGate>().Single(gate => gate.Id == fixture.Gate.Id);
        var units = Control<ListBox>(window, "UnitList");
        RapidSelect(units, fixture.RedUnit.Id, fixture.BlueUnit.Id);
        var photo = Control<Image>(window, "GateImage");
        await WaitUntil(() => HasColor(photo.Source, Colors.Blue), "Main window did not keep the final selected unit's blue photo.");
        Require(Control<TextBlock>(window, "UnitTitleText").Text.Contains(fixture.BlueUnit.UnitDisplay, StringComparison.Ordinal), "Unit title does not match the final selection.");
        Require(Control<Panel>(window, "SystemResourcePanel").Children.Count > 0, "Selected unit resource buttons were not populated.");
        units.SelectedIndex = 0;
        units.SelectedItem = null;
        await DispatcherSettled();
        Require(photo.Source is null && Control<Panel>(window, "UnitDetailPanel").Visibility == Visibility.Collapsed, "Deselection allowed an obsolete photo to repopulate cleared details.");
    }

    private static async Task NotesIndexSurvivesInvalidResource()
    {
        var fixture = NewFixture();
        var phrase = "notes-regression-" + Guid.NewGuid().ToString("N");
        fixture.Data.SaveSystemNotes(fixture.BlueUnit, phrase);
        Equal(phrase, fixture.Data.LoadSystemNotes(fixture.Data.LoadSystems().Single(unit => unit.Id == fixture.BlueUnit.Id)), "Persisted regression note");
        var resources = fixture.Data.LoadResources();
        resources.Add(new ResourceDefinition
        {
            Id = "invalid-folder-regression", GateId = fixture.Gate.Id,
            Label = "Deliberately invalid managed resource", Kind = "ManagedFiles",
            RelativeFolder = "invalid\0folder", AppliesToUnitIds = [fixture.BlueUnit.Id], IsVisible = true
        });
        fixture.Data.SaveResources(resources);

        // Establish that this fixture reaches the file-enumeration failure. Without
        // the local recovery in RefreshSearchIndexAsync, the completed notes entries
        // are discarded and _searchIndexReady remains false.
        var invalidPathObserved = false;
        try { _ = fixture.Data.GetCurrentSystemFiles().ToList(); }
        catch (ArgumentException) { invalidPathObserved = true; }
        Require(invalidPathObserved, "The malformed managed-resource path did not trigger the intended enumeration failure.");

        var window = Track(new MainWindow(fixture.Data));
        var readyField = typeof(MainWindow).GetField("_searchIndexReady", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow search readiness field was not found.");
        ShowOffscreen(window);
        await WaitUntil(() => window.IsEnabled && readyField.GetValue(window) is true,
            "The background index discarded inventory and notes after a managed-resource enumeration failure.");
        Control<TextBox>(window, "GlobalSearchBox").Text = phrase;
        var results = Control<ListBox>(window, "GlobalSearchResults");
        await WaitUntil(() => results.Items.Cast<GlobalSearchResult>().Any(result => result.Kind == "System" && result.SystemId == fixture.BlueUnit.Id && result.MatchSource == "SharedNotes"),
            "The note saved before a malformed resource folder was not searchable for its unit.");
    }

    private static async Task PickerPhotoSelection()
    {
        var fixture = NewFixture();
        var window = Track(new TroubleshootPickerWindow(fixture.Data, fixture.Data.LoadGates(), fixture.Data.LoadSystems(), fixture.Data.LoadCategories()));
        ShowOffscreen(window);
        Control<ComboBox>(window, "AreaBox").SelectedItem = fixture.Gate.Category;
        var assets = Control<ListBox>(window, "AssetList");
        assets.SelectedItem = assets.Items.Cast<SafetyGate>().Single(gate => gate.Id == fixture.Gate.Id);
        var units = Control<ListBox>(window, "UnitList");
        RapidSelect(units, fixture.RedUnit.Id, fixture.BlueUnit.Id);
        var photo = Control<Image>(window, "PreviewImage");
        Require(photo.Source is not null || !Control<Button>(window, "StartButton").IsEnabled, "Troubleshooting must wait for the selected preview to load or display an error.");
        await WaitUntil(() => HasColor(photo.Source, Colors.Blue), "Picker did not keep the final selected unit's blue photo.");
        Require(Control<Button>(window, "StartButton").IsEnabled, "Picker should allow troubleshooting for the selected unit.");
        Equal(Visibility.Collapsed, Control<TextBlock>(window, "PreviewPlaceholderText").Visibility, "Loaded-photo placeholder visibility");

        var missingUnit = units.Items.Cast<SafetySystem>().Single(unit => unit.Id == fixture.RedUnit.Id);
        missingUnit.PrimaryPhoto = "missing-identification.png";
        units.SelectedItem = missingUnit;
        await WaitUntil(() => Control<TextBlock>(window, "PreviewPlaceholderText").Text.Contains("could not be found", StringComparison.OrdinalIgnoreCase), "Picker did not display its missing-photo message.");
        Require(photo.Source is null && Control<Button>(window, "StartButton").IsEnabled, "A missing photo should clear the image and leave the selected unit usable.");

        units.SelectedItem = units.Items.Cast<SafetySystem>().Single(unit => unit.Id == fixture.BlueUnit.Id);
        assets.SelectedItem = null;
        await DispatcherSettled();
        Require(photo.Source is null && !Control<Button>(window, "StartButton").IsEnabled, "Clearing the gate allowed an obsolete picker preview or start button.");
    }

    private static async Task AdminPhotoSelection()
    {
        var fixture = NewFixture();
        var window = Track(new AdminWindow(fixture.Data));
        ShowOffscreen(window);
        Control<ComboBox>(window, "GateAreaFilterBox").SelectedItem = fixture.Gate.Category;
        var gates = Control<ListBox>(window, "GateList");
        gates.SelectedItem = gates.Items.Cast<SafetyGate>().Single(gate => gate.Id == fixture.Gate.Id);
        var units = Control<ListBox>(window, "UnitListAdmin");
        RapidSelect(units, fixture.RedUnit.Id, fixture.BlueUnit.Id);
        var photo = Control<Image>(window, "UnitPhotoPreview");
        await WaitUntil(() => HasColor(photo.Source, Colors.Blue), "Setup preview did not keep the final selected unit's blue photo.");
        Equal(fixture.BlueUnit.UnitNumber, Control<TextBox>(window, "UnitNumberBox").Text, "Setup selected unit number");
        units.SelectedItem = units.Items.Cast<SafetySystem>().Single(unit => unit.Id == fixture.RedUnit.Id);
        units.SelectedItem = null;
        await DispatcherSettled();
        Require(photo.Source is null, "Deselection allowed an obsolete setup preview to repopulate.");
    }

    private static async Task TroubleshootingNavigation()
    {
        var fixture = NewFixture();
        var flow = new TroubleshootingFlow
        {
            Id = "windows-validation", Title = "Validation flow", StartNodeId = "start",
            Nodes =
            [
                new() { Id = "start", Prompt = "First step", Choices = [new() { Text = "Continue", NextNodeId = "end" }] },
                new() { Id = "end", Prompt = "Final step", Body = "Validation completed.", IsTerminal = true }
            ]
        };
        var window = Track(new TroubleshootWindow(fixture.Data, fixture.Gate, fixture.BlueUnit, fixture.Data.LoadResources(), [flow]));
        ShowOffscreen(window);
        Control<ListBox>(window, "FlowList").SelectedIndex = 0;
        Equal("First step", Control<TextBlock>(window, "StepPromptText").Text, "Initial troubleshooting step");
        Click(Control<Panel>(window, "ChoicePanel").Children.OfType<Button>().Single());
        Equal("Final step", Control<TextBlock>(window, "StepPromptText").Text, "Choice navigation");
        Click(Control<Button>(window, "BackButton"));
        Equal("First step", Control<TextBlock>(window, "StepPromptText").Text, "Back navigation");
        Click(Control<Panel>(window, "ChoicePanel").Children.OfType<Button>().Single());
        // The restart button is deliberately identified by its visible label: it has no x:Name.
        var restart = Descendants<Button>(window).Single(button => Equals(button.Content, "Restart"));
        Click(restart);
        Equal("First step", Control<TextBlock>(window, "StepPromptText").Text, "Restart navigation");
        Require(!Control<Button>(window, "BackButton").IsEnabled, "Restart should clear navigation history.");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static Fixture NewFixture()
    {
        var root = NewDirectory();
        var data = new DataService(initializeSharedDefaults: false, localSettingsDirectory: Path.Combine(root, "settings"));
        data.SetSharedRoot(Path.Combine(root, "shared"));
        Require(!data.IsReady, "Fixture root unexpectedly contained shared data.");
        data.InitializeSharedData();
        var systems = data.LoadSystems();
        var group = systems.GroupBy(unit => unit.GateId).First(units => units.Count() >= 2);
        var selected = group.Take(2).ToArray();
        var gate = data.LoadGates().Single(item => item.Id == group.Key);
        Require(gate.AssetNumber.Length >= 2, "Fixture must have a searchable asset number.");
        selected[0].PrimaryPhoto = "validation-red.png";
        selected[1].PrimaryPhoto = "validation-blue.png";
        WriteImage(data.Resolve(selected[0].PrimaryPhoto), 3000, 1500, Colors.Red);
        WriteImage(data.Resolve(selected[1].PrimaryPhoto), 17, 23, Colors.Blue);
        data.SaveSystems(systems);
        return new(data, gate, selected[0], selected[1]);
    }

    private static string NewDirectory() => Directory.CreateDirectory(Path.Combine(_runRoot, $"fixture-{++_fixtureNumber:D2}")).FullName;

    private static T Track<T>(T window) where T : Window
    {
        OpenWindows.Add(window);
        return window;
    }

    private static void ShowOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
    }

    private static void RapidSelect(ListBox units, string firstId, string finalId)
    {
        var first = units.Items.Cast<SafetySystem>().Single(unit => unit.Id == firstId);
        var final = units.Items.Cast<SafetySystem>().Single(unit => unit.Id == finalId);
        for (var i = 0; i < 20; i++)
        {
            units.SelectedItem = first;
            units.SelectedItem = final;
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string failure)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(failure);
            await Task.Delay(20);
        }
    }

    private static async Task DispatcherSettled()
    {
        await Task.Delay(250);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task ExpectCancellation(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("A canceled load completed successfully.");
    }

    private static void WriteImage(string path, int width, int height, Color color)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = color.A;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static bool HasColor(ImageSource? image, Color color)
    {
        if (image is not BitmapSource source) return false;
        var converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[4];
        converted.CopyPixels(new Int32Rect(0, 0, 1, 1), pixels, 4, 0);
        return pixels[0] == color.B && pixels[1] == color.G && pixels[2] == color.R && pixels[3] == color.A;
    }

    private static void AssertColor(BitmapSource image, Color color) => Require(HasColor(image, color), $"Photo pixels did not match {color}.");
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Control<T>(FrameworkElement window, string name) where T : FrameworkElement => window.FindName(name) as T ?? throw new InvalidOperationException($"Missing {typeof(T).Name} named {name}.");

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void ReportWorkflowError(string name, Exception exception)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") return;
        var message = $"{name}: {exception}".Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
        Console.WriteLine($"::error title=Windows validation::{message}");
    }

    private static byte[] SyntheticColumnArchive()
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJson("configurator.conf", new
            {
                version = 5,
                device = new { Variant = 21, SegmentCount = 9, HasSound = true },
                mode = 4,
                stages = Array.Empty<object>(),
                states = Array.Empty<object>()
            });
            for (var state = 0; state < 64; state++)
                WriteJson($"state_config_{state}.json", new
                {
                    Segments = Enumerable.Range(0, 9).Select(segment => new
                    {
                        r = state == 7 && segment == 0 ? 255 : 0,
                        g = 0,
                        b = 0,
                        brightness = 255,
                        lightEffect = 10
                    }).ToArray(),
                    Sound = new
                    {
                        mode = 0, volume = 0, frequency01 = 0, frequency02 = 0,
                        frequency03 = 0, repeatCount = 0, repeatPause = 0,
                        cyclePause = 0, holdTime = 0
                    }
                });

            void WriteJson(string name, object value)
            {
                using var output = archive.CreateEntry(name).Open();
                JsonSerializer.Serialize(output, value);
            }
        }
        return bytes.ToArray();
    }

    // This stream acts as a column and exposes a reply only after receiving the
    // exact allowed GET. Unexpected requests, writes before replies are consumed,
    // and retries fail immediately; no USB hardware is accessed by these checks.
    private sealed class ColumnScriptStream : Stream
    {
        private readonly (byte[] Request, byte[] Body, int Status)[] _exchanges;
        private readonly byte[][] _responses;
        private readonly List<byte> _sent = [];
        private int _exchange;
        private int _requestOffset;
        private int _responseOffset;
        private bool _replyAvailable;

        public ColumnScriptStream(params (byte[] Request, byte[] Body, int Status)[] exchanges)
            : this(false, exchanges) { }

        public ColumnScriptStream(bool chunkedFirstResponse, params (byte[] Request, byte[] Body, int Status)[] exchanges)
        {
            _exchanges = exchanges;
            _responses = exchanges.Select((exchange, index) => chunkedFirstResponse && index == 0
                ? Encoding.ASCII.GetBytes($"HTTP/1.1 {exchange.Status} OK\r\nContent-Type: text/html\r\nTransfer-Encoding: chunked\r\n\r\n{exchange.Body.Length:x}\r\n")
                    .Concat(exchange.Body).Concat(Encoding.ASCII.GetBytes("\r\n0\r\n\r\n")).ToArray()
                : Encoding.ASCII.GetBytes($"HTTP/1.0 {exchange.Status} Test\r\nContent-Length: {exchange.Body.Length}\r\n\r\n")
                    .Concat(exchange.Body).ToArray()).ToArray();
        }

        public byte[] SentBytes => _sent.ToArray();
        public bool Completed => _exchange == _exchanges.Length;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            for (var index = 0; index < count; index++)
            {
                Require(_exchange < _exchanges.Length && !_replyAvailable, "The session sent an unexpected request, retry, or command before reading its reply.");
                byte value = buffer[offset + index];
                Require(value == _exchanges[_exchange].Request[_requestOffset], "The session sent bytes other than the exact permitted read-only request.");
                _sent.Add(value);
                if (++_requestOffset == _exchanges[_exchange].Request.Length)
                    _replyAvailable = true;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            Require(_replyAvailable && _exchange < _responses.Length, "The session attempted to read without sending the expected GET.");
            var response = _responses[_exchange];
            int copied = Math.Min(3, Math.Min(count, response.Length - _responseOffset));
            response.AsSpan(_responseOffset, copied).CopyTo(buffer.AsSpan(offset, copied));
            _responseOffset += copied;
            if (_responseOffset == response.Length)
            {
                _exchange++;
                _requestOffset = _responseOffset = 0;
                _replyAvailable = false;
            }
            return copied;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static void Equal<T>(T expected, T actual, string label) => Require(EqualityComparer<T>.Default.Equals(expected, actual), $"{label}: expected {expected}; got {actual}.");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record Fixture(DataService Data, SafetyGate Gate, SafetySystem RedUnit, SafetySystem BlueUnit);
}
