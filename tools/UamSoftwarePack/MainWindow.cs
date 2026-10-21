using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace UamSoftwarePack;

public sealed class MainWindow : Window
{
    private readonly Button _choose = new() { Content = "Choose UAM Project Designer ZIP…", Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 16, 0, 8) };
    private readonly Button _cancel = new() { Content = "Cancel", IsEnabled = false, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button _folder = new() { Content = "Open output folder", IsEnabled = false, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox _status = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 170 };
    private CancellationTokenSource? _operation;
    private string? _output;
    private bool _closeAfter;

    public MainWindow()
    {
        Title = "UAM software analysis pack — v0.1.0"; Width = 730; Height = 510; MinWidth = 600; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Prepare a smaller UAM software upload", FontSize = 24, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Select the original UAM Project Designer ZIP. This creates an analysis ZIP under 30 MiB containing selected application files and an inventory. It only reads the software archive; no scanner connection or installation is required.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(_choose); panel.Children.Add(_cancel); panel.Children.Add(_folder); panel.Children.Add(_status);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _status.Text = "Ready. Nothing has been opened. Your original ZIP is preserved.";
        _choose.Click += Choose_Click; _cancel.Click += (_, _) => _operation?.Cancel();
        _folder.Click += (_, _) => { if (_output is not null) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_output}\"") { UseShellExecute = true }); };
    }

    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        var source = new OpenFileDialog { Title = "Select UAM Project Designer.zip", Filter = "Software ZIP (*.zip)|*.zip", CheckFileExists = true };
        if (source.ShowDialog(this) != true) return;
        var target = new SaveFileDialog { Title = "Save smaller upload", Filter = "Analysis ZIP (*.zip)|*.zip", DefaultExt = ".zip", AddExtension = true, InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), FileName = $"UAM-analysis-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
        if (target.ShowDialog(this) != true) return;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        _choose.IsEnabled = false; _cancel.IsEnabled = true; _folder.IsEnabled = false;
        var progress = new Progress<string>(name => _status.Text = "Collecting for static inspection:\n" + name);
        try
        {
            var result = await Task.Run(() => SoftwarePack.Create(source.FileName, target.FileName, cancellation.Token, ((IProgress<string>)progress).Report));
            _output = result.Path; _folder.IsEnabled = true;
            _status.Text = $"DONE — upload this file in the chat:\n\n{result.Path}\n\n{result.Bytes / (1024d * 1024d):0.0} MiB; {result.IncludedFiles} selected files; {result.TotalFiles} files inventoried.\n\nNo scanner status was read. The pack is for investigating USB diagnostics.";
        }
        catch (OperationCanceledException) { _status.Text = "Canceled. The unfinished pack was discarded. Your source ZIP is preserved."; }
        catch (Exception error) { _status.Text = "No complete pack was confirmed:\n" + error.Message; }
        finally { _operation = null; _choose.IsEnabled = true; _cancel.IsEnabled = false; if (_closeAfter) Close(); }
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_operation is not null) { e.Cancel = true; _closeAfter = true; _operation.Cancel(); _status.Text = "Canceling before closing…"; }
        base.OnClosing(e);
    }
}
