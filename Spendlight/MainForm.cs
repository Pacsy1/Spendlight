using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Spendlight;

/// <summary>
/// A window hosting the dashboard in WebView2. The page and its data are served from memory
/// through a virtual origin — no local web server.
///
/// The window keeps Windows' own frame (shadow, snapping, resize edges, maximize animation,
/// rounded corners) but hides its title bar: the page draws its own, and asks the window to
/// move, minimize, maximize or close through web messages.
/// </summary>
public sealed class MainForm : Form
{
    // A reserved .example domain (RFC 2606): never a real site. Every request to it is answered in-process.
    private const string Origin = "https://spendlight.example";

    private readonly DataStore _store;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private bool _pageReady;

    public MainForm(DataStore store)
    {
        _store = store;
        Text = "Spendlight";
        MinimumSize = new Size(480, 520);
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 1000);
        Size = new Size(Math.Min(1440, area.Width - 80), Math.Min(1000, area.Height - 60));
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { /* default icon */ }

        var dark = SystemUsesDarkTheme();
        BackColor = dark ? Color.FromArgb(13, 13, 13) : Color.FromArgb(245, 245, 242);
        _web.DefaultBackgroundColor = BackColor;   // no white flash before the page paints
        Controls.Add(_web);
        HandleCreated += (_, _) =>
        {
            SetDarkFrame(dark);
            // Apply the custom frame (see WndProc) right away.
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            UpdateResizeStrip();
        };
        Resize += (_, _) => { UpdateResizeStrip(); SendWindowState(); };
        Activated += (_, _) => SendWindowState();
        Deactivate += (_, _) => SendWindowState();
        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spendlight");
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
        core.NavigationCompleted += (_, _) => { _pageReady = true; SendWindowState(); };
        // Anything that tries to leave the dashboard opens in the real browser instead.
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) { e.Cancel = true; OpenExternal(e.Uri); }
        };
        core.Navigate(Origin + "/");
    }

    // ───────────────────────────── page ↔ window ─────────────────────────────

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
        if (path is "/api/data" or "/api/rates")
        {
            // Off the UI thread; the deferral keeps the request open meanwhile.
            var deferral = e.GetDeferral();
            try
            {
                byte[] body;
                int status = 200;
                try { body = path == "/api/data" ? await Task.Run(_store.BuildJson) : await Rates.GetJsonAsync(); }
                catch (Exception ex)
                {
                    status = path == "/api/data" ? 500 : 502;
                    body = Rates.ErrorJson(ex.Message);
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

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;

            // The page's theme and title-bar colour, so the frame and resize strip match it.
            if (root.TryGetProperty("theme", out var t))
            {
                var dark = t.GetString() == "dark";
                SetDarkFrame(dark);
                var bg = root.TryGetProperty("bg", out var b) ? b.GetString() : null;
                BackColor = TryParseColor(bg) ?? (dark ? Color.FromArgb(13, 13, 13) : Color.FromArgb(245, 245, 242));
            }

            if (root.TryGetProperty("window", out var w))
            {
                switch (w.GetString())
                {
                    case "hello": SendWindowState(); break;
                    case "drag":
                        ReleaseCapture();
                        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                        break;
                    case "minimize": WindowState = FormWindowState.Minimized; break;
                    case "maximize": WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
                    case "close": Close(); break;
                    case "menu": ShowSystemMenu(Cursor.Position); break;
                }
            }
        }
        catch (JsonException) { }
    }

    private void SendWindowState()
    {
        if (!_pageReady || _web.CoreWebView2 == null) return;
        var json = $"{{\"type\":\"window\",\"maximized\":{(WindowState == FormWindowState.Maximized ? "true" : "false")},\"active\":{(ActiveForm == this ? "true" : "false")}}}";
        try { _web.CoreWebView2.PostWebMessageAsJson(json); } catch { }
    }

    /// <summary>Right-clicking the title bar shows the usual window menu (Restore, Move, Size…).</summary>
    private void ShowSystemMenu(Point screen)
    {
        var menu = GetSystemMenu(Handle, false);
        if (menu == IntPtr.Zero) return;
        bool max = WindowState == FormWindowState.Maximized;
        EnableMenuItem(menu, SC_RESTORE, max ? MF_ENABLED : MF_GRAYED);
        EnableMenuItem(menu, SC_MOVE, max ? MF_GRAYED : MF_ENABLED);
        EnableMenuItem(menu, SC_SIZE, max ? MF_GRAYED : MF_ENABLED);
        EnableMenuItem(menu, SC_MAXIMIZE, max ? MF_GRAYED : MF_ENABLED);
        EnableMenuItem(menu, SC_MINIMIZE, MF_ENABLED);
        int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screen.X, screen.Y, 0, Handle, IntPtr.Zero);
        if (cmd != 0) PostMessage(Handle, WM_SYSCOMMAND, (IntPtr)cmd, IntPtr.Zero);
    }

    // ───────────────────────────── the frame ─────────────────────────────

    /// <summary>Height of the invisible strip along the top edge that resizes the window.</summary>
    private int ResizeStrip => (int)Math.Round(5 * DeviceDpi / 96.0);

    /// <summary>The top resize strip is the form itself peeking out above the page (same colour).</summary>
    private void UpdateResizeStrip()
    {
        var top = WindowState == FormWindowState.Maximized ? 0 : ResizeStrip;
        if (Padding.Top != top) Padding = new Padding(0, top, 0, 0);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            // Take the title bar away but keep the side and bottom frame (the invisible resize
            // borders). A maximized window hangs over the screen edge by its frame, so inset it.
            var p = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
            var dpi = (uint)DeviceDpi;
            int pad = GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
            int fx = GetSystemMetricsForDpi(SM_CXFRAME, dpi) + pad;
            int fy = GetSystemMetricsForDpi(SM_CYFRAME, dpi) + pad;
            p.rgrc0.left += fx;
            p.rgrc0.right -= fx;
            p.rgrc0.bottom -= fy;
            if (IsZoomed(Handle)) p.rgrc0.top += fy;
            Marshal.StructureToPtr(p, m.LParam, false);
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if ((int)m.Result == HTCLIENT && !IsZoomed(Handle))
            {
                var pt = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
                int grip = ResizeStrip;
                if (pt.Y < grip)
                    m.Result = (IntPtr)(pt.X < grip * 3 ? HTTOPLEFT : pt.X > ClientSize.Width - grip * 3 ? HTTOPRIGHT : HTTOP);
            }
            return;
        }
        base.WndProc(ref m);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static Color? TryParseColor(string? hex)
    {
        if (hex == null || hex.Length != 7 || hex[0] != '#') return null;
        try { return ColorTranslator.FromHtml(hex); } catch { return null; }
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

    /// <summary>Dark or light window border and system menu, to match the page.</summary>
    private void SetDarkFrame(bool dark)
    {
        if (!IsHandleCreated) return;
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref v, sizeof(int));
    }

    private const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1, WM_SYSCOMMAND = 0x0112;
    private const int HTCLIENT = 1, HTCAPTION = 2, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    private const int SM_CXFRAME = 32, SM_CYFRAME = 33, SM_CXPADDEDBORDER = 92;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    private const uint SC_SIZE = 0xF000, SC_MOVE = 0xF010, SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120;
    private const uint MF_ENABLED = 0x0, MF_GRAYED = 0x1, TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NCCALCSIZE_PARAMS { public RECT rgrc0, rgrc1, rgrc2; public IntPtr lppos; }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);
    [DllImport("user32.dll")] private static extern bool EnableMenuItem(IntPtr menu, uint item, uint enable);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
}
