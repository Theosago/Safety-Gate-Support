using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SafetyGateSupport.Hokuyo;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class UamDiagnosticsWindow : Window
{
    private readonly bool _preview;
    private bool _detecting;
    private bool _closeAfter;
    private CancellationTokenSource? _operation;
    public UamDiagnosticsWindow(DataService data, SafetyGate gate, SafetySystem unit)
    {
        InitializeComponent(); _preview = data.IsHomePreview;
        ContextText.Text = $"{gate.Display} / {unit.UnitDisplay} • confirm the device label is UAM-05LP-T301";
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        MaxHeight = SystemParameters.WorkArea.Height;
        if (_preview) { DetectionText.Text = "Home preview: USB operations are disabled. Explore Look up a display code without hardware."; UpdateControls(); return; }
        await DetectAsync();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await DetectAsync();
    private async Task DetectAsync()
    {
        if (_preview || _detecting || _operation is not null) return;
        var previous = DeviceCombo.SelectedItem as UamUsbPort;
        _detecting = true; UpdateControls();
        try
        {
            var devices = await Task.Run(WindowsUamConnection.DetectPorts);
            DeviceCombo.ItemsSource = devices;
            DeviceCombo.SelectedItem = devices.FirstOrDefault(p => p.DeviceInstanceId == previous?.DeviceInstanceId);
            if (devices.Count == 1) DeviceCombo.SelectedIndex = 0;
            DetectionText.Text = devices.Count == 0 ? "No supported SLS USB serial device is present. Check external power, the data cable, and the driver supplied with Project Designer. Detection does not open a port." : $"{devices.Count} supported USB serial device(s) detected. Select the intended test scanner. No USB connection has been opened.";
        }
        catch (Exception error) { DetectionText.Text = "Device detection failed: " + error.Message; }
        finally { _detecting = false; UpdateControls(); }
    }
    private void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ReadButton is null) return;
        ResultText.Text = "Not checked for this USB selection. Detection does not establish scanner health.";
        UpdateControls();
    }
    private async void Read_Click(object sender, RoutedEventArgs e)
    {
        if (_preview || _operation is not null || _detecting || DeviceCombo.SelectedItem is not UamUsbPort port) return;
        string reportPath;
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SafetyGateSupport", "HokuyoReports"); Directory.CreateDirectory(folder);
            var save = new SaveFileDialog { Title = "Save UAM USB status report", Filter = "Diagnostic report (*.zip)|*.zip", DefaultExt = ".zip", AddExtension = true, InitialDirectory = folder, FileName = $"Hokuyo-UAM-USB-check-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}.zip" };
            if (save.ShowDialog(this) != true) return;
            reportPath = save.FileName;
            if (File.Exists(reportPath)) throw new IOException("Choose a new report filename to preserve previous evidence.");
            if (MessageBox.Show(this, $"Read identity and status from {port.DisplayName}?\n\nUse the test scanner isolated from operating equipment. Opening the serial port asserts DTR/RTS. This first hardware check sends only the fixed read requests; no reset, configuration, or mode-changing commands.\n\nKeep the generated report for comparison with Project Designer.", "Read UAM test scanner", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        catch (Exception error) { ResultText.Text = error.Message; return; }
        using var cancel = new CancellationTokenSource(); _operation = cancel; UpdateControls();
        ResultText.Text = "Reading selected test scanner. Status is unverified until a complete response is checked.";
        var progress = new Progress<string>(s => StatusText.Text = s);
        try
        {
            var result = await Task.Run(() => UamReadSession.Run(port, reportPath, ct => WindowsUamConnection.Open(port, ct), cancel.Token, ((IProgress<string>)progress).Report));
            ResultText.Text = result.Summary + "\n\nReport:\n" + result.ReportPath;
            StatusText.Text = result.Success ? "USB reading completed. Physical operation remains unverified." : "USB status could not be fully verified. Keep the report.";
        }
        catch (Exception error) { ResultText.Text = "USB status not verified: " + error.Message; StatusText.Text = "Not verified."; }
        finally { _operation = null; UpdateControls(); if (_closeAfter) Close(); }
    }
    private void Lookup_Click(object sender, RoutedEventArgs e)
    {
        try { var error = UamErrorCatalog.Lookup(CodeBox.Text); LookupTitleText.Text = error.Code + " — " + error.Title; LookupBodyText.Text = error.Checks; LookupSourceText.Text = error.Source + "\nManual lookup; not a current USB status reading."; }
        catch (FormatException error) { LookupTitleText.Text = "Check the display code"; LookupBodyText.Text = error.Message; LookupSourceText.Text = ""; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _operation?.Cancel(); CancelButton.IsEnabled = false; StatusText.Text = "Canceling… waiting for the driver and report."; }
    private void UpdateControls()
    {
        bool idle = !_detecting && _operation is null;
        DeviceCombo.IsEnabled = !_preview && idle; RefreshButton.IsEnabled = !_preview && idle;
        ReadButton.IsEnabled = !_preview && idle && DeviceCombo.SelectedItem is UamUsbPort;
        CancelButton.IsEnabled = _operation is not null && !_operation.IsCancellationRequested;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_operation is not null) { e.Cancel = true; _closeAfter = true; _operation.Cancel(); }
        base.OnClosing(e);
    }
}
