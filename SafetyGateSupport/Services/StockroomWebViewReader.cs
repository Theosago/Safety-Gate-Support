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
    public static Task<StockroomService.Snapshot> FetchAsync(string url, CancellationToken token) => ReadSearchAsync(url, "Safe2Cross", false, token);
    public static Task<StockroomService.Snapshot> SearchAsync(string url, string term, CancellationToken token)
    {
        if (!StockroomService.IsItemId(term)) throw new InvalidOperationException("Enter a valid Item ID.");
        return ReadSearchAsync(url, term, true, token);
    }
    private static async Task<StockroomService.Snapshot> ReadSearchAsync(string url, string term, bool itemLookup, CancellationToken token)
    {
        await Slot.WaitAsync(token);
        try { return await Application.Current.Dispatcher.InvokeAsync(() => ReadAsync(url, term, itemLookup, token)).Task.Unwrap(); }
        finally { Slot.Release(); }
    }
    private static async Task<StockroomService.Snapshot> ReadAsync(string url, string term, bool itemLookup, CancellationToken token)
    {
        LastSearchDiagnostics = "";
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
                serverSearch |= state.RootElement.GetProperty("specific").GetBoolean();
                serverConfirmed = state.RootElement.GetProperty("applied").GetString()?.Equals(term, StringComparison.OrdinalIgnoreCase) == true;
                if (!searched || serverSearch && !serverConfirmed && DateTime.UtcNow - lastAttempt > TimeSpan.FromMilliseconds(1500))
                {
                    using var probe = JsonDocument.Parse(await Evaluate(StockroomBrowserReader.FindSearch));
                    if (probe.RootElement.GetProperty("found").GetBoolean())
                    {
                        baseline ??= await Evaluate(StockroomBrowserReader.ReadTables);
                        await core.ExecuteScriptAsync("(()=>{const e=globalThis.__safetyGateStockroomInput;e.focus();e.select();if(e.form)e.form.target='_self';})()").WaitAsync(deadline.Token);
                        await core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = term })).WaitAsync(deadline.Token);
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
                var parsed = StockroomService.Parse(html);
                var doc = new HtmlAgilityPack.HtmlDocument(); doc.LoadHtml(html);
                bool highlighted = doc.DocumentNode.Descendants("mark").Any(n => System.Net.WebUtility.HtmlDecode(n.InnerText).Trim().Equals(term, StringComparison.OrdinalIgnoreCase));
                bool changed = baseline is not null && html != baseline;
                int exactMatches = parsed.Rows.Count(r => StockroomService.StockKey(r.Stock) == StockroomService.StockKey(term));
                LastSearchDiagnostics += $"; rows={parsed.Rows.Count}; exactMatches={exactMatches}; tableChanged={changed}; highlighted={highlighted}; itemLookup={itemLookup}";
                stable = html == previous ? stable + 1 : 0; previous = html;
                // Item badges need not be highlighted. Require a changed, stable result table
                // and the server acknowledgement instead; the import layer enforces a unique exact ID.
                bool ready = serverSearch ? serverConfirmed && (itemLookup ? changed : highlighted || parsed.Rows.Count == 0 && changed) : changed;
                if (searched && stable >= 3 && ready && (serverSearch || DateTime.UtcNow - activity > TimeSpan.FromMilliseconds(750)))
                    return parsed with { Reader = "WebView2: " + term + " search" };
            }
        }
        catch (WebView2RuntimeNotFoundException error) { throw new InvalidOperationException("Microsoft Edge WebView2 Runtime is missing on this PC. Install Microsoft's Evergreen WebView2 Runtime to enable stockroom search.", error); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(serverSearch && !serverConfirmed ? "The stockroom server did not confirm Searching: " + term + " within 35 seconds. Unfiltered inventory was rejected." : "The embedded stockroom search did not finish within 35 seconds. " + LastSearchDiagnostics); }
        finally { view.Dispose(); window.Content = null; window.Close(); }
    }
}
