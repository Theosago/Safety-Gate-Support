using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

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
            app.StartupUri = null;
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
            ("Guided troubleshooting choices, back, and restart still work", TroubleshootingNavigation)
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

    private static void Equal<T>(T expected, T actual, string label) => Require(EqualityComparer<T>.Default.Equals(expected, actual), $"{label}: expected {expected}; got {actual}.");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record Fixture(DataService Data, SafetyGate Gate, SafetySystem RedUnit, SafetySystem BlueUnit);
}
