using System.IO;
using System.Windows;
using System.Windows.Controls;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class TroubleshootWindow : Window
{
    private readonly DataService _data;
    private readonly SafetyGate _gate;
    private readonly SafetySystem _unit;
    private readonly List<ResourceDefinition> _resources;
    private readonly List<TroubleshootingFlow> _flows;
    private TroubleshootingFlow? _currentFlow;
    private TroubleshootingNode? _currentNode;
    private readonly Stack<string> _history = new();

    public TroubleshootWindow(DataService data, SafetyGate gate, SafetySystem unit, IEnumerable<ResourceDefinition> resources, IEnumerable<TroubleshootingFlow> flows, string? initialFlowId = null, string? initialNodeId = null)
    {
        InitializeComponent();
        _data = data; _gate = gate; _unit = unit; _resources = resources.ToList();
        _flows = flows.Where(f => f.AppliesTo(gate, unit)).OrderBy(f => f.Title).ToList();
        SystemText.Text = $"{gate.Display} - {unit.UnitDisplay}   •   {gate.Category}" + (string.IsNullOrWhiteSpace(gate.Location) ? "" : $"   •   {gate.Location}");
        FlowList.ItemsSource = _flows;
        StatusText.Text = _flows.Count == 0 ? "No guided troubleshooting flows are configured for this system yet." : "Choose the problem that best matches what you are seeing.";
        if (initialFlowId is not null)
        {
            var initial = _flows.FirstOrDefault(f => f.Id.Equals(initialFlowId, StringComparison.OrdinalIgnoreCase));
            if (initial is not null)
            {
                FlowList.SelectedItem = initial;
                if (initialNodeId is not null && initial.Nodes.FirstOrDefault(n => n.Id == initialNodeId) is { } node) ShowNode(node);
            }
            else StatusText.Text = "No matching guide is assigned to this unit. Choose an applicable guide or open unit files.";
        }
    }

    private void FlowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentFlow = FlowList.SelectedItem as TroubleshootingFlow;
        _history.Clear();
        if (_currentFlow is null) { ShowEmpty(); return; }
        var node = _currentFlow.Nodes.FirstOrDefault(n => n.Id == _currentFlow.StartNodeId) ?? _currentFlow.Nodes.FirstOrDefault();
        if (node is null) { ShowEmpty(); StatusText.Text = "This troubleshooting flow has no steps yet."; return; }
        ShowNode(node);
    }

    private void ShowEmpty()
    {
        _currentNode = null; StepPromptText.Text = "Select a problem to begin."; StepBodyText.Text = ""; ChoicePanel.Children.Clear(); LinkedResourcesPanel.Children.Clear(); LinkedResourcesTitle.Visibility = Visibility.Collapsed; BackButton.IsEnabled = false;
    }

    private void ShowNode(TroubleshootingNode node)
    {
        _currentNode = node;
        StepPromptText.Text = node.Prompt;
        StepBodyText.Text = node.Body;
        ChoicePanel.Children.Clear();
        LinkedResourcesPanel.Children.Clear();
        BackButton.IsEnabled = _history.Count > 0;

        foreach (var choice in node.Choices)
        {
            var button = new Button { Content = new TextBlock { Text = choice.Text, TextWrapping = TextWrapping.Wrap }, Tag = choice.NextNodeId, HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 42 };
            button.Click += Choice_Click;
            ChoicePanel.Children.Add(button);
        }
        if ((node.IsTerminal || node.Choices.Count == 0) && string.IsNullOrEmpty(node.Action))
        {
            ChoicePanel.Children.Add(new TextBlock { Text = "Follow the documented checks before returning equipment to service.", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(4, 8, 4, 0) });
        }

        if (node.Action == "SignalColumn")
        {
            var tool = new Button { Content = "Check / configure signal column…", MinHeight = 42 };
            tool.Click += async (_, _) => await SignalColumnService.OpenAsync(this, _data, _gate, _unit, _resources);
            ChoicePanel.Children.Add(tool);
        }
        if (node.Action == "UamDiagnostics")
        {
            var tool = new Button { Content = "Check UAM scanner…", MinHeight = 42 };
            tool.Click += (_, _) => new UamDiagnosticsWindow(_data, _gate, _unit) { Owner = this }.ShowDialog();
            ChoicePanel.Children.Add(tool);
        }
        if (node.Action == "Library")
        {
            var library = new Button { Content = "Open support library…", MinHeight = 42 };
            library.Click += (_, _) => new SupportLibraryWindow(_data, _gate, _unit) { Owner = this }.ShowDialog();
            ChoicePanel.Children.Add(library);
        }
        if (node.Action == "Files")
        {
            var files = new Button { Content = "Open this unit’s files and drawings", MinHeight = 42 };
            files.Click += (_, _) => OpenUnitFiles();
            ChoicePanel.Children.Add(files);
        }
        if (node.Action.StartsWith("Guide:", StringComparison.Ordinal))
        {
            var next = _flows.FirstOrDefault(f => f.Id == node.Action[6..]);
            var tool = new Button { Content = new TextBlock { Text = next is null ? "Guide unavailable — open unit files" : "Continue to " + next.Title, TextWrapping = TextWrapping.Wrap }, MinHeight = 42, HorizontalContentAlignment = HorizontalAlignment.Left };
            tool.Click += (_, _) => { if (next is not null) FlowList.SelectedItem = next; else OpenUnitFiles(); };
            ChoicePanel.Children.Add(tool);
        }
        foreach (var id in node.ResourceIds.Distinct())
        {
            var resource = _resources.FirstOrDefault(r => r.Id == id && r.IsVisible && r.GateId == _gate.Id && r.AppliesTo(_unit.Id));
            if (resource is null) continue;
            var button = new Button { Content = resource.Label, Tag = resource.Id };
            button.Click += Resource_Click;
            LinkedResourcesPanel.Children.Add(button);
        }
        StepNumberText.Text = $"CHECK {_history.Count + 1}";
        LinkedResourcesTitle.Visibility = LinkedResourcesPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = _currentFlow?.Title ?? "Troubleshooting";
    }

    private void OpenUnitFiles() => new UnitFilesWindow(_data, _gate, _unit, _resources) { Owner = this }.ShowDialog();
    private void UnitFiles_Click(object sender, RoutedEventArgs e) => OpenUnitFiles();

    private void Choice_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFlow is null || _currentNode is null || sender is not Button b || b.Tag is not string nextId || string.IsNullOrWhiteSpace(nextId)) return;
        var next = _currentFlow.Nodes.FirstOrDefault(n => n.Id == nextId);
        if (next is null) { StatusText.Text = $"The next step '{nextId}' could not be found. Fix this flow in Setup Mode."; return; }
        _history.Push(_currentNode.Id); ShowNode(next);
    }

    private void Resource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string id) return;
        var r = _resources.FirstOrDefault(x => x.Id == id); if (r is null) return;
        switch (r.Kind.ToLowerInvariant())
        {
            case "manuals":
                var manuals = _data.ManualsFor(_gate, _unit); if (manuals.Count == 0) { MessageBox.Show("No manuals are linked to this gate/unit."); return; }
                new UnitFilesWindow(_data, _gate, _unit, _resources, "manual") { Owner = this }.ShowDialog(); break;
            case "parts":
                FileLauncherService.Open(_data.LoadSharedContent().StoreroomUrl); break;
            case "url": if (!string.IsNullOrWhiteSpace(r.Target)) FileLauncherService.Open(r.Target); break;
            case "folder": var folder = _data.Resolve(r.Target); Directory.CreateDirectory(folder); FileLauncherService.Open(folder); break;
            default: new ResourceWindow(_data, _gate, _unit, r) { Owner = this }.ShowDialog(); break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFlow is null || _history.Count == 0) return;
        var id = _history.Pop(); var node = _currentFlow.Nodes.FirstOrDefault(n => n.Id == id); if (node is not null) ShowNode(node);
    }
    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFlow is null) return; _history.Clear(); var node = _currentFlow.Nodes.FirstOrDefault(n => n.Id == _currentFlow.StartNodeId) ?? _currentFlow.Nodes.FirstOrDefault(); if (node is not null) ShowNode(node);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
