using System.IO;
using System.Text;
using System.Text.Json;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

public partial class DataService
{
    public void CreateBlankPlantLibrary(string destination, string firstArea)
    {
        if (string.IsNullOrWhiteSpace(destination) || string.IsNullOrWhiteSpace(firstArea)) throw new InvalidOperationException("Choose an empty folder and enter the first plant area.");
        string root = Path.GetFullPath(destination); Directory.CreateDirectory(root);
        if (Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("This folder is not empty. Connect to its existing library, or choose a new empty folder. Nothing was replaced.");
        string staging = Path.Combine(root, ".new-plant-" + Guid.NewGuid().ToString("N"));
        bool appDataMoved = false, dataMoved = false;
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, "Data"));
            void Write(string relative, object value) { string path = Path.Combine(staging, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(value, _write)); }
            foreach (string name in new[] { "systems", "gates", "resources", "help", "parts", "manuals", "troubleshooting" }) Write("Data/" + name + ".json", Array.Empty<object>());
            Write("Data/categories.json", new[] { firstArea.Trim() });
            Write("Data/shared-content.json", new SharedContent { StoreroomUrl = "" });
            // A blank plant must stay blank when defaults are refreshed or the app restarts.
            Write("_AppData/Catalogs/" + Path.GetFileName(PartsCatalogMarker), new { catalog = "Blank plant library", bundledCatalogImported = false });
            Directory.Move(Path.Combine(staging, "_AppData"), Path.Combine(root, "_AppData")); appDataMoved = true;
            Directory.Move(Path.Combine(staging, "Data"), Path.Combine(root, "Data")); dataMoved = true;
        }
        catch
        {
            if (appDataMoved && !dataMoved) Directory.Delete(Path.Combine(root, "_AppData"), true);
            throw;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        try { SetSharedRoot(root); EnsureSharedDefaults(); LogAudit("Blank plant library created", details: firstArea.Trim()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new IOException("The blank library was created at " + root + ", but connecting this PC could not finish. Retry using Connect to existing library. " + error.Message, error); }
    }
    public ManualRecord AddPilotPdf(string unitId, string source)
    {
        using var lease = AcquireAssetWriteLock();
        if (!Path.GetExtension(source).Equals(".pdf", StringComparison.OrdinalIgnoreCase) || new FileInfo(source).Length > 50 * 1024 * 1024) throw new InvalidDataException("Choose a PDF smaller than 50 MB.");
        using (var input = File.OpenRead(source))
        { var header = new byte[5]; input.ReadExactly(header); if (Encoding.ASCII.GetString(header) != "%PDF-") throw new InvalidDataException("This file is not a PDF."); }
        var units = JsonSerializer.Deserialize<List<SafetySystem>>(File.ReadAllText(SystemsFile), _read) ?? throw new InvalidDataException("Unit records unavailable.");
        var unit = units.SingleOrDefault(u => u.Id == unitId) ?? throw new InvalidOperationException("Select a saved pilot unit first.");
        byte[] beforeManuals = File.ReadAllBytes(ManualsIndexFile), beforeUnits = File.ReadAllBytes(SystemsFile);
        var manuals = JsonSerializer.Deserialize<List<ManualRecord>>(beforeManuals, _read) ?? throw new InvalidDataException("Manual records unavailable.");
        var manual = new ManualRecord { Title = Path.GetFileNameWithoutExtension(source), AppliesToUnitIds = [unitId] };
        string target = Path.Combine(ManualLibraryFolder, "Pilot", Guid.NewGuid().ToString("N") + ".pdf");
        AtomicCopy(source, target); manual.FilePath = ToRelative(target);
        unit.ManualIds = (unit.ManualIds ?? manuals.Where(m => m.AppliesTo(unit.GateId, unit.Id)).Select(m => m.Id).ToList()).Append(manual.Id).Distinct().ToList();
        unit.ShowManuals = true; manuals.Add(manual);
        try { SaveManualRecords(manuals); SaveSystems(units); }
        catch
        {
            // Keep the copied file if rollback itself fails: never leave a surviving record pointing at a deleted file.
            File.WriteAllBytes(ManualsIndexFile, beforeManuals); File.WriteAllBytes(SystemsFile, beforeUnits); File.Delete(target); throw;
        }
        LogAudit("Pilot PDF attached", unit.GateId, unit.Id, manual.Title); return manual;
    }
}
