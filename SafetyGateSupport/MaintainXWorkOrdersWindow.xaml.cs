using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using SafetyGateSupport.Models;
using SafetyGateSupport.Services;

namespace SafetyGateSupport;

public partial class MaintainXWorkOrdersWindow : Window
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        MaxResponseContentBufferSize = 4 * 1024 * 1024
    };
    private static readonly MaintainXWorkOrderClient Client = new(Http);
    private readonly DataService _data;
    private readonly string _unitId;
    private CancellationTokenSource? _operation;
    private MaintainXAsset? _link;
    private string? _savedKey;
    private bool _loaded;
    private bool _closed;

    internal MaintainXWorkOrdersWindow(DataService data, SafetySystem unit)
    {
        InitializeComponent();
        _data = data;
        _unitId = unit.Id;
        UnitText.Text = $"{unit.AssetDisplay} / {unit.UnitDisplay}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _operation?.Cancel();
        base.OnClosed(e);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await RunAsync(async token =>
        {
            _link = await Task.Run(() => _data.LoadMaintainXLink(_unitId), token);
            token.ThrowIfCancellationRequested();
            UpdateLink();
            SettingsPanel.IsExpanded = true;
            try { _savedKey = MaintainXCredentials.Load(); }
            catch (CryptographicException)
            {
                StatusText.Text = "Enter your MaintainX API key again to replace the saved key.";
                return;
            }
            if (_link is null || _savedKey is null)
            {
                StatusText.Text = _link is null ? "Connect this unit to its MaintainX asset." : "Enter your MaintainX API key to view work orders.";
                return;
            }

            SettingsPanel.IsExpanded = false;
            await LoadOrdersAsync(_link.Id, _savedKey, token);
        });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_link is { } link && _savedKey is { } key)
            await RunAsync(token => LoadOrdersAsync(link.Id, key, token));
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(AssetIdBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long assetId) || assetId <= 0)
        {
            StatusText.Text = "Enter the numeric asset ID from MaintainX.";
            AssetIdBox.Focus();
            return;
        }

        string enteredKey = ApiKeyBox.Password.Trim();
        string? key = enteredKey.Length == 0 ? _savedKey : enteredKey;
        if (string.IsNullOrEmpty(key) || key.Any(character => character < '!' || character > '~'))
        {
            StatusText.Text = "Enter a valid MaintainX API key.";
            ApiKeyBox.Focus();
            return;
        }

        await RunAsync(async token =>
        {
            var asset = await Client.GetAssetAsync(assetId, key, token);
            token.ThrowIfCancellationRequested();
            if (_link?.Id != asset.Id && MessageBox.Show(this,
                $"Link {UnitText.Text} to {asset.Name} (MaintainX asset {asset.Id})?",
                "Confirm unit link", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                StatusText.Text = "Review the asset ID and try again.";
                return;
            }

            var orders = await Client.ListCurrentAsync(asset.Id, key, token);
            token.ThrowIfCancellationRequested();
            if (_link?.Id != asset.Id)
                await Task.Run(() => _data.SaveMaintainXLink(_unitId, _link, asset), token);
            token.ThrowIfCancellationRequested();
            _link = asset;
            UpdateLink();
            if (enteredKey.Length > 0)
            {
                MaintainXCredentials.Save(key);
                _savedKey = key;
                ApiKeyBox.Clear();
            }
            SettingsPanel.IsExpanded = false;
            ShowOrders(orders);
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (_closed || _operation is not null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _operation = timeout;
        RefreshButton.IsEnabled = SettingsPanel.IsEnabled = false;
        OrdersGrid.ItemsSource = null;
        StatusText.Text = "Loading MaintainX work orders…";
        try { await operation(timeout.Token); }
        catch (OperationCanceledException) { if (!_closed) StatusText.Text = "The request timed out. Try again."; }
        catch (MaintainXRequestException ex) { if (!_closed) StatusText.Text = ex.Message; }
        catch (InvalidOperationException ex) { if (!_closed) StatusText.Text = ex.Message; }
        catch (HttpRequestException) { if (!_closed) StatusText.Text = "Could not reach MaintainX. Check your connection and try again."; }
        catch (JsonException) { if (!_closed) StatusText.Text = "The work-order response or saved asset link could not be read."; }
        catch (InvalidDataException ex) { if (!_closed) StatusText.Text = ex.Message; }
        catch (IOException) { if (!_closed) StatusText.Text = "Could not read or save the MaintainX settings. Check that the shared drive is available."; }
        catch (UnauthorizedAccessException) { if (!_closed) StatusText.Text = "Windows could not read or save the settings. Check your shared drive and Windows permissions."; }
        catch (CryptographicException) { if (!_closed) StatusText.Text = "Windows could not protect the saved key. Enter your API key again."; }
        finally
        {
            _operation = null;
            if (!_closed)
            {
                SettingsPanel.IsEnabled = true;
                RefreshButton.IsEnabled = _link is not null && _savedKey is not null;
            }
        }
    }

    private async Task LoadOrdersAsync(long assetId, string key, CancellationToken token)
    {
        var orders = await Client.ListCurrentAsync(assetId, key, token);
        token.ThrowIfCancellationRequested();
        ShowOrders(orders);
    }

    private void ShowOrders(IReadOnlyList<MaintainXWorkOrder> orders)
    {
        OrdersGrid.ItemsSource = orders;
        string count = orders.Count switch
        {
            0 => "No current work orders for this asset.",
            1 => "1 current work order.",
            _ => $"{orders.Count} current work orders."
        };
        StatusText.Text = $"{count} Updated at {DateTime.Now:t}.";
    }

    private void UpdateLink()
    {
        AssetIdBox.Text = _link?.Id.ToString(CultureInfo.InvariantCulture) ?? "";
        LinkText.Text = _link is { } link ? $"MaintainX asset {link.Id} · {link.Name}" : "";
    }
}
