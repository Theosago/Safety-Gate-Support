namespace SafetyGateSupport.Models;

// Inventory routes supplied by the plant-selection portal. Choice is local to this PC.
public sealed record StockroomPlant(string Id, string Name, string Url)
{
    public static IReadOnlyList<StockroomPlant> All { get; } =
    [
        new("02", "Huntingdon", "http://usdevapps01:3454/inventory/02"),
        new("04", "Salisbury", "http://usdevapps01:3454/inventory/04"),
        new("05", "Newport", "http://usdevapps01:3454/inventory/05")
    ];
    public static StockroomPlant? Find(string? id) => All.FirstOrDefault(p => p.Id == id);
}
