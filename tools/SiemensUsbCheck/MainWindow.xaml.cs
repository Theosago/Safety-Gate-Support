using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SiemensUsbCheck.Services;

namespace SiemensUsbCheck;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _operationCancellation;
    private bool _isRefreshing;
    private bool _closeWhenFinished;

    public MainWindow() => InitializeComponent();

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        if (_isRefreshing || _operationCancellation is not null)
            return;

        var previous = DeviceCombo.SelectedItem as UsbColumnPort;
        _isRefreshing = true;
        UpdateControls();
        DetectionText.Text = "Checking connected devices…";
        try
        {
            // Registry and PnP enumeration only: detection never opens a port.
            var ports = await Task.Run(WindowsColumnConnection.DetectPorts);
            DeviceCombo.ItemsSource = ports;
            DeviceCombo.SelectedItem = previous is null ? null : ports.FirstOrDefault(port =>
                string.Equals(port.PortName, previous.PortName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(port.DeviceInstanceId, previous.DeviceInstanceId, StringComparison.OrdinalIgnoreCase));
            if (DeviceCombo.SelectedItem is null && ports.Count == 1)
                DeviceCombo.SelectedItem = ports[0];

            DetectionText.Text = ports.Count switch
            {
                0 => "No supported column detected. Check the data cable and Windows driver, then click Refresh. Bootloader devices are excluded.",
                1 => "One supported USB device found. Detection has not opened it.",
                _ => $"{ports.Count} supported USB devices found. Select the spare column before reading."
            };
        }
        catch (Exception error)
        {
            DeviceCombo.ItemsSource = null;
            DetectionText.Text = "Windows could not list the connected USB columns.";
            ResultText.Text = error.Message;
        }
        finally
        {
            _isRefreshing = false;
            UpdateControls();
        }
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged can fire while InitializeComponent is still running.
        if (ReadButton is not null)
            UpdateControls();
    }

    private async void ReadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing || _operationCancellation is not null || DeviceCombo.SelectedItem is not UsbColumnPort port)
            return;

        var destination = new SaveFileDialog
        {
            Title = "Save Siemens USB diagnostic report",
            Filter = "ZIP diagnostic report (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            CheckPathExists = true,
            OverwritePrompt = true,
            FileName = $"Siemens-8WD46-USB-check-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        try
        {
            if (destination.ShowDialog(this) != true)
                return;
        }
        catch (Exception error)
        {
            ResultText.Text = $"Windows could not choose a report destination:\n{error.Message}";
            StatusText.Text = "No device was opened.";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        UpdateControls();
        ResultText.Text = $"Reading {port.DisplayName}\n\nReport destination:\n{destination.FileName}";
        StatusText.Text = "Opening the selected spare column…";
        var progress = new Progress<string>(message => StatusText.Text = message);

        try
        {
            var result = await Task.Run(() => UsbCheckSession.Run(
                port, destination.FileName, cancellation.Token, ((IProgress<string>)progress).Report));
            ResultText.Text = $"{result.Summary}\n\nReport:\n{destination.FileName}";
            StatusText.Text = result.Success ? "USB read completed. Report saved." : "USB check finished. Review the report for details.";
        }
        catch (OperationCanceledException)
        {
            ResultText.Text = "The USB check was canceled. A complete report could not be confirmed.";
            StatusText.Text = "Canceled.";
        }
        catch (Exception error)
        {
            ResultText.Text = $"The USB check could not finish:\n{error.Message}\n\nA complete report could not be confirmed. Check the destination and reconnect the spare column before retrying.";
            StatusText.Text = "USB check failed.";
        }
        finally
        {
            _operationCancellation = null;
            UpdateControls();
            if (_closeWhenFinished)
                Close();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Canceling… Waiting for the current driver call and report cleanup.";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_operationCancellation is not null)
        {
            e.Cancel = true;
            _closeWhenFinished = true;
            _operationCancellation.Cancel();
            CancelButton.IsEnabled = false;
            StatusText.Text = "Canceling before closing… Waiting for report cleanup.";
        }
        base.OnClosing(e);
    }

    private void UpdateControls()
    {
        bool running = _operationCancellation is not null;
        DeviceCombo.IsEnabled = !running && !_isRefreshing;
        RefreshButton.IsEnabled = !running && !_isRefreshing;
        ReadButton.IsEnabled = !running && !_isRefreshing && DeviceCombo.SelectedItem is UsbColumnPort;
        CancelButton.IsEnabled = running && !_operationCancellation!.IsCancellationRequested;
    }
}
