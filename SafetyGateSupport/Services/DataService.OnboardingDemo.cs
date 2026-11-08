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
            HomeSubtitle = "Open DEMO-01 from its photo. Try the reference PDF and Common Problems, or explore PLC files, the light column and the safety scanner. Hardware operations are disabled in this example.",
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
            PlcModel = "Illustrative PLC",
            PlcNotes = "The saved listing contains fictional tags for file browsing practice.",
        };
        gate.AssetFolder = AssetFileLayout.SuggestedRoot(gate);
        data.SaveGates([gate]);
        var unit = new SafetySystem
        {
            Id = "demo-unit",
            GateId = gate.Id,
            UnitNumber = "1",
            DisplayName = "Identification label example",
            ShowElectricalDrawings = false,
            ShowLightConfiguration = true,
            ShowScannerConfiguration = true,
            ShowPartsBom = false,
            ManualIds = [],
        };

        string sources = Path.Combine(workspace, "Sources");
        Directory.CreateDirectory(sources);
        data.SetIdentificationPhoto(unit, ExtractDemoFile("Identification.png", sources, "DEMO-01.png"));
        data.SynchronizeLegacyFields([gate], [unit]);
        data.SaveSystems([unit]);
        data.AddPilotPdf(unit.Id, ExtractDemoFile("DemoReference.pdf", sources, "DEMO-01 Reference.pdf"));
        var plc = AssetFileLayout.Create(gate, "PLC");
        var column = AssetFileLayout.Create(gate, "Light Configurations");
        var scanner = AssetFileLayout.Create(gate, "Scanner Configurations");
        foreach (var resource in new[] { plc, column, scanner })
            data.EnsureResourceFolders(resource, gate);

        const string plcFile = "DEMO ONLY - PLC code listing.txt";
        ExtractDemoFile("PlcListing.txt", data.ResourceCurrentFolder(plc, gate), plcFile);
        plc.FileAssignments[plcFile] = [];
        const string columnFile = "DEMO ONLY - not for equipment.signal";
        File.WriteAllBytes(Path.Combine(data.ResourceCurrentFolder(column, gate), columnFile), HomePreviewService.SampleConfiguration());
        column.FileAssignments[columnFile] = [unit.Id];
        const string scannerFile = "DEMO ONLY - scanner configuration record.txt";
        ExtractDemoFile("ScannerRecord.txt", data.ResourceCurrentFolder(scanner, gate), scannerFile);
        scanner.FileAssignments[scannerFile] = [unit.Id];
        data.SaveResources([plc, column, scanner]);

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
