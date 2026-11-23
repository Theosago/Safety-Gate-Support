using System.Windows;
using System.Windows.Controls;
using SafetyGateSupport.Models;

namespace SafetyGateSupport;

public partial class DrawingUnitsWindow : Window
{
    private readonly List<(SafetySystem Unit, CheckBox Option)> _choices = [];

    internal IReadOnlyList<string> UnitIds { get; private set; } = [];

    internal DrawingUnitsWindow(SafetyGate gate, IReadOnlyList<SafetySystem> units, string files,
        IEnumerable<string> selectedIds, string action)
    {
        InitializeComponent();
        AssetText.Text = gate.AssetDisplay;
        FilesText.Text = files;
        SaveButton.Content = action;
        SelectAllBox.Visibility = units.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var selected = selectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in units)
        {
            string label = string.IsNullOrWhiteSpace(unit.DisplayName) ? unit.UnitDisplay : $"{unit.UnitDisplay} · {unit.DisplayName}";
            var option = new CheckBox
            {
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                IsChecked = selected.Contains(unit.Id),
                Margin = new Thickness(2, 6, 2, 6)
            };
            option.Click += (_, _) => RefreshSelection();
            _choices.Add((unit, option));
            UnitOptions.Children.Add(option);
        }
        RefreshSelection();
    }

    internal static IReadOnlyList<string>? PickForNewPair(Window owner, SafetyGate gate, SafetySystem unit,
        IReadOnlyList<SafetySystem> eligible, IEnumerable<string> files)
    {
        if (eligible.Count == 0) throw new InvalidOperationException("Add a unit to this asset before uploading a drawing.");
        if (eligible.Count == 1 && eligible[0].Id == unit.Id) return new[] { unit.Id };
        var picker = new DrawingUnitsWindow(gate, eligible, string.Join(Environment.NewLine, files.Select(System.IO.Path.GetFileName)),
            new[] { unit.Id }, "Add drawing") { Owner = owner };
        return picker.ShowDialog() == true ? picker.UnitIds : null;
    }

    internal static string UnitNames(IEnumerable<SafetySystem> units, IReadOnlyCollection<string> ids) =>
        string.Join(", ", units.Where(unit => ids.Count == 0 || ids.Contains(unit.Id, StringComparer.OrdinalIgnoreCase))
            .Select(unit => unit.UnitDisplay));

    private void RefreshSelection()
    {
        int selected = _choices.Count(choice => choice.Option.IsChecked == true);
        SaveButton.IsEnabled = selected > 0;
        SelectAllBox.IsChecked = selected == _choices.Count;
        SelectionText.Text = selected == 1 ? "1 unit selected" : $"{selected} units selected";
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var choice in _choices) choice.Option.IsChecked = SelectAllBox.IsChecked;
        RefreshSelection();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        UnitIds = _choices.Where(choice => choice.Option.IsChecked == true).Select(choice => choice.Unit.Id).ToArray();
        if (UnitIds.Count > 0) DialogResult = true;
    }
}
