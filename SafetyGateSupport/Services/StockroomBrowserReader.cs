using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
namespace SafetyGateSupport.Services;

/// <summary>Read an inventory page's actual JavaScript search using an isolated headless browser.</summary>
public static class StockroomBrowserReader
{
    private static readonly SemaphoreSlim BrowserSlot = new(1);
    public static async Task<StockroomService.Snapshot> FetchAsync(string url, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Interactive stockroom search is available on Windows.");
        var uri = new Uri(url); string browser = FindBrowser() ?? throw new InvalidOperationException("The stockroom uses an interactive search. Microsoft Edge or Chrome is needed to read it automatically.");
        await BrowserSlot.WaitAsync(token);
        string profile = Path.Combine(Path.GetTempPath(), "SafetyGateSupport-stockroom-" + Guid.NewGuid().ToString("N"));
        Process? process = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            Directory.CreateDirectory(profile);
            var start = new ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "--headless=new", "--disable-gpu", "--window-size=1600,1000", "--disable-component-update", "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-background-networking", "--disable-sync", "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=0", "--user-data-dir=" + profile, "--auth-server-allowlist=" + uri.Host, "about:blank" }) start.ArgumentList.Add(argument);
            process = Process.Start(start) ?? throw new IOException("The background stockroom reader could not start.");
            // Cancellation runs this synchronously, including when the app is closing.
            // Do not leave an isolated browser running after the main window exits.
            using var killOnCancel = deadline.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
            });
            string portFile = Path.Combine(profile, "DevToolsActivePort");
            int port = 0;
            while (port == 0)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("The background stockroom reader exited before connecting.");
                try { if (File.Exists(portFile)) { var lines = await File.ReadAllLinesAsync(portFile, deadline.Token); if (lines.Length > 0) int.TryParse(lines[0], out port); } } catch (IOException) { }
                if (port == 0) await Task.Delay(80, deadline.Token);
            }
            using var local = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            string? socket = null;
            while (socket is null)
            {
                string targets = await local.GetStringAsync($"http://127.0.0.1:{port}/json/list", deadline.Token);
                using var list = JsonDocument.Parse(targets);
                foreach (var target in list.RootElement.EnumerateArray()) if (target.GetProperty("type").GetString() == "page" && target.TryGetProperty("webSocketDebuggerUrl", out var address)) { socket = address.GetString(); break; }
                if (socket is null) await Task.Delay(80, deadline.Token);
            }
            var socketUri = new Uri(socket); if (!socketUri.IsLoopback || socketUri.Port != port) throw new IOException("Unexpected browser debugging address.");
            using var client = new Protocol(); await client.ConnectAsync(socketUri, deadline.Token);
            await client.CallAsync("Page.enable", new { }, deadline.Token); await client.CallAsync("Network.enable", new { }, deadline.Token);
            await client.CallAsync("Page.navigate", new { url }, deadline.Token);
            bool searched = false;
            while (!searched)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string state = await client.EvaluateAsync(FindSearch, deadline.Token);
                using var found = JsonDocument.Parse(state);
                string href = found.RootElement.GetProperty("url").GetString() ?? "";
                if (Uri.TryCreate(href, UriKind.Absolute, out var current) && current.Scheme is "http" or "https" && !SameOrigin(uri, current)) throw new IOException("The stockroom redirected to another site. Open the stockroom to complete its sign-in first.");
                if (found.RootElement.GetProperty("found").GetBoolean() && client.IsIdle)
                {
                    await client.EvaluateAsync("(()=>{const el=globalThis.__safetyGateStockroomInput;if(!el?.isConnected)return false;const form=el.closest('form');if(form)form.target='_self';el.focus();el.select();return true;})().toString()", deadline.Token);
                    await client.CallAsync("Input.insertText", new { text = "Safe2Cross" }, deadline.Token);
                    await client.CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, text = "\r", unmodifiedText = "\r" }, deadline.Token);
                    await client.CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13 }, deadline.Token);
                    searched = true;
                }
                else
                {
                    string html = await client.EvaluateAsync(ReadTables, deadline.Token);
                    var direct = StockroomService.Parse(html);
                    if (direct.Rows.Count > 0 && client.IsIdle) return direct with { Reader = "Browser search" };
                    await Task.Delay(150, deadline.Token);
                }
            }
            string? previous = null; int stable = 0;
            while (true)
            {
                await Task.Delay(200, deadline.Token);
                string html = await client.EvaluateAsync(ReadTables, deadline.Token);
                if (html == previous) stable++; else { previous = html; stable = 0; }
                var result = StockroomService.Parse(html);
                if (stable >= 2 && client.IsIdle) return result with { Reader = "Browser search" };
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("The stockroom search did not finish within 25 seconds. Check plant-network access and stockroom sign-in."); }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
                process.Dispose();
            }
            try { if (Directory.Exists(profile)) Directory.Delete(profile, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            BrowserSlot.Release();
        }
    }
    private static bool SameOrigin(Uri a, Uri b) => a.Scheme == b.Scheme && a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
    private static string? FindBrowser()
    {
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            foreach (string path in new[] { "Microsoft/Edge/Application/msedge.exe", "Google/Chrome/Application/chrome.exe" })
            { string candidate = Path.Combine(root, path); if (File.Exists(candidate)) return candidate; }
        return null;
    }
    private const string Documents = "const docs=[document]; for(const f of document.querySelectorAll('iframe,frame')){try{if(f.contentDocument)docs.push(f.contentDocument);}catch{}};";
    public static readonly string FindSearch = "(()=>{" + Documents + """
        let best=null, score=0;
        for(const doc of docs) for(const el of doc.querySelectorAll('input')) {
          if(doc.readyState==='loading')continue;
          const type=(el.type||'text').toLowerCase();
          if(!['search','text'].includes(type)||el.disabled||el.readOnly||el.getClientRects().length===0)continue;
          const hint=[el.name,el.id,el.placeholder,el.getAttribute('aria-label'),el.closest('label')?.textContent].join(' ').toLowerCase();
          let s=type==='search'?10:0;
          if(/search|filter|keyword|query/.test(hint))s+=8;
          if(s>0&&/stock|inventory|storeroom|material/.test(hint))s+=6;
          if(el.closest('.dataTables_filter,.dt-search'))s+=20;
          if(el.closest('form')?.querySelector('input[type=password]'))continue;
          if(s>score){best=el;score=s;}
        }
        globalThis.__safetyGateStockroomInput=best;
        return JSON.stringify({found:!!best,url:location.href});
        })()
        """;
    public static readonly string ReadTables = "(()=>{" + Documents + """
        let html='';
        for(const doc of docs){
          const win=doc.defaultView;const captured=new Set();
          // DataTables normally keeps other result pages outside the rendered table.
          try{if(win.jQuery?.fn?.dataTable){for(const table of win.jQuery.fn.dataTable.tables()){
            const api=win.jQuery(table).DataTable();captured.add(table);if(api.page.len()>0&&api.page.len()<100)api.page.len(100).draw(false);
            html+='<table>'+(table.tHead?.outerHTML||'')+'<tbody>'+api.rows({search:'applied'}).nodes().toArray().map(n=>n.outerHTML).join('')+'</tbody></table>';
          }}}catch{}
          html+=Array.from(doc.querySelectorAll('table')).filter(t=>!captured.has(t)).map(t=>t.outerHTML).join('');
        }
        return html;
        })()
        """;
    private sealed class Protocol : IDisposable
    {
        private readonly ClientWebSocket _socket = new(); private int _id; private readonly HashSet<string> _requests = new(); private DateTime _activity = DateTime.UtcNow;
        public Protocol() { _socket.Options.Proxy = new System.Net.WebProxy(); }
        public bool IsIdle => _requests.Count == 0 && DateTime.UtcNow - _activity > TimeSpan.FromMilliseconds(500);
        public Task ConnectAsync(Uri uri, CancellationToken token) => _socket.ConnectAsync(uri, token);
        public async Task<JsonElement> CallAsync(string method, object args, CancellationToken token)
        {
            int id = ++_id; byte[] message = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = args });
            await _socket.SendAsync(message, WebSocketMessageType.Text, true, token);
            var block = new byte[16384];
            while (true)
            {
                using var data = new MemoryStream(); WebSocketReceiveResult received;
                do { received = await _socket.ReceiveAsync(new ArraySegment<byte>(block), token); if (received.MessageType == WebSocketMessageType.Close) throw new IOException("The stockroom reader disconnected."); if (data.Length + received.Count > 8 * 1024 * 1024) throw new IOException("The stockroom result exceeded 8 MB."); data.Write(block, 0, received.Count); } while (!received.EndOfMessage);
                using var json = JsonDocument.Parse(data.ToArray()); var root = json.RootElement;
                if (root.TryGetProperty("method", out var eventName) && root.TryGetProperty("params", out var p) && p.TryGetProperty("requestId", out var request))
                {
                    if (eventName.GetString() == "Network.requestWillBeSent" && p.TryGetProperty("type", out var kind) && kind.GetString() is "Document" or "Script" or "XHR" or "Fetch") { _requests.Add(request.GetString()!); _activity = DateTime.UtcNow; }
                    else if (eventName.GetString() is "Network.loadingFinished" or "Network.loadingFailed") { if (_requests.Remove(request.GetString()!)) _activity = DateTime.UtcNow; }
                }
                if (!root.TryGetProperty("id", out var response) || response.GetInt32() != id) continue;
                if (root.TryGetProperty("error", out var error)) throw new IOException("Stockroom browser request failed: " + error.GetProperty("message").GetString());
                return root.GetProperty("result").Clone();
            }
        }
        public async Task<string> EvaluateAsync(string expression, CancellationToken token)
        {
            var result = await CallAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, token);
            if (result.TryGetProperty("exceptionDetails", out _)) throw new IOException("The stockroom page could not be read.");
            return result.GetProperty("result").GetProperty("value").GetString() ?? "";
        }
        public void Dispose() => _socket.Dispose();
    }
}
