using System.Windows;
using System.Windows.Controls;
using SafetyGateSupport.Models;

namespace SafetyGateSupport;

public partial class DrawingUnitsWindow : Window
{
    private readonly List<(SafetySystem Unit, CheckBox Option)> _choices = [];

    internal IReadOnlyList<string> UnitIds { get; private set; } = [];

    internal DrawingUnitsWindow(SafetyGate gate, IReadOnlyList<SafetySystem> units, string files,
        IEnumerable<string> selectedIds, string action, bool replacing = false)
    {
        InitializeComponent();
        AssetText.Text = gate.AssetDisplay;
        FilesText.Text = files;
        SaveButton.Content = action;
        var selected = selectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (replacing) SelectionHeading.Text = "Apply this revision to";
        foreach (var unit in units)
        {
            string label = string.IsNullOrWhiteSpace(unit.DisplayName) ? unit.UnitDisplay : $"{unit.UnitDisplay} · {unit.DisplayName}";
            bool linked = selected.Contains(unit.Id);
            bool required = replacing && linked;
            var option = new CheckBox
            {
                Content = new TextBlock { Text = required ? label + " (already linked)" : label, TextWrapping = TextWrapping.Wrap },
                IsChecked = linked,
                IsEnabled = !required,
                Margin = new Thickness(2, 6, 2, 6)
            };
            option.Click += (_, _) => RefreshSelection();
            _choices.Add((unit, option));
            UnitOptions.Children.Add(option);
        }
        SelectAllBox.Visibility = _choices.Count > 1 && _choices.Any(choice => choice.Option.IsEnabled)
            ? Visibility.Visible : Visibility.Collapsed;
        RefreshSelection();
    }

    internal static IReadOnlyList<string>? PickForNewPair(Window owner, SafetyGate gate, SafetySystem unit,
        IReadOnlyList<SafetySystem> eligible, IEnumerable<string> files)
    {
        if (eligible.Count == 0) throw new InvalidOperationException("Add a unit to this asset before uploading a drawing.");
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
        foreach (var choice in _choices.Where(choice => choice.Option.IsEnabled))
            choice.Option.IsChecked = SelectAllBox.IsChecked;
        RefreshSelection();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        UnitIds = _choices.Where(choice => choice.Option.IsChecked == true).Select(choice => choice.Unit.Id).ToArray();
        if (UnitIds.Count > 0) DialogResult = true;
    }
}
