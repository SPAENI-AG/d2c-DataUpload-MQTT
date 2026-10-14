using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
namespace D2cDataUpload.Core;

/// <summary>
/// Webbasierte Hilfe: Mini-Webserver nur auf 127.0.0.1 (zufälliger Port, keine Admin-Rechte,
/// kein URL-ACL nötig) liefert die HTML-Dateien aus dem Ordner "Help" neben der exe aus.
/// Geöffnet wird im Standardbrowser – Sprungmarken (#anker) funktionieren, anders als bei file://.
/// </summary>
public static class HelpServer
{
    private static TcpListener? _listener;
    private static CancellationTokenSource? _cts;
    private static readonly object Sync = new();

    /// <summary>Ordner mit de.html / en.html / style.css (Standard: "Help" neben der exe).</summary>
    public static string HelpFolder { get; set; } = Path.Combine(AppContext.BaseDirectory, "Help");

    /// <summary>Öffnet die Hilfe im Standardbrowser. Rückgabe: null oder Fehlermeldung.</summary>
    public static string? Open(string anchor = "")
    {
        var url = Url(anchor);
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return null;
        }
        catch (Exception e)
        {
            return Loc.T("Help_OpenFailed", url, e.Message);
        }
    }

    public static string Url(string anchor = "")
    {
        var page = $"{Loc.Instance.Language}.html";
        var frag = string.IsNullOrEmpty(anchor) ? "" : "#" + anchor;
        var port = EnsureStarted();
        return port > 0
            ? $"http://127.0.0.1:{port}/{page}{frag}"
            : new Uri(Path.Combine(HelpFolder, page)).AbsoluteUri + frag;
    }

    private static int EnsureStarted()
    {
        lock (Sync)
        {
            if (_listener is not null) return ((IPEndPoint)_listener.LocalEndpoint).Port;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => AcceptLoop(_listener, _cts.Token));
                return ((IPEndPoint)_listener.LocalEndpoint).Port;
            }
            catch (SocketException)
            {
                _listener = null;
                return 0;
            }
        }
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;
        }
    }

    private static async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".png"] = "image/png",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
    };

    private static async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync() ?? "";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { } // Header ignorieren

                var parts = requestLine.Split(' ');
                var rel = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Split('?', '#')[0]).TrimStart('/') : "";
                if (rel.Length == 0) rel = $"{Loc.Instance.Language}.html";

                var root = Path.GetFullPath(HelpFolder);
                var full = Path.GetFullPath(Path.Combine(root, rel));
                byte[] body;
                string status, type;
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    body = await File.ReadAllBytesAsync(full);
                    status = "200 OK";
                    type = Mime.GetValueOrDefault(Path.GetExtension(full), "application/octet-stream");
                }
                else
                {
                    body = Encoding.UTF8.GetBytes("404 – not found");
                    status = "404 Not Found";
                    type = "text/plain; charset=utf-8";
                }
                var head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                           "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(body);
            }
            catch
            {
                // Browser hat Verbindung abgebrochen – ignorieren
            }
        }
    }
}
