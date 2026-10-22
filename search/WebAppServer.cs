using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace search
{
    /// <summary>
    /// Serves the built-in HTML apps and the files handed to them over loopback HTTP - the
    /// portable replacement of WebView2's virtual host and file-handle APIs. Any engine can
    /// show the pages then: WebKitGTK inside the app, or the user's default browser.
    /// Bound to 127.0.0.1 only; every file is reachable solely through an unguessable
    /// per-window session token, and requests must name this exact host (no DNS rebinding).
    /// A stable per-user port keeps the pages' origin - and so their saved localStorage
    /// (Log explorer profiles) - the same from one run to the next.
    /// </summary>
    internal static class WebAppServer
    {
        sealed record Session(string Page, string[] Files, string HandoffScript, Action<string> OnMessage);

        static readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);
        static readonly object gate = new();
        static TcpListener listener;
        static CancellationTokenSource stop;
        static int port;

        static string WebAppsFolder => Path.Combine(AppContext.BaseDirectory, "WebApps");

        static int PreferredPort()
        {
            //FNV of the user name => 41000..48999, the same every run for this user
            uint hash = 2166136261;
            foreach (var c in Environment.UserName ?? "") hash = (hash ^ c) * 16777619;
            return 41000 + (int)(hash % 8000);
        }

        /// <summary>Base URL ("http://127.0.0.1:port/"), starting the server on first use</summary>
        public static string BaseUrl
        {
            get
            {
                lock (gate)
                {
                    if (listener == null) Start();
                    return $"http://127.0.0.1:{port}/";
                }
            }
        }

        static void Start()
        {
            var preferred = PreferredPort();
            for (var attempt = 0; attempt < 20 && listener == null; attempt++)
            {
                try
                {
                    var candidate = attempt < 19 ? preferred + attempt : 0;
                    var l = new TcpListener(IPAddress.Loopback, candidate);
                    l.Start();
                    listener = l;
                    port = ((IPEndPoint)l.LocalEndpoint).Port;
                }
                catch (SocketException) { }
            }
            if (listener == null) throw new IOException("No loopback port is available for the web apps.");
            stop = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoop(listener, stop.Token));
        }

        public static void Shutdown()
        {
            lock (gate)
            {
                stop?.Cancel();
                try { listener?.Stop(); } catch { }
                listener = null;
                sessions.Clear();
            }
        }

        /// <summary>
        /// Register one window's page and files; returns the URL that opens the page with
        /// them. Messages the page posts (unreadable files, calculator size, zoom) reach
        /// onMessage as JSON text on a background thread.
        /// </summary>
        public static string Register(string page, string[] files, string message, double zoom, Action<string> onMessage,
            out string token)
        {
            var baseUrl = BaseUrl;
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var script = HandoffScript(token, files, message, zoom, page == Apps.Calculator);
            sessions[token] = new Session(page, files ?? Array.Empty<string>(), script, onMessage);
            return baseUrl + "WebApps/" + Uri.EscapeDataString(page) + "?lang="
                + Uri.EscapeDataString(CultureInfo.CurrentUICulture.Name) + "&session=" + token;
        }

        public static void Unregister(string token)
        {
            if (token != null) sessions.TryRemove(token, out _);
        }

        static async Task AcceptLoop(TcpListener l, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await l.AcceptTcpClientAsync(token); }
                catch { return; }
                _ = Task.Run(() => Serve(client));
            }
        }

        static async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 10000;
                    var stream = client.GetStream();
                    var (method, target, headers, body) = await ReadRequest(stream);
                    if (method == null) return;
                    //Only this exact authority - a page from elsewhere resolving to 127.0.0.1 is refused
                    if (!headers.TryGetValue("host", out var host)
                        || host != $"127.0.0.1:{port}" && host != $"localhost:{port}")
                    {
                        await Respond(stream, 403, "text/plain", Encoding.UTF8.GetBytes("Forbidden"));
                        return;
                    }
                    await Route(stream, method, target, body);
                }
                catch (Exception e) { $"web app server: {e.Message}".Debug(); }
            }
        }

        static async Task Route(NetworkStream stream, string method, string target, byte[] body)
        {
            var queryAt = target.IndexOf('?');
            var path = Uri.UnescapeDataString(queryAt < 0 ? target : target[..queryAt]);
            var query = queryAt < 0 ? "" : target[(queryAt + 1)..];
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (method == "GET" && parts.Length == 2 && parts[0] == "WebApps")
            {
                var name = parts[1];
                if (!WebAppWindow.IsWebApp(name) && name != "LICENSE.singleHtmlApps.txt")
                {
                    await Respond(stream, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
                    return;
                }
                var file = Path.Combine(WebAppsFolder, name);
                if (!File.Exists(file))
                {
                    await Respond(stream, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
                    return;
                }
                var content = await File.ReadAllBytesAsync(file);
                if (name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                    && QueryValue(query, "session") is { } token && sessions.TryGetValue(token, out var session))
                    content = Inject(content, session.HandoffScript);
                await Respond(stream, 200, name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                    ? "text/html; charset=utf-8" : "text/plain; charset=utf-8", content);
                return;
            }
            if (parts.Length >= 3 && parts[0] == "fsm" && sessions.TryGetValue(parts[1], out var owner))
            {
                if (method == "GET" && parts.Length == 4 && parts[2] == "file"
                    && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    && index >= 0 && index < owner.Files.Length)
                {
                    await SendFile(stream, owner.Files[index]);
                    return;
                }
                if (method == "POST" && parts.Length == 3 && parts[2] == "message")
                {
                    try { owner.OnMessage?.Invoke(Encoding.UTF8.GetString(body ?? Array.Empty<byte>())); }
                    catch (Exception e) { $"web app message failed: {e.Message}".Debug(); }
                    await Respond(stream, 204, "text/plain", Array.Empty<byte>());
                    return;
                }
            }
            await Respond(stream, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
        }

        static string QueryValue(string query, string name)
        {
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0 && pair[..eq] == name) return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
            return null;
        }

        /// <summary>The handoff script runs before the page's own scripts</summary>
        static byte[] Inject(byte[] html, string script)
        {
            var text = Encoding.UTF8.GetString(html);
            var tag = "<script>" + script + "</script>";
            var head = text.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
            text = head >= 0 ? text.Insert(head + "<head>".Length, tag) : tag + text;
            return Encoding.UTF8.GetBytes(text);
        }

        static async Task SendFile(NetworkStream stream, string path)
        {
            //Read the content once at the handoff, retrying while a writer holds the file -
            //a log being written must not fail the whole Log explorer load
            byte[] content = null;
            Exception last = null;
            for (var attempt = 1; attempt <= 5 && content == null; attempt++)
            {
                try
                {
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var memory = new MemoryStream();
                    await file.CopyToAsync(memory);
                    content = memory.ToArray();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    last = e;
                    await Task.Delay(50 * attempt);
                }
            }
            if (content == null)
            {
                await Respond(stream, 409, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(last?.Message ?? "Unreadable"));
                return;
            }
            await Respond(stream, 200, "application/octet-stream", content);
        }

        static async Task<(string Method, string Target, System.Collections.Generic.Dictionary<string, string> Headers, byte[] Body)>
            ReadRequest(NetworkStream stream)
        {
            var buffer = new byte[16384];
            var received = new MemoryStream();
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer);
                if (read <= 0) return default;
                received.Write(buffer, 0, read);
                if (received.Length > 65536) return default;
                headerEnd = IndexOf(received.GetBuffer(), (int)received.Length, "\r\n\r\n"u8);
            }
            var all = received.ToArray();
            var lines = Encoding.ASCII.GetString(all, 0, headerEnd).Split("\r\n");
            var request = lines[0].Split(' ');
            if (request.Length < 2) return default;
            var headers = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }
            var bodyStart = headerEnd + 4;
            var length = headers.TryGetValue("content-length", out var value) && int.TryParse(value, out var n)
                ? Math.Clamp(n, 0, 1 << 20) : 0;
            var body = new MemoryStream();
            body.Write(all, bodyStart, Math.Min(length, all.Length - bodyStart));
            while (body.Length < length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - body.Length)));
                if (read <= 0) break;
                body.Write(buffer, 0, read);
            }
            return (request[0], request[1], headers, body.ToArray());
        }

        static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern)
            => data.AsSpan(0, length).IndexOf(pattern);

        static async Task Respond(NetworkStream stream, int status, string contentType, byte[] content)
        {
            var reason = status switch { 200 => "OK", 204 => "No Content", 403 => "Forbidden", 409 => "Conflict", _ => "Not Found" };
            var header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {content.Length}\r\n"
                + "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
            if (content.Length > 0) await stream.WriteAsync(content);
            await stream.FlushAsync();
        }

        /// <summary>
        /// The server-mode counterpart of WebAppWindow's FileHandoffScript: fetches the files
        /// of the session and feeds them to the page the same way (Log explorer API, a drop on
        /// the document, or the Hex editor's two file inputs). Also applies the saved zoom
        /// (Ctrl+wheel / Ctrl+plus/minus change it) and reports the calculator's size.
        /// </summary>
        static string HandoffScript(string token, string[] files, string message, double zoom, bool calculator)
        {
            //Both are embedded in a <script> element - never let them close it
            var names = JsonSerializer.Serialize((files ?? Array.Empty<string>()).Select(Path.GetFileName).ToArray())
                .Replace("</", "<\\/");
            message = message?.Replace("</", "<\\/");
            var zoomText = zoom.ToString("0.###", CultureInfo.InvariantCulture);
            return $$"""
                (() => {
                    const token = '{{token}}', names = {{names}}, message = {{message ?? "null"}};
                    const post = m => fetch('/fsm/' + token + '/message', { method: 'POST', body: JSON.stringify(m) }).catch(() => {});
                    let zoom = {{zoomText}};
                    const applyZoom = () => { document.documentElement.style.zoom = zoom; };
                    const setZoom = z => { zoom = Math.min(5, Math.max(0.25, Math.round(z * 100) / 100)); applyZoom(); post({ zoom }); };
                    document.addEventListener('DOMContentLoaded', applyZoom);
                    window.addEventListener('wheel', e => { if (!e.ctrlKey) return; e.preventDefault(); setZoom(zoom * (e.deltaY < 0 ? 1.1 : 1 / 1.1)); }, { passive: false });
                    window.addEventListener('keydown', e => {
                        if (!e.ctrlKey) return;
                        if (e.key === '+' || e.key === '=') { e.preventDefault(); setZoom(zoom * 1.1); }
                        else if (e.key === '-') { e.preventDefault(); setZoom(zoom / 1.1); }
                        else if (e.key === '0') { e.preventDefault(); setZoom(1); }
                    });
                    if ({{(calculator ? "true" : "false")}}) window.addEventListener('DOMContentLoaded', () => {
                        const app = document.querySelector('.app');
                        if (!app) return;
                        const preferredWidth = parseFloat(getComputedStyle(app).maxWidth) || app.getBoundingClientRect().width;
                        let timer, previous = '';
                        const report = () => {
                            const body = getComputedStyle(document.body);
                            const bounds = app.getBoundingClientRect();
                            const width = Math.ceil(Math.max(bounds.width, app.scrollWidth, preferredWidth) + parseFloat(body.paddingLeft) + parseFloat(body.paddingRight) + 2);
                            const height = Math.ceil(bounds.height + parseFloat(body.paddingTop) + parseFloat(body.paddingBottom) + 2);
                            const key = width + ':' + height;
                            if (key === previous) return;
                            previous = key;
                            post({ calculatorSize: { width, height } });
                        };
                        const queue = () => { clearTimeout(timer); timer = setTimeout(report, 50); };
                        new ResizeObserver(queue).observe(app);
                        window.addEventListener('resize', queue);
                        document.fonts.ready.then(queue);
                        queue();
                    }, { once: true });
                    if (!names.length || !message) return;
                    const shown = id => { const e = document.getElementById(id); return e && getComputedStyle(e).display !== 'none'; };
                    const until = async test => { for (let i = 0; i < 1200 && !test(); i++) await new Promise(r => setTimeout(r, 50)); };
                    const transfer = list => { const dt = new DataTransfer(); list.forEach(f => dt.items.add(f)); return dt; };
                    const read = async index => {
                        const response = await fetch('/fsm/' + token + '/file/' + index);
                        if (!response.ok) throw new Error(await response.text() || response.statusText);
                        return new File([await response.blob()], names[index]);
                    };
                    window.addEventListener('load', async () => {
                        const m = message;
                        const results = await Promise.allSettled(names.map((_, i) => read(i)));
                        const files = [], paths = [], failed = [];
                        results.forEach((r, index) => r.status === 'fulfilled'
                            ? (files.push(r.value), paths.push(((m.logExplorer || {}).paths || [])[index]))
                            : failed.push({ index, error: String((r.reason && (r.reason.message || r.reason.name)) || r.reason) }));
                        if (failed.length) post({ failed });
                        if (!files.length) return;
                        if (m.logExplorer && window.logExplorer) { await window.logExplorer.open(files, { ...m.logExplorer, paths }); return; }
                        if (m.drop) { document.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: transfer(files) })); return; }
                        for (let i = 0; i < files.length && i < (m.inputs || []).length; i++) {
                            const step = m.inputs[i];
                            if (step.after) await until(() => shown(step.after));
                            const input = document.getElementById(step.id);
                            input.files = transfer([files[i]]).files;
                            input.dispatchEvent(new Event('change', { bubbles: true }));
                        }
                    }, { once: true });
                })();
                """;
        }
    }
}
