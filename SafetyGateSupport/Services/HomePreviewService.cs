using System.IO;
using System.IO.Compression;
using System.Text.Json;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

/// <summary>An isolated local profile with illustrative data; never loads normal settings.</summary>
public static class HomePreviewService
{
    public static bool IsRequested(IEnumerable<string> args, string? executablePath) =>
        args.Any(a => a.Equals("--preview", StringComparison.OrdinalIgnoreCase)) ||
        Path.GetFileNameWithoutExtension(executablePath ?? "").Equals("SafetyGateSupportPreview", StringComparison.OrdinalIgnoreCase);

    public static DataService Create(string? localDataRoot = null)
    {
        string root = Path.Combine(localDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SafetyGateSupport", "HomePreview");
        var data = new DataService(initializeSharedDefaults: false, localSettingsDirectory: Path.Combine(root, "Settings")) { IsHomePreview = true };
        // Reset only the preview profile's root, even if its settings were previously edited.
        data.Settings.SharedRoot = Path.Combine(root, "Data");
        data.SaveLocalSettings();
        return data;
    }

    public static void Initialize(DataService data)
    {
        if (!data.IsHomePreview) throw new InvalidOperationException("Sample data requires the home preview profile.");
        if (!data.IsReady) data.InitializeSharedData();
        var gates = data.LoadGates(); var units = data.LoadSystems(gates); var resources = data.LoadResources(); var categories = data.LoadCategories();
        var gate = gates.FirstOrDefault(g => g.Id == "home-preview-gate");
        if (gate is null)
        {
            gate = new SafetyGate { Id = "home-preview-gate", AssetNumber = "DEMO", Manufacturer = "Safe2Cross", Category = "Home Preview", Model = "Illustrative example", Location = "Local sample — not field equipment" };
            gates.Add(gate); data.SaveGates(gates);
            var content = data.LoadSharedContent();
            content.HomeSubtitle = "HOME PREVIEW • Local starter data and sample configuration. Open Home Preview → Asset DEMO / Unit 1 to explore the light tools. USB operations are disabled in this preview.";
            content.PartsIntro = "Home preview: the plant stockroom is unavailable. You can explore the parts interface and import local documentation.";
            content.StoreroomUrl = ""; data.SaveSharedContent(content);
        }
        if (!categories.Contains("Home Preview")) { categories.Insert(0, "Home Preview"); data.SaveCategories(categories); }
        if (!units.Any(u => u.Id == "home-preview-unit"))
        {
            units.Add(new SafetySystem { Id = "home-preview-unit", GateId = gate.Id, UnitNumber = "1", DisplayName = "Illustrative light configuration", SystemFolder = "HomePreview/SampleUnit", NotesFolder = "HomePreview/SampleUnit/Notes", ShowScannerConfiguration = false, ShowElectricalDrawings = false });
            data.SaveSystems(units);
        }
        var resource = resources.FirstOrDefault(r => r.Id == "home-preview-light");
        if (resource is null)
        {
            resource = new ResourceDefinition { Id = "home-preview-light", GateId = gate.Id, Label = "Light Configuration — SAMPLE", Kind = "ManagedFiles", RelativeFolder = "HomePreview/SampleUnit/LightConfig", AppliesToUnitIds = ["home-preview-unit"], LaunchAction = "Siemens", Description = "Invented preview configuration. State 1: red; state 2: amber; state 4: green; state 8: sound. Other states are off. This is not an approved site configuration.", SortOrder = 10 };
            resources.Add(resource); data.SaveResources(resources);
        }
        string folder = data.ResourceCurrentFolder(resource, gate); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "DEMO ONLY - not for equipment.signal");
        if (!File.Exists(path)) File.WriteAllBytes(path, SampleConfiguration());
    }

    public static byte[] SampleConfiguration()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            Write("configurator.conf", new { version = 5, device = new { Variant = 21, SegmentCount = 9, HasSound = true }, mode = 4, stages = Array.Empty<object>(), states = Array.Empty<object>(), previewOnly = true });
            for (int state = 0; state < 64; state++)
            {
                bool light = state is 1 or 2 or 4, sound = state == 8;
                Write($"state_config_{state}.json", new
                {
                    Segments = Enumerable.Range(0, 9).Select(_ => new { r = state is 1 or 2 ? 255 : 0, g = state == 2 ? 180 : state == 4 ? 255 : 0, b = 0, brightness = light ? 180 : 0, lightEffect = 10 }).ToArray(),
                    Sound = new { mode = sound ? 1 : 0, volume = sound ? 5 : 0, frequency01 = sound ? 1000 : 0, frequency02 = 0, frequency03 = 0, repeatCount = 0, repeatPause = 0, cyclePause = 0, holdTime = 0 }
                });
            }
            void Write(string name, object value) { using var entry = zip.CreateEntry(name).Open(); JsonSerializer.Serialize(entry, value); }
        }
        return memory.ToArray();
    }
}
