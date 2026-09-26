using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ClaudeSpend;

/// <summary>
/// Serves the dashboard and its data on http://127.0.0.1:&lt;port&gt;/ — loopback only.
/// Requests whose Host header isn't the loopback address are refused, so other web pages
/// can't read your usage through DNS rebinding.
/// </summary>
public sealed class DashboardServer : IDisposable
{
    public const string PingSignature = "claude-spend";

    private readonly DataStore _store;
    private readonly HttpListener _listener = new();
    private long _lastRequestTicks = DateTime.UtcNow.Ticks;

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/";
    public DateTime LastRequestUtc => new(Interlocked.Read(ref _lastRequestTicks), DateTimeKind.Utc);

    public DashboardServer(DataStore store, int port)
    {
        _store = store;
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
    }

    /// <summary>Tries the preferred port first, then any free one.</summary>
    public static DashboardServer StartOnFreePort(DataStore store, int preferred)
    {
        try { return new DashboardServer(store, preferred); }
        catch (HttpListenerException) { }
        catch (SocketException) { }
        return new DashboardServer(store, FreePort());
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>True when a Claude Code Spend server already answers on this port.</summary>
    public static async Task<bool> IsRunningOn(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };
            var body = await http.GetStringAsync($"http://127.0.0.1:{port}/api/ping");
            return body.Contains(PingSignature, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var reg = ct.Register(() => { try { _listener.Stop(); } catch { } });
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch when (ct.IsCancellationRequested || !_listener.IsListening) { break; }
            catch (HttpListenerException) { continue; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            var host = ctx.Request.Url?.Host ?? "";
            if (host != "127.0.0.1" && host != "localhost")
            {
                Send(res, 403, "text/plain", "Forbidden"u8.ToArray());
                return;
            }
            Interlocked.Exchange(ref _lastRequestTicks, DateTime.UtcNow.Ticks);
            switch (ctx.Request.Url?.AbsolutePath)
            {
                case "/":
                case "/index.html":
                    Send(res, 200, "text/html; charset=utf-8", Dashboard.Html());
                    break;
                case "/api/data":
                    try { Send(res, 200, "application/json", _store.BuildJson()); }
                    catch (Exception ex)
                    {
                        var msg = ex.Message.Replace("\\", "\\\\").Replace("\"", "\\\"");
                        Send(res, 500, "application/json", Encoding.UTF8.GetBytes($"{{\"error\":\"{msg}\"}}"));
                    }
                    break;
                case "/api/ping":
                    Send(res, 200, "application/json",
                        Encoding.UTF8.GetBytes($"{{\"app\":\"{PingSignature}\",\"version\":\"{Dashboard.Version}\"}}"));
                    break;
                case "/favicon.ico":
                    Send(res, 204, "image/x-icon", Array.Empty<byte>());
                    break;
                default:
                    Send(res, 404, "text/plain", "Not found"u8.ToArray());
                    break;
            }
        }
        catch { try { res.Abort(); } catch { } }
    }

    private static void Send(HttpListenerResponse res, int status, string type, byte[] body)
    {
        res.StatusCode = status;
        res.ContentType = type;
        res.Headers["Cache-Control"] = "no-store";
        res.Headers["X-Content-Type-Options"] = "nosniff";
        res.ContentLength64 = body.Length;
        res.OutputStream.Write(body);
        res.OutputStream.Close();
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
