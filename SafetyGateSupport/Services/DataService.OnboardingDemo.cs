using System.IO;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

public partial class DataService
{
    public bool IsOnboardingDemo { get; init; }

    public static DataService CreateOnboardingDemo(string workspace, string themeMode = "Light")
    {
        const string area = "Training";
        var data = new DataService(initializeSharedDefaults: false, localSettingsDirectory: Path.Combine(workspace, "Settings"))
        {
            IsHomePreview = true,
            IsOnboardingDemo = true,
        };
        data.Settings.ThemeMode = themeMode;
        data.CreateBlankPlantLibrary(Path.Combine(workspace, "Library"), area);
        data.SaveSharedContent(new SharedContent
        {
            HomeSubtitle = "Open DEMO-01 from its photo. Under Manuals / documents, select the reference PDF and click Open. Then use Common Problems to find the missing-label example.",
            ManualsIntro = "Open the fictional reference PDF for DEMO-01.",
            PartsIntro = "The fictional example does not use live inventory.",
            StoreroomUrl = "",
        });

        var gate = new SafetyGate
        {
            Id = "demo-gate",
            AssetNumber = "DEMO-01",
            Category = area,
            Location = "Fictional test bench",
            Manufacturer = "Training example",
        };
        data.SaveGates([gate]);
        var unit = new SafetySystem
        {
            Id = "demo-unit",
            GateId = gate.Id,
            UnitNumber = "1",
            DisplayName = "Identification label example",
            ShowElectricalDrawings = false,
            ShowLightConfiguration = false,
            ShowScannerConfiguration = false,
            ShowPartsBom = false,
            ManualIds = [],
        };

        string sources = Path.Combine(workspace, "Sources");
        Directory.CreateDirectory(sources);
        data.SetIdentificationPhoto(unit, ExtractDemoFile("Identification.png", sources, "DEMO-01.png"));
        data.SynchronizeLegacyFields([gate], [unit]);
        data.SaveSystems([unit]);
        data.AddPilotPdf(unit.Id, ExtractDemoFile("DemoReference.pdf", sources, "DEMO-01 Reference.pdf"));
        data.SaveHelpTopic(new HelpItem
        {
            Section = "Common Problems",
            Title = "Missing identification label",
            Body = "Fictional example. Compare the missing-label picture with the DEMO-01 photo and reference PDF. Use the asset number to report which box needs a label.",
        }, ExtractDemoFile("MissingLabel.png", sources, "Missing label.png"));
        return data;
    }

    private static string ExtractDemoFile(string resource, string folder, string filename)
    {
        using var source = typeof(DataService).Assembly.GetManifestResourceStream("SafetyGateSupport.DemoAssets." + resource)
            ?? throw new InvalidDataException("A fictional example file is missing.");
        string destination = Path.Combine(folder, filename);
        using var target = File.Create(destination);
        source.CopyTo(target);
        return destination;
    }
}
