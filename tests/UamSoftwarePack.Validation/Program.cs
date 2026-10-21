using System.IO.Compression;
using System.Text;
using System.Text.Json;
using UamSoftwarePack;

var cases = new (string Name, Action<string> Check)[]
{
    ("portable application, USB DLL, INF and resources retain exact bytes and layout", root =>
    {
        string source = Zip(root, ("UAM/UAMProjectDesigner.exe", "vendor-app"u8.ToArray()), ("UAM/lib/CyUSB.dll", "usb-bytes"u8.ToArray()), ("driver/UAM.inf", "VID_1234"u8.ToArray()), ("UAM/en/error-messages.xml", "<errors/>"u8.ToArray()));
        string output = Path.Combine(root, "pack.zip"); var result = SoftwarePack.Create(source, output);
        using var zip = ZipFile.OpenRead(output);
        Require(result.IncludedFiles == 4 && result.TotalFiles == 4, "Selected files were lost.");
        Require(Read(zip, "vendor-files/UAM/lib/CyUSB.dll").SequenceEqual("usb-bytes"u8.ToArray()), "Vendor bytes changed.");
        Require(zip.GetEntry("vendor-files/driver/UAM.inf") is not null && zip.GetEntry("pack.json") is not null, "Driver metadata or provenance missing.");
    }),
    ("private projects, licenses, logs and databases are inventory-only", root =>
    {
        string source = Zip(root, ("Asset1225.hux", "private-project"u8.ToArray()), ("license.dll", "license-data"u8.ToArray()), ("scanner.log", "log"u8.ToArray()), ("db.sqlite", "db"u8.ToArray()));
        string output = Path.Combine(root, "pack.zip"); var result = SoftwarePack.Create(source, output);
        Require(result.IncludedFiles == 0 && result.TotalFiles == 4, "Private/project data was selected.");
        using var zip = ZipFile.OpenRead(output); Require(!zip.Entries.Any(e => e.FullName.StartsWith("vendor-files/")), "Unexpected private file in pack.");
        Require(Encoding.UTF8.GetString(Read(zip, "inventory.json")).Contains("Asset1225.hux"), "Inventory lost unselected filenames.");
    }),
    ("USB files are prioritized before generic dependencies and the raw-byte budget is enforced", root =>
    {
        string source = Zip(root, ("UAM.exe", new byte[1024 * 1024]), ("USBTransport.dll", new byte[1024 * 1024]), ("Generic.dll", new byte[25 * 1024 * 1024]), ("Qt5Core.dll", [1, 2, 3]));
        string output = Path.Combine(root, "pack.zip"); var result = SoftwarePack.Create(source, output);
        using var zip = ZipFile.OpenRead(output);
        Require(result.IncludedFiles == 2 && zip.GetEntry("vendor-files/USBTransport.dll") is not null, "Transport files were crowded out.");
        Require(result.Bytes <= SoftwarePack.MaxPackBytes && zip.GetEntry("vendor-files/Generic.dll") is null, "Raw/output budget was exceeded.");
    }),
    ("path traversal, absolute paths, Windows device names and case collisions are rejected", root =>
    {
        foreach (string name in new[] { "../evil.exe", "/evil.exe", "C:/evil.exe", "folder/..\\evil.dll", "A:stream.dll", "NUL.exe", "CON/config.inf" })
            Refuses(() => SoftwarePack.Create(Zip(root, (name, [1])), Path.Combine(root, Guid.NewGuid() + ".zip")));
        Refuses(() => SoftwarePack.Create(Zip(root, ("UAM.exe", [1]), ("uam.exe", [2])), Path.Combine(root, "case.zip")));
    }),
    ("selected entries must pass CRC validation; corrupt packs leave no result or temporary file", root =>
    {
        string source = Zip(root, ("UAM.exe", "payload"u8.ToArray())); byte[] bytes = File.ReadAllBytes(source);
        for (int i = 0; i < bytes.Length - 20; i++) if (bytes[i] == 0x50 && bytes[i+1] == 0x4b && bytes[i+2] == 1 && bytes[i+3] == 2) { bytes[i+16] ^= 1; break; }
        File.WriteAllBytes(source, bytes); string output = Path.Combine(root, "pack.zip"); Refuses(() => SoftwarePack.Create(source, output));
        Require(!File.Exists(output) && !Directory.GetFiles(root, ".uam-pack-*").Any(), "Failed pack was published or not cleaned up.");
        Require(File.ReadAllBytes(source).SequenceEqual(bytes), "Input archive changed.");
    }),
    ("cancellation discards unfinished output", root =>
    {
        string source = Zip(root, ("UAM.exe", new byte[10000])); using var cancel = new CancellationTokenSource();
        string output = Path.Combine(root, "pack.zip");
        try { SoftwarePack.Create(source, output, cancel.Token, _ => cancel.Cancel()); throw new InvalidOperationException("Canceled pack completed."); } catch (OperationCanceledException) { }
        Require(!File.Exists(output) && !Directory.GetFiles(root, ".uam-pack-*").Any(), "Cancellation left partial output.");
    }),
    ("existing source/output files are preserved", root =>
    {
        string source = Zip(root, ("UAM.exe", [1, 2])); byte[] original = File.ReadAllBytes(source);
        Refuses(() => SoftwarePack.Create(source, source));
        string output = Path.Combine(root, "existing.zip"); File.WriteAllText(output, "keep"); Refuses(() => SoftwarePack.Create(source, output));
        Require(File.ReadAllText(output) == "keep" && File.ReadAllBytes(source).SequenceEqual(original), "An existing file was overwritten.");
    }),
    ("symbolic-link archive entries are rejected", root =>
    {
        string source = Path.Combine(root, "link.zip"); using (var zip = ZipFile.Open(source, ZipArchiveMode.Create)) { var e = zip.CreateEntry("UAM.exe"); e.ExternalAttributes = unchecked((int)0xa1ff0000); using var writer = new StreamWriter(e.Open()); writer.Write("../outside"); }
        Refuses(() => SoftwarePack.Create(source, Path.Combine(root, "pack.zip")));
    }),
    ("managed assembly metadata is inspected without invoking vendor code", root =>
    {
        string source = Zip(root, ("Hokuyo.dll", File.ReadAllBytes(typeof(SoftwarePack).Assembly.Location)));
        string output = Path.Combine(root, "pack.zip"); SoftwarePack.Create(source, output);
        using var zip = ZipFile.OpenRead(output); string report = Encoding.UTF8.GetString(Read(zip, "pack.json"));
        Require(report.Contains("Managed .NET assembly") && report.Contains("UamSoftwarePack.Validation"), "Managed metadata was not identified.");
        using var json = JsonDocument.Parse(report); Require(json.RootElement.GetProperty("sourceName").GetString() == Path.GetFileName(source), "The report leaked an absolute source path.");
    }),
    ("empty archives are rejected", root =>
    {
        string source = Zip(root); Refuses(() => SoftwarePack.Create(source, Path.Combine(root, "pack.zip")));
    })
};
int passed = 0;
foreach (var (name, run) in cases)
{
    string root = Directory.CreateTempSubdirectory("uam-pack-validation-").FullName;
    try { run(root); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine($"FAIL {name}: {error}"); }
    finally { Directory.Delete(root, true); }
}
Console.WriteLine($"UAM collector: {passed}/{cases.Length} passed."); return passed == cases.Length ? 0 : 1;

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Refuses(Action action) { try { action(); } catch (Exception e) when (e is IOException or InvalidDataException) { return; } throw new InvalidOperationException("Invalid request was accepted."); }
static string Zip(string root, params (string Name, byte[] Body)[] files)
{
    string source = Path.Combine(root, Guid.NewGuid() + ".zip"); using var zip = ZipFile.Open(source, ZipArchiveMode.Create);
    foreach (var f in files) { using var entry = zip.CreateEntry(f.Name).Open(); entry.Write(f.Body); }
    return source;
}
static byte[] Read(ZipArchive zip, string name) { using var entry = zip.GetEntry(name)!.Open(); using var bytes = new MemoryStream(); entry.CopyTo(bytes); return bytes.ToArray(); }
