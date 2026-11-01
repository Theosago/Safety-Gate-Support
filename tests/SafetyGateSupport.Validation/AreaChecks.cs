using System.Text;
using System.Text.Json;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport.Validation;

internal static class AreaChecks
{
    public static IEnumerable<(string Name, Action Check)> Cases =>
    [
        ("Area rename preserves asset IDs, unit assignments, file paths and unrelated records", Rename),
        ("Area edits reject duplicates, missing names and deletion of an occupied area", RejectedEdits),
        ("Area edits read current records rather than overwrite another user's inventory changes", FreshRecords),
        ("Malformed or locked shared records cannot publish part of an area change", FailedEdits),
        ("Recent audit tail retains Unicode, line limits and incomplete-line handling", AuditTail)
    ];
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("area-check-").FullName;
        public DataService Data { get; }
        public Fixture()
        {
            Data = new(false, Path.Combine(Root, "settings")); Data.SetSharedRoot(Path.Combine(Root, "share")); Data.InitializeSharedData();
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
    private static void Rename()
    {
        using var f = new Fixture(); var d = f.Data; var gates = d.LoadGates(); var units = d.LoadSystems();
        var first = gates[0]; string old = first.Category, name = old + " — Renamed";
        string resourceBytes = File.ReadAllText(d.ResourcesFile);
        var result = d.ChangeArea(old, name);
        Require(result.Areas.Contains(name) && !result.Areas.Contains(old), "Category index was not renamed.");
        Require(gates.Where(g => g.Category == old).All(g => result.Gates.Single(n => n.Id == g.Id).Category == name), "Gate assignments changed.");
        foreach (var unit in units)
        {
            var after = result.Units.Single(u => u.Id == unit.Id);
            Require(after.GateId == unit.GateId && after.PrimaryPhoto == unit.PrimaryPhoto && after.PhotosFolder == unit.PhotosFolder && after.DrawingsFolder == unit.DrawingsFolder && after.NotesFolder == unit.NotesFolder, "Rename moved files or changed unit identity.");
            Require(after.Category == (unit.Category == old ? name : unit.Category), "Unit area does not follow its asset.");
        }
        Require(resourceBytes == File.ReadAllText(d.ResourcesFile), "Rename rewrote unrelated resource assignments.");
        var caseOnly = d.ChangeArea(name, name.ToUpperInvariant()); Require(caseOnly.Areas.Contains(name.ToUpperInvariant()), "Case-only rename was lost.");
    }
    private static void RejectedEdits()
    {
        using var f = new Fixture(); var d = f.Data; var names = d.LoadCategories(); string before = File.ReadAllText(d.CategoriesFile);
        foreach (var (old, name) in new (string?, string?)[] { (names[0], names[1]), (names[0], " "), ("missing area", "New"), (names[0], null) })
        {
            bool refused = false; try { d.ChangeArea(old, name); } catch (InvalidOperationException) { refused = true; }
            Require(refused && before == File.ReadAllText(d.CategoriesFile), "Invalid edit changed categories.");
        }
        string gates = File.ReadAllText(d.GatesFile), units = File.ReadAllText(d.SystemsFile);
        d.ChangeArea(null, "Unoccupied test area"); d.ChangeArea("Unoccupied test area", null);
        Require(before == File.ReadAllText(d.CategoriesFile) && gates == File.ReadAllText(d.GatesFile) && units == File.ReadAllText(d.SystemsFile), "Adding/deleting an empty area changed inventory.");
    }
    private static void FreshRecords()
    {
        using var f = new Fixture(); var d = f.Data; string old = d.LoadCategories()[0];
        var current = d.LoadGates(); current[0].Location = "Edited on another PC"; d.SaveGates(current);
        var result = d.ChangeArea(old, old + " updated");
        Require(result.Gates.Single(g => g.Id == current[0].Id).Location == "Edited on another PC", "Rename overwrote a current inventory edit.");
    }
    private static void FailedEdits()
    {
        using var f = new Fixture(); var d = f.Data; string old = d.LoadCategories()[0], before = File.ReadAllText(d.CategoriesFile);
        File.WriteAllText(d.GatesFile, "{invalid JSON");
        bool refused = false; try { d.ChangeArea(old, "Bad edit"); } catch (JsonException) { refused = true; }
        Require(refused && before == File.ReadAllText(d.CategoriesFile) && File.ReadAllText(d.GatesFile) == "{invalid JSON", "Malformed records were silently overwritten.");
        using var held = new FileStream(Path.Combine(d.SharedRoot, "_AppData", "asset-files.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        refused = false; try { d.ChangeArea(null, "Locked edit"); } catch (IOException) { refused = true; }
        Require(refused && before == File.ReadAllText(d.CategoriesFile), "Concurrent write lease did not protect records.");
    }
    private static void AuditTail()
    {
        using var f = new Fixture(); var d = f.Data;
        var rows = Enumerable.Range(0, 12).Select(i => JsonSerializer.Serialize(new AuditEntry { Action = i.ToString(), Details = new string('界', 23000) + " Gränges 🛠" })).ToArray();
        File.WriteAllText(d.AuditFile, string.Join("\r\n", rows) + "\r\n", new UTF8Encoding(true));
        var last = d.LoadRecentAudit(3);
        Require(last.Select(e => e.Action).SequenceEqual(["11", "10", "9"]) && last.All(e => e.Details.EndsWith("Gränges 🛠")), "Audit block boundaries corrupted Unicode or newest-first ordering.");
        File.AppendAllText(d.AuditFile, "incomplete JSON");
        Require(d.LoadRecentAudit(3).Select(e => e.Action).SequenceEqual(["11", "10"]), "Incomplete last line changed the physical-line limit.");
        Require(d.LoadRecentAudit(0).Count == 0 && d.LoadRecentAudit(-1).Count == 0, "Empty audit limit returned entries.");
        File.WriteAllText(d.AuditFile, rows[0]); Require(d.LoadRecentAudit(3).Single().Action == "0", "A final line without newline was lost.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
