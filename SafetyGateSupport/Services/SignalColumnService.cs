using System.IO;
using System.Windows;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

public static class SignalColumnService
{
    public static bool IsLightResource(ResourceDefinition r) => r.Kind.Equals("ManagedFiles", StringComparison.OrdinalIgnoreCase) &&
        (r.LaunchAction.Equals("Siemens", StringComparison.OrdinalIgnoreCase) || r.Label.Contains("Light Config", StringComparison.OrdinalIgnoreCase));

    public static async Task OpenAsync(Window owner, DataService data, SafetyGate gate, SafetySystem unit, IEnumerable<ResourceDefinition> resources)
    {
        try
        {
            var current = await Task.Run(data.LoadResources);
            var assigned = current.Concat(resources).GroupBy(r => r.Id).Select(g => g.First()).Where(r => r.IsVisible && r.GateId == gate.Id && r.AppliesTo(unit.Id) && IsLightResource(r)).ToList();
            var paths = await Task.Run(() => assigned.Select(r => data.ResourceCurrentFolder(r, gate)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists).SelectMany(f => Directory.EnumerateFiles(f, "*", SearchOption.TopDirectoryOnly))
                .Where(p => Path.GetExtension(p).Equals(".signal", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray());
            new SiemensUsbCheck.MainWindow($"{gate.Display} / {unit.UnitDisplay}", paths, previewOnly: data.IsHomePreview) { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
        }
        catch (Exception error) { MessageBox.Show(owner, error.Message, "Unable to open signal column tools", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
