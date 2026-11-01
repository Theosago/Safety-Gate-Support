using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport.Validation;

internal static class PartsCatalogChecks
{
    public static void Check()
    {
        string root = Directory.CreateTempSubdirectory("parts-catalog-check-").FullName;
        try
        {
            var data = new DataService(false, Path.Combine(root, "settings")); data.SetSharedRoot(Path.Combine(root, "share")); data.InitializeSharedData();
            string[] expected = ["0115639", "0115638", "0115641", "0115640", "0115642", "0117369", "0117488", "0117489", "0117490", "0116820", "0117711", "0117712", "0117713", "0117714", "0117715", "0117716", "0117717", "0117718", "0117719"];
            var parts = data.LoadParts();
            Require(expected.All(n => parts.Count(p => p.PartNumber == n) == 1), "A supplied stock number is missing or duplicated.");
            Require(parts.Single(p => p.PartNumber == "0115639").Notes.Contains("5.00") && parts.Single(p => p.PartNumber == "0115639").Notes.Contains("TOP K-12"), "Quantity or bin location was lost.");
            Require(parts.Single(p => p.PartNumber == "0117719").Notes.Contains("Site: Not supplied"), "An unspecified site was invented.");
            Require(parts.Single(p => p.PartNumber == "0116820").Keywords.Contains("8WD4613-5JH37"), "Canonical Siemens model is not searchable.");
            Require(parts.Single(p => p.PartNumber == "0117719").QuantityDisplay == "0" && parts.Single(p => p.PartNumber == "0117716").SiteValue == "HRM", "Zero stock or the corrected HRM site is missing.");
            string marker = data.Resolve("_AppData/Catalogs/safe2cross-parts-v2.json");
            // Existing v1 installations must acquire the three missing entries and corrected site.
            var oldCatalog = parts.Where(p => p.PartNumber is not ("0117717" or "0117718" or "0117719")).ToList();
            var mount = oldCatalog.Single(p => p.PartNumber == "0117716"); mount.Site = ""; mount.Notes = mount.Notes.Replace("Site: HRM", "Site: Not supplied"); mount.PhotoPath = "existing-photo.png";
            File.Delete(marker); data.SaveParts(oldCatalog); File.WriteAllText(data.Resolve("_AppData/Catalogs/safe2cross-parts-v1.json"), "{}");
            data.EnsureSharedDefaults();
            Require(data.LoadParts().Count == 19 && data.LoadParts().Single(p => p.PartNumber == "0117716").SiteValue == "HRM" && data.LoadParts().Single(p => p.PartNumber == "0117716").PhotoPath == "existing-photo.png", "The v1-to-v2 catalog upgrade missed parts or lost photo metadata.");
            File.Delete(marker);
            data.SaveParts([new() { Id = "existing-edited", PartNumber = "115639", Description = "Site-edited description", Notes = "Keep this stock note" }, new() { Id = "custom", PartNumber = "CUSTOM", Description = "Unrelated custom part" }]);
            data.EnsureSharedDefaults(); parts = data.LoadParts();
            Require(parts.Count == 20 && parts.Single(p => p.Id == "existing-edited").Notes == "Keep this stock note" && parts.Any(p => p.PartNumber == "CUSTOM"), "Upgrade duplicated an existing stock number or overwrote custom records.");
            parts.RemoveAll(p => p.PartNumber == "0117716"); data.SaveParts(parts); data.EnsureSharedDefaults();
            Require(data.LoadParts().Count == 19 && data.LoadParts().All(p => p.PartNumber != "0117716"), "A completed import reintroduced a deliberately removed part.");
            // Read-only/temporarily locked shares must still offer the new catalog without a write.
            File.Delete(marker); data.SaveParts([]);
            using (var held = new FileStream(data.Resolve("_AppData/asset-files.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                data.EnsureSharedDefaults();
                Require(data.LoadParts().Count == 19 && !File.Exists(marker), "A pending read-only import hid the catalog or falsely marked it complete.");
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
