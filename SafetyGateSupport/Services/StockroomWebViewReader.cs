using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
namespace SafetyGateSupport.Services;

/// <summary>Use WebView2's supported in-process API, without launching Edge in remote-debugging mode.</summary>
public static class StockroomWebViewReader
{
    public static string LastSearchDiagnostics { get; private set; } = "";
    private static readonly SemaphoreSlim Slot = new(1);
    public static async Task<StockroomService.Snapshot> FetchAsync(string url, CancellationToken token)
    {
        await Slot.WaitAsync(token);
        try { return await Application.Current.Dispatcher.InvokeAsync(() => ReadAsync(url, token)).Task.Unwrap(); }
        finally { Slot.Release(); }
    }
    private static async Task<StockroomService.Snapshot> ReadAsync(string url, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(35));
        var origin = new Uri(url); var view = new WebView2();
        var window = new Window { Content = view, Width = 1200, Height = 900, Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Title = "Stockroom background reader" };
        bool serverSearch = false, serverConfirmed = false;
        string? navigationError = null; bool loaded = false; DateTime activity = DateTime.UtcNow;
        try
        {
            window.Show();
            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SafetyGateSupport", "StockroomWebView");
            var environment = await CoreWebView2Environment.CreateAsync(null, profile, new CoreWebView2EnvironmentOptions { AllowSingleSignOnUsingOSPrimaryAccount = true }).WaitAsync(deadline.Token);
            await view.EnsureCoreWebView2Async(environment).WaitAsync(deadline.Token);
            var core = view.CoreWebView2; core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
            core.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var next) || next.Scheme != origin.Scheme || next.Host != origin.Host || next.Port != origin.Port)
                { args.Cancel = true; navigationError = "The inventory page redirected outside the selected stockroom site."; }
                loaded = false; activity = DateTime.UtcNow;
            };
            core.NavigationCompleted += (_, args) => { loaded = true; activity = DateTime.UtcNow; if (!args.IsSuccess) navigationError = "Stockroom navigation failed: " + args.WebErrorStatus; };
            core.WebResourceResponseReceived += (_, _) => activity = DateTime.UtcNow;
            await core.AddScriptToExecuteOnDocumentCreatedAsync("window.addEventListener('error',e=>globalThis.__safetyGateScriptError=e.message);").WaitAsync(deadline.Token);
            core.Navigate(url);
            async Task<string> Evaluate(string script) => JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(script).WaitAsync(deadline.Token)) ?? "";
            bool searched = false; string? previous = null, baseline = null; int stable = 0; DateTime lastAttempt = DateTime.MinValue;
            while (true)
            {
                await Task.Delay(250, deadline.Token);
                if (navigationError is not null) throw new IOException(navigationError);
                if (!loaded) continue;
                using var state = JsonDocument.Parse(await Evaluate("(()=>JSON.stringify({specific:!!document.querySelector('.inventory-container input.search-input'),applied:document.querySelector('.search-info .search-terms')?.textContent.trim()||'',value:document.querySelector('input.search-input')?.value||'',error:globalThis.__safetyGateScriptError||''}))()"));
                LastSearchDiagnostics = state.RootElement.ToString();
                serverSearch = state.RootElement.GetProperty("specific").GetBoolean();
                serverConfirmed = state.RootElement.GetProperty("applied").GetString()?.Equals("Safe2Cross", StringComparison.OrdinalIgnoreCase) == true;
                if (!searched || serverSearch && !serverConfirmed && DateTime.UtcNow - lastAttempt > TimeSpan.FromMilliseconds(1500))
                {
                    using var probe = JsonDocument.Parse(await Evaluate(StockroomBrowserReader.FindSearch));
                    if (probe.RootElement.GetProperty("found").GetBoolean())
                    {
                        baseline ??= await Evaluate(StockroomBrowserReader.ReadTables);
                        await core.ExecuteScriptAsync("(()=>{const e=globalThis.__safetyGateStockroomInput;e.focus();e.select();if(e.form)e.form.target='_self';})()").WaitAsync(deadline.Token);
                        await core.CallDevToolsProtocolMethodAsync("Input.insertText", "{\"text\":\"Safe2Cross\"}").WaitAsync(deadline.Token);
                        // Blazor's default binding listens for change, not necessarily input.
                        // Blur commits trusted edits; change also supports server handlers registered after startup.
                        await core.ExecuteScriptAsync("(()=>{const e=globalThis.__safetyGateStockroomInput;e.dispatchEvent(new Event('change',{bubbles:true}));e.blur();})()").WaitAsync(deadline.Token);
                        if (!serverSearch)
                        {
                        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13,\"text\":\"\\r\"}").WaitAsync(deadline.Token);
                        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13}").WaitAsync(deadline.Token);
                        }
                        lastAttempt = DateTime.UtcNow;
                        searched = true; previous = null; stable = 0; activity = DateTime.UtcNow; continue;
                    }
                }
                string html = await Evaluate(StockroomBrowserReader.ReadTables);
                LastSearchDiagnostics += "; highlighted=" + html.Contains("<mark>Safe2Cross</mark>", StringComparison.OrdinalIgnoreCase) + "; rows=" + StockroomService.Parse(html).Rows.Count;
                stable = html == previous ? stable + 1 : 0; previous = html;
                if (stable >= 3 && (serverSearch ? serverConfirmed : html != baseline) && DateTime.UtcNow - activity > TimeSpan.FromMilliseconds(750))
                {
                    if (!searched) throw new IOException("Inventory loaded, but its search field was not found. The initial unfiltered page is not a completed lookup.");
                    var snapshot = StockroomService.Parse(html);
                    // This inventory highlights the applied term in result cells. A changed
                    // search caption alone may precede the server's table update.
                    if (serverSearch && snapshot.Rows.Count > 0 && !html.Contains("<mark>Safe2Cross</mark>", StringComparison.OrdinalIgnoreCase)) continue;
                    return snapshot with { Reader = "WebView2: Safe2Cross search" };
                }
            }
        }
        catch (WebView2RuntimeNotFoundException error) { throw new InvalidOperationException("Microsoft Edge WebView2 Runtime is missing on this PC. Install Microsoft's Evergreen WebView2 Runtime to enable stockroom search.", error); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(serverSearch && !serverConfirmed ? "The stockroom server did not confirm Searching: Safe2Cross within 35 seconds. Unfiltered inventory was rejected." : "The embedded stockroom search did not finish within 35 seconds."); }
        finally { view.Dispose(); window.Content = null; window.Close(); }
    }
}
