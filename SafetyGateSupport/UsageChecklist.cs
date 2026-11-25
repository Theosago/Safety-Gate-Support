using System.Windows;
using System.Windows.Controls;
using SafetyGateSupport.Models;

namespace SafetyGateSupport;

/// <summary>
/// Asset → unit checklist used in Plant setup to choose where a part or manual is used.
/// Ticking an asset ticks all of its units; a whole-asset record shows as a single line.
/// </summary>
internal sealed class UsageChecklist
{
    private sealed class Group
    {
        public required CheckBox Asset { get; init; }
        public List<(SafetySystem Unit, CheckBox Box)> Units { get; } = new();
    }
    private readonly List<Group> _groups = new();
    private bool _syncing;

    public UsageChecklist(Panel host, IEnumerable<SafetyGate> gates, IEnumerable<SafetySystem> units, IReadOnlySet<string> selected)
    {
        host.Children.Clear();
        var byGate = units.GroupBy(u => u.GateId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var gate in gates.OrderBy(g => g.Category).ThenBy(g => SortKey(g.AssetNumber)).ThenBy(g => g.AssetNumber))
        {
            if (!byGate.TryGetValue(gate.Id, out var gateUnits) || gateUnits.Count == 0) continue;
            gateUnits = gateUnits.OrderBy(u => SortKey(u.UnitNumber)).ThenBy(u => u.UnitDisplay).ToList();
            bool single = gateUnits.Count == 1 && gateUnits[0].IsWholeAsset;
            var asset = new CheckBox { Content = new TextBlock { Text = $"{gate.AssetDisplay} · {gate.Category}", FontWeight = FontWeights.SemiBold }, Margin = new Thickness(0, 6, 0, 2) };
            var group = new Group { Asset = asset };
            host.Children.Add(asset);
            foreach (var unit in gateUnits)
            {
                var box = new CheckBox { Content = unit.UnitDisplay, IsChecked = selected.Contains(unit.Id), Margin = new Thickness(28, 3, 0, 3) };
                if (single) box.Visibility = Visibility.Collapsed;
                box.Checked += (_, _) => Sync(group); box.Unchecked += (_, _) => Sync(group);
                group.Units.Add((unit, box));
                host.Children.Add(box);
            }
            asset.Click += (_, _) =>
            {
                bool target = group.Units.Any(u => u.Box.IsChecked != true);
                _syncing = true; foreach (var (_, box) in group.Units) box.IsChecked = target; _syncing = false;
                Sync(group);
            };
            _groups.Add(group); Sync(group);
        }
        if (_groups.Count == 0) host.Children.Add(new TextBlock { Text = "Add assets and units first.", Margin = new Thickness(0, 6, 0, 6) });
    }

    private void Sync(Group group)
    {
        if (_syncing) return;
        int ticked = group.Units.Count(u => u.Box.IsChecked == true);
        group.Asset.IsChecked = ticked == 0 ? false : ticked == group.Units.Count ? true : null;
    }

    public List<string> SelectedUnitIds => _groups.SelectMany(g => g.Units).Where(u => u.Box.IsChecked == true).Select(u => u.Unit.Id).ToList();

    private static int SortKey(string? value) => int.TryParse(value, out int number) ? number : int.MaxValue;
}
