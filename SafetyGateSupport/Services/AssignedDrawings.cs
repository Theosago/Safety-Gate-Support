using System.IO;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

public static class AssignedDrawings
{
    public sealed record FileItem(string Name, string Path) { public ResourceDefinition? Resource { get; init; } }
    public sealed record Listing(IReadOnlyList<FileItem> Files, IReadOnlyList<string> Unavailable);
    public static bool IsDrawing(ResourceDefinition r) => r.Kind.Equals("ManagedFiles", StringComparison.OrdinalIgnoreCase) &&
        (r.StorageCategory == "Electrical Drawings" || new[] { "drawing", "schematic", "wiring diagram" }.Any(word => $"{r.Label} {r.Keywords}".Contains(word, StringComparison.OrdinalIgnoreCase)));

    public static Listing List(DataService data, SafetyGate gate, SafetySystem unit, IEnumerable<ResourceDefinition> resources, CancellationToken token)
    {
        var files = new List<FileItem>(); var unavailable = new List<string>();
        foreach (var resource in data.ResourcesFor(gate, unit, resources).Where(IsDrawing))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (var path in data.GetResourceFiles(resource, gate))
                {
                    token.ThrowIfCancellationRequested();
                    var pair = resource.DrawingPairs.FirstOrDefault(p => p.Dwg.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
                    if (pair is not null && resource.IncludesFile(pair.Pdf) && File.Exists(Path.Combine(data.ResourceCurrentFolder(resource, gate), pair.Pdf))) continue;
                    if (new[] { ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".dwg", ".dxf" }.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                        files.Add(new($"{resource.Label} • {System.IO.Path.GetFileName(path)}", path) { Resource = resource });
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { unavailable.Add(resource.Label); }
        }
        return new(files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => System.IO.Path.GetExtension(f.Path).ToLowerInvariant() switch { ".pdf" => 0, ".dwg" or ".dxf" => 2, _ => 1 })
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray(), unavailable);
    }
}
