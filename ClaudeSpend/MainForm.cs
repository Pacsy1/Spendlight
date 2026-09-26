using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClaudeSpend;

/// <summary>
/// A window hosting the dashboard in WebView2. The page and its data are served from memory
/// through a virtual origin — no local web server, no network access.
/// </summary>
public sealed class MainForm : Form
{
    private const string Origin = "https://claude-spend.app";

    private readonly DataStore _store;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    public MainForm(DataStore store)
    {
        _store = store;
        Text = "Claude Code Spend";
        MinimumSize = new Size(420, 480);
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 1000);
        Size = new Size(Math.Min(1440, area.Width - 80), Math.Min(1000, area.Height - 60));
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { /* default icon */ }

        var dark = SystemUsesDarkTheme();
        BackColor = dark ? Color.FromArgb(13, 13, 13) : Color.FromArgb(245, 245, 242);
        _web.DefaultBackgroundColor = BackColor;   // no white flash before the page paints
        Controls.Add(_web);
        HandleCreated += (_, _) => SetDarkTitleBar(dark);
        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeSpend");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this,
                "This app needs the Microsoft Edge WebView2 Runtime, which ships with Windows 11.\n\n" +
                "Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and start the app again.",
                "WebView2 Runtime missing", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessage;
        core.DocumentTitleChanged += (_, _) => Text = core.DocumentTitle;
        // Anything that tries to leave the dashboard opens in the real browser instead.
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) { e.Cancel = true; OpenExternal(e.Uri); }
        };
        core.Navigate(Origin + "/");
    }

    private async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var path = new Uri(e.Request.Uri).AbsolutePath;
        var core = _web.CoreWebView2;
        if (path is "/" or "/index.html")
        {
            e.Response = core.Environment.CreateWebResourceResponse(
                new MemoryStream(Dashboard.Html()), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            return;
        }
        if (path == "/api/data")
        {
            // Parse logs off the UI thread; the deferral keeps the request open meanwhile.
            var deferral = e.GetDeferral();
            try
            {
                byte[] body;
                int status = 200;
                try { body = await Task.Run(_store.BuildJson); }
                catch (Exception ex)
                {
                    status = 500;
                    body = JsonSerializer.SerializeToUtf8Bytes(new { error = ex.Message });
                }
                e.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(body), status, status == 200 ? "OK" : "Error",
                    "Content-Type: application/json\r\nCache-Control: no-store");
            }
            finally { deferral.Complete(); }
            return;
        }
        if (path == "/api/ping")
        {
            var ping = System.Text.Encoding.UTF8.GetBytes($"{{\"app\":\"{DashboardServer.PingSignature}\",\"version\":\"{Dashboard.Version}\"}}");
            e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(ping), 200, "OK", "Content-Type: application/json");
            return;
        }
        e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
    }

    /// <summary>The page reports its effective theme so the title bar can match it.</summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.TryGetProperty("theme", out var t))
            {
                var dark = t.GetString() == "dark";
                SetDarkTitleBar(dark);
                BackColor = dark ? Color.FromArgb(13, 13, 13) : Color.FromArgb(245, 245, 242);
            }
        }
        catch (JsonException) { }
    }

    private static void OpenExternal(string uri)
    {
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void SetDarkTitleBar(bool dark)
    {
        if (!IsHandleCreated) return;
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref v, sizeof(int));
    }
}
