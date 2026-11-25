using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace SafetyGateSupport;

/// <summary>
/// One picker for choosing which parts or manuals a unit uses: search, tick, and apply to this unit
/// or to every unit on its asset.
/// </summary>
public sealed class AssignmentPickerWindow : Window
{
    public sealed record Option(string Id, string Label, string Detail);
    public sealed record Result(IReadOnlyList<string> SelectedIds, bool WholeAsset);

    private readonly List<(Option Option, CheckBox Box)> _rows = new();
    private readonly TextBlock _count = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _wholeAsset = new() { Margin = new Thickness(0, 6, 0, 0) };
    private Result? _result;

    private AssignmentPickerWindow(string title, string subtitle, IEnumerable<Option> options, IReadOnlySet<string> selected, string unitLabel, string assetLabel, int unitsOnAsset)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = title; Width = 640; Height = 720; MinWidth = 460; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;

        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 16) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold });
        var sub = new TextBlock { Text = subtitle, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        header.Children.Add(sub);
        var search = new TextBox { MinHeight = 38, ToolTip = "Search by name or ID" };
        AutomationProperties.SetName(search, "Search");
        header.Children.Add(search);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (unitsOnAsset > 1)
        {
            var scope = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var thisUnit = new RadioButton { Content = $"Only {unitLabel}", IsChecked = true };
            _wholeAsset.Content = $"Every unit on {assetLabel} ({unitsOnAsset} units) — replaces their lists";
            scope.Children.Add(new TextBlock { Text = "Apply to", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            scope.Children.Add(thisUnit); scope.Children.Add(_wholeAsset);
            footer.Children.Add(scope);
        }
        var buttons = new DockPanel();
        var save = new Button { Content = "Save", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) }; save.SetResourceReference(StyleProperty, "PrimaryButton");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var right = new StackPanel { Orientation = Orientation.Horizontal }; right.Children.Add(cancel); right.Children.Add(save);
        DockPanel.SetDock(right, Dock.Right); buttons.Children.Add(right);
        _count.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted"); buttons.Children.Add(_count);
        footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var list = new StackPanel();
        foreach (var option in options)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = option.Label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (option.Detail.Length > 0)
            {
                var detail = new TextBlock { Text = option.Detail, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                detail.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
                text.Children.Add(detail);
            }
            var box = new CheckBox { Content = text, IsChecked = selected.Contains(option.Id), Padding = new Thickness(0), Margin = new Thickness(4, 7, 4, 7), VerticalContentAlignment = VerticalAlignment.Top };
            box.Checked += (_, _) => UpdateCount(); box.Unchecked += (_, _) => UpdateCount();
            _rows.Add((option, box)); list.Children.Add(box);
        }
        if (_rows.Count == 0) list.Children.Add(new TextBlock { Text = "Nothing to choose from yet.", Margin = new Thickness(4, 12, 4, 0) });
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 10, 0, 0),
            Child = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        frame.SetResourceReference(Border.BorderBrushProperty, "Line"); frame.SetResourceReference(Border.BackgroundProperty, "Surface");
        root.Children.Add(frame);

        search.TextChanged += (_, _) =>
        {
            string query = search.Text.Trim();
            foreach (var (option, box) in _rows)
                box.Visibility = query.Length == 0 || (option.Label + " " + option.Detail).Contains(query, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        };
        save.Click += (_, _) => { _result = new(_rows.Where(r => r.Box.IsChecked == true).Select(r => r.Option.Id).ToList(), _wholeAsset.IsChecked == true); DialogResult = true; };
        Content = root;
        UpdateCount();
        Loaded += (_, _) => search.Focus();
    }

    private void UpdateCount() => _count.Text = $"{_rows.Count(r => r.Box.IsChecked == true)} selected";

    public static Result? Show(Window owner, string title, string subtitle, IEnumerable<Option> options, IEnumerable<string> selected,
        string unitLabel, string assetLabel, int unitsOnAsset)
    {
        var window = new AssignmentPickerWindow(title, subtitle, options, selected.ToHashSet(StringComparer.OrdinalIgnoreCase), unitLabel, assetLabel, unitsOnAsset) { Owner = owner };
        return window.ShowDialog() == true ? window._result : null;
    }
}
