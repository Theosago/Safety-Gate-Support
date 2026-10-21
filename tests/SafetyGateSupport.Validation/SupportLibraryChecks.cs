using System.IO.Compression;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport.Validation;

internal static class SupportLibraryChecks
{
    public static IEnumerable<(string Name, Action Check)> Cases =>
    [
        ("Library import retains portable software layout without running it", PortableLayout),
        ("Library import deduplicates bytes and retains different revisions", Deduplication),
        ("Library import rejects traversal, drive paths, ADS, reserved names, and collisions", UnsafePaths),
        ("Canceled library import leaves no published or temporary files", Cancellation),
        ("Library import rejects symbolic links", SymbolicLink),
        ("Library listing rejects tampered manifest paths", TamperedManifest),
        ("Library import rejects corrupted ZIP bytes", CorruptZip),
        ("Curated troubleshooting branches resolve and preserve site overrides", CuratedFlows)
    ];
    private static void WithRoot(Action<string> action)
    {
        string root = Directory.CreateTempSubdirectory("support-library-check-").FullName;
        try { action(root); } finally { Directory.Delete(root, true); }
    }
    private static string Zip(string root, params (string Name, string Body)[] entries)
    {
        string path = Path.Combine(root, Guid.NewGuid() + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, body) in entries) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(body); }
        return path;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Refuses(Action action)
    {
        try { action(); } catch (Exception e) when (e is InvalidDataException or IOException) { return; }
        throw new InvalidOperationException("Unsafe import was accepted.");
    }
    private static void PortableLayout() => WithRoot(root =>
    {
        string zip = Zip(root, ("BusLink/BusLink2.exe", "not an executable"), ("BusLink/LinakUsbDll/driver.dll", "dependency"), ("West/Asset 1225/Unit 8.signal", "original bytes"));
        string library = Path.Combine(root, "library");
        var result = SupportLibraryService.Import(library, zip);
        var files = SupportLibraryService.List(library);
        Require(result.FileCount == 3 && files.Count == 3, "Portable archive inventory changed.");
        var program = files.Single(f => f.Name == "BusLink2.exe");
        Require(File.ReadAllText(program.FullPath) == "not an executable", "Imported binary bytes changed.");
        Require(File.Exists(Path.Combine(Path.GetDirectoryName(program.FullPath)!, "LinakUsbDll", "driver.dll")), "Portable dependencies were separated.");
        Require(files.All(f => f.SourceName == Path.GetFileName(zip)), "Original source provenance was lost.");
    });
    private static void Deduplication() => WithRoot(root =>
    {
        string library = Path.Combine(root, "library"), a = Path.Combine(root, "guide.pdf"), b = Path.Combine(root, "copy.pdf");
        File.WriteAllText(a, "revision one"); File.Copy(a, b);
        Require(!SupportLibraryService.Import(library, a).AlreadyImported, "First import treated as duplicate.");
        Require(SupportLibraryService.Import(library, b).AlreadyImported, "Identical renamed source was not deduplicated.");
        File.WriteAllText(a, "revision two"); SupportLibraryService.Import(library, a);
        var files = SupportLibraryService.List(library);
        Require(files.Count == 2 && files.Select(f => File.ReadAllText(f.FullPath)).ToHashSet().SetEquals(["revision one", "revision two"]), "A different revision overwrote the old version.");
    });
    private static void UnsafePaths() => WithRoot(root =>
    {
        string library = Path.Combine(root, "library");
        foreach (string name in new[] { "../escape.pdf", "folder/../../escape.pdf", "C:/evil.pdf", "\\\\server\\evil.pdf", "/evil.pdf", "folder/a:stream", "NUL.txt", "folder/COM1.pdf", "folder/trailing. /x.pdf", "folder/..\\escape.pdf" })
            Refuses(() => SupportLibraryService.Import(library, Zip(root, (name, "bad"))));
        Refuses(() => SupportLibraryService.Import(library, Zip(root, ("A.pdf", "first"), ("a.pdf", "second"))));
        Refuses(() => SupportLibraryService.Import(library, Zip(root, ("a", "file"), ("a/b.pdf", "collision"))));
        Require(!Directory.EnumerateDirectories(library).Any(), "A rejected import was published or left staging files.");
    });
    private static void Cancellation() => WithRoot(root =>
    {
        string library = Path.Combine(root, "library"); using var cancel = new CancellationTokenSource();
        string zip = Zip(root, ("one.pdf", "first"), ("two.pdf", "second"));
        try { SupportLibraryService.Import(library, zip, cancel.Token, _ => cancel.Cancel()); throw new InvalidOperationException("Canceled import completed."); }
        catch (OperationCanceledException) { }
        Require(!Directory.EnumerateDirectories(library).Any(), "Cancellation left a partial import.");
    });
    private static void SymbolicLink() => WithRoot(root =>
    {
        string path = Path.Combine(root, "link.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("link.pdf"); entry.ExternalAttributes = unchecked((int)0xa1ff0000);
            using var writer = new StreamWriter(entry.Open()); writer.Write("../../outside.pdf");
        }
        Refuses(() => SupportLibraryService.Import(Path.Combine(root, "library"), path));
        if (OperatingSystem.IsLinux())
        {
            string target = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
            string link = Path.Combine(root, "library-link"); Directory.CreateSymbolicLink(link, target);
            Refuses(() => SupportLibraryService.Import(link, Zip(root, ("guide.pdf", "data"))));
        }
    });
    private static void TamperedManifest() => WithRoot(root =>
    {
        string library = Path.Combine(root, "library"); SupportLibraryService.Import(library, Zip(root, ("guide.pdf", "data")));
        string manifest = Path.Combine(Directory.GetDirectories(library).Single(), "manifest.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("guide.pdf", "../outside.pdf"));
        Refuses(() => SupportLibraryService.List(library));
    });
    private static void CorruptZip() => WithRoot(root =>
    {
        string path = Zip(root, ("manual.pdf", "payload")); byte[] bytes = File.ReadAllBytes(path);
        // Damage the central-directory CRC while leaving payload and framing readable.
        for (int i = 0; i < bytes.Length - 20; i++) if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 1 && bytes[i + 3] == 2) { bytes[i + 16] ^= 1; break; }
        File.WriteAllBytes(path, bytes); Refuses(() => SupportLibraryService.Import(Path.Combine(root, "library"), path));
    });
    private static void CuratedFlows()
    {
        var defaults = GuidedTroubleshooting.Defaults().ToList();
        Require(defaults.Count == 4 && defaults.Select(f => f.Id).Distinct().Count() == 4, "Missing or duplicated model-specific flows.");
        foreach (var flow in defaults)
        {
            Require(flow.Nodes.Any(n => n.Id == flow.StartNodeId), "Missing flow start.");
            Require(flow.Nodes.SelectMany(n => n.Choices).All(c => flow.Nodes.Any(n => n.Id == c.NextNodeId)), "Unresolved troubleshooting branch.");
            Require(flow.Nodes.All(n => n.Action is "" or "Library" or "SignalColumn" or "UamDiagnostics"), "Unknown tool action.");
        }
        var edited = new TroubleshootingFlow { Id = defaults[0].Id, Title = "Site edited flow", Nodes = [new() { Prompt = "Keep this edit" }] };
        var merged = GuidedTroubleshooting.Merge([edited]);
        Require(merged.Count == 4 && ReferenceEquals(merged.Single(f => f.Id == edited.Id), edited), "A site edit was overwritten by a built-in flow.");
        Require(GuidedTroubleshooting.Merge(merged).Count == 4, "Repeated merging duplicates flows.");
    }
}
