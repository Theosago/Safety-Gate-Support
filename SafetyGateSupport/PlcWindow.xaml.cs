using System.IO;
using System.Windows;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class PlcWindow : Window
{
    private readonly DataService _data;
    private readonly SafetyGate _gate;
    private readonly SafetySystem _unit;
    public PlcWindow(DataService data, SafetyGate gate, SafetySystem unit)
    {
        InitializeComponent(); _data = data; _gate = gate; _unit = unit;
        AssetText.Text = $"{gate.AssetDisplay} • PLC";
        ModelText.Text = string.IsNullOrWhiteSpace(gate.PlcModel) ? "Not recorded yet" : gate.PlcModel;
        AddressText.Text = string.IsNullOrWhiteSpace(gate.PlcAddress) ? "Not recorded yet" : gate.PlcAddress;
        NotesText.Text = string.IsNullOrWhiteSpace(gate.PlcNotes) ? "No PLC notes recorded for this asset." : gate.PlcNotes;
    }
    public static ResourceDefinition FilesFor(SafetyGate gate) => !string.IsNullOrWhiteSpace(gate.AssetFolder) ? AssetFileLayout.Create(gate, "PLC") : new()
    {
        Id = $"asset-plc-{gate.Id}", GateId = gate.Id, Label = "PLC files — all asset units", Kind = "ManagedFiles",
        RelativeFolder = $"PLC/{gate.Id}", AllowUserUpload = true, ArchiveMode = "SameName",
        Description = $"PLC files for {gate.AssetDisplay}. Shared by all units of this asset. Previous same-name files are archived when updated."
    };
    private void Files_Click(object sender, RoutedEventArgs e)
    {
        var resource = FilesFor(_gate);
        try { new ResourceWindow(_data, _gate, _unit, resource) { Owner = this }.ShowDialog(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Unable to open this asset’s PLC files. Check shared-drive access and folder permissions.\n\n" + error.Message,
                "PLC files unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
