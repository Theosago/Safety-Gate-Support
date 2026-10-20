using System.Text.Json;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;
using SafetyGateSupport.Siemens;

namespace SafetyGateSupport.Validation;

internal static class HomePreviewChecks
{
    public static IEnumerable<(string Name, Action Check)> Cases =>
    [
        ("Home preview is selected only by its launcher name or explicit flag", PreviewSelection),
        ("Home preview ignores normal and stale preview share settings and preserves edits", ProfileIsolation),
        ("Home preview sample renders stored states and cannot enter an upload session", SampleUploadGuard)
    ];
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void PreviewSelection()
    {
        Require(HomePreviewService.IsRequested([], "SafetyGateSupportPreview.exe"), "Preview executable name was ignored.");
        Require(HomePreviewService.IsRequested(["--preview"], "SafetyGateSupport.exe"), "Explicit preview flag was ignored.");
        Require(!HomePreviewService.IsRequested(["preview", "--preview-other"], "SafetyGateSupport.exe"), "Normal launch unexpectedly selected preview mode.");
    }
    private static void ProfileIsolation()
    {
        string root = Directory.CreateTempSubdirectory("home-preview-profile-").FullName;
        try
        {
            string normalSettings = Path.Combine(root, "SafetyGateSupport", "settings.json"); Directory.CreateDirectory(Path.GetDirectoryName(normalSettings)!);
            string normal = JsonSerializer.Serialize(new AppSettings { SharedRoot = @"Q:\SafetyGateSupport", UamExePath = "leave-normal-settings-alone.exe" });
            File.WriteAllText(normalSettings, normal);
            string oldPreview = Path.Combine(root, "SafetyGateSupport", "HomePreview", "Settings", "settings.json"); Directory.CreateDirectory(Path.GetDirectoryName(oldPreview)!);
            File.WriteAllText(oldPreview, JsonSerializer.Serialize(new AppSettings { SharedRoot = @"Q:\DoNotUse" }));
            var data = HomePreviewService.Create(root);
            Require(data.IsHomePreview && data.SharedRoot == Path.Combine(root, "SafetyGateSupport", "HomePreview", "Data"), "Preview did not force its isolated local root.");
            Require(!data.IsReady, "Factory initialized starter data synchronously.");
            HomePreviewService.Initialize(data);
            Require(data.IsReady && data.LoadSystems().Count == 61 && data.LoadGates().Count == 29, "Local starter inventory or sample unit missing.");
            var gate = data.LoadGates().Single(g => g.Id == "home-preview-gate");
            var unit = data.LoadSystems().Single(u => u.Id == "home-preview-unit");
            var resource = data.LoadResources().Single(r => r.Id == "home-preview-light");
            Require(gate.AssetNumber == "DEMO" && resource.AppliesTo(unit.Id), "Sample assignment lost its explicit demo identity.");
            string sample = Directory.GetFiles(data.ResourceCurrentFolder(resource, gate), "*.signal").Single();
            Require(PreparedSignalUpload.Load(sample).IsPreviewOnly, "Sample file is not marked preview-only.");
            data.SaveSystemNotes(unit, "My local preview note"); HomePreviewService.Initialize(data);
            Require(data.LoadSystems().Count == 61 && data.LoadSystemNotes(unit) == "My local preview note", "Reopening duplicated sample data or overwrote edits.");
            Require(File.ReadAllText(normalSettings) == normal, "Home preview changed the normal application's settings.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void SampleUploadGuard()
    {
        var selected = PreparedSignalUpload.FromBytes(HomePreviewService.SampleConfiguration(), "DEMO ONLY.signal");
        Require(selected.IsPreviewOnly && selected.Archive.ActiveStates.SequenceEqual([1, 2, 4, 8]), "Sample states or preview marker incorrect.");
        Require(SignalStatePreview.Describe(selected, 1).Contains("RGB 255, 0, 0"), "Sample red state did not render.");
        bool connected = false;
        try
        {
            SignalUploadSession.Run(selected, new SiemensUploadTarget("COM99", "FAKE"), "unused-preview-report.zip", _ => { connected = true; throw new InvalidOperationException("Must not connect"); });
            throw new InvalidOperationException("Preview sample entered an upload session.");
        }
        catch (InvalidDataException) { }
        Require(!connected && !File.Exists("unused-preview-report.zip"), "Preview upload opened a port or created a report.");
    }
}
