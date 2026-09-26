using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClaudeSpendSetup
{
    /// <summary>
    /// A borderless window whose whole UI is installer.html in WebView2. The page and this form
    /// talk through postMessage: the page sends commands (install, browse…), the form replies with
    /// state and progress events.
    /// </summary>
    internal sealed class SetupForm : Form
    {
        private const int BaseWidth = 820, BaseHeight = 560;

        private readonly Options _opts;
        private readonly WebView2 _web = new WebView2 { Dock = DockStyle.Fill };
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly bool _dark;
        private bool _busy;
        private string _installedDir;

        public SetupForm(Options opts)
        {
            _opts = opts;
            _dark = opts.Theme == "dark" || (opts.Theme != "light" && Native.SystemUsesDarkTheme());
            Text = opts.Uninstall ? "Uninstall Claude Code Spend" : "Claude Code Spend Setup";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = _dark ? Color.FromArgb(26, 26, 25) : Color.FromArgb(252, 252, 251);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            _web.DefaultBackgroundColor = BackColor;
            Controls.Add(_web);
            Load += async (s, e) => await InitAsync();
            FormClosing += (s, e) => { if (_busy) e.Cancel = true; };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;          // CS_DROPSHADOW
                cp.Style |= 0x00020000 | 0x00080000;  // WS_MINIMIZEBOX | WS_SYSMENU: taskbar minimize/restore works
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var scale = DeviceDpi / 96f;
            var w = (int)(BaseWidth * scale);
            var h = (int)(BaseHeight * scale);
            var area = Screen.FromHandle(Handle).WorkingArea;
            Bounds = new Rectangle(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
            Native.StyleWindow(Handle, _dark);
        }

        private async Task InitAsync()
        {
            try
            {
                CoreWebView2Environment.SetLoaderDllFolderPath(Embedded.ExtractLoader());
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Embedded.TempRoot, "webview"));
                await _web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex) when (ex is WebView2RuntimeNotFoundException || ex is DllNotFoundException || ex is BadImageFormatException)
            {
                Hide();
                Fallback.Run(_opts, this);
                Close();
                return;
            }

            var core = _web.CoreWebView2;
            if (_opts.Theme != null)
                core.Profile.PreferredColorScheme = _dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = _opts.Debug;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.WebMessageReceived += OnMessage;
            core.NewWindowRequested += (s, e) => e.Handled = true;
            core.NavigateToString(Embedded.Text("installer.html"));
        }

        private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> msg;
            try { msg = _json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); }
            catch { return; }
            var type = Get(msg, "type");

            switch (type)
            {
                case "init": SendState(); break;
                case "drag": Native.BeginDrag(Handle); break;
                case "minimize": WindowState = FormWindowState.Minimized; break;
                case "close": if (!_busy) Close(); break;

                case "browse":
                    var picked = FolderPicker.Pick(Handle, "Choose where to install Claude Code Spend", Get(msg, "dir"));
                    if (picked != null)
                    {
                        // Picking a parent folder installs into a "Claude Code Spend" folder inside it.
                        if (!picked.TrimEnd('\\').EndsWith(InstallerCore.AppName, StringComparison.OrdinalIgnoreCase))
                            picked = Path.Combine(picked, InstallerCore.AppName);
                        Send(new { type = "browsed", dir = picked, freeBytes = InstallerCore.FreeBytes(picked) });
                    }
                    break;

                case "checkDir":
                    Send(new { type = "browsed", dir = Get(msg, "dir"), freeBytes = InstallerCore.FreeBytes(Get(msg, "dir") ?? "") });
                    break;

                case "install":
                    var dir = Get(msg, "dir");
                    var desktop = GetBool(msg, "desktop");
                    var startMenu = GetBool(msg, "startMenu");
                    RunJob(p => InstallerCore.Install(dir, desktop, startMenu, p),
                        () => _installedDir = InstallerCore.GetInstalled()?.Dir);
                    break;

                case "uninstall":
                    var removeData = GetBool(msg, "removeData");
                    RunJob(p => InstallerCore.Uninstall(removeData, p), null);
                    break;

                case "launch":
                    var exe = Path.Combine(_installedDir ?? InstallerCore.GetInstalled()?.Dir ?? "", InstallerCore.AppExe);
                    try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) }); }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't start the app: " + ex.Message }); break; }
                    Close();
                    break;

                case "openFolder":
                    var folder = _installedDir ?? InstallerCore.GetInstalled()?.Dir;
                    if (folder != null && Directory.Exists(folder)) Process.Start("explorer.exe", "\"" + folder + "\"");
                    break;
            }
        }

        private void RunJob(Action<InstallerCore.Progress> job, Action after)
        {
            if (_busy) return;
            _busy = true;
            Task.Run(() =>
            {
                try
                {
                    job((pct, step, detail) => Post(new { type = "progress", pct, step, detail }));
                    after?.Invoke();
                    Post(new { type = "done" });
                }
                catch (Exception ex)
                {
                    Post(new { type = "error", message = ex.Message });
                }
                finally
                {
                    BeginInvoke((Action)(() => _busy = false));
                }
            });
        }

        private void SendState()
        {
            var installed = InstallerCore.GetInstalled();
            var logs = InstallerCore.FindLogs();
            var dir = installed?.Dir ?? _opts.Dir ?? InstallerCore.DefaultDir;
            Send(new
            {
                type = "state",
                mode = _opts.Uninstall ? "uninstall" : "install",
                version = InstallerCore.Version,
                installed = installed == null ? null : new { version = installed.Version, dir = installed.Dir },
                defaults = new
                {
                    dir,
                    desktop = installed?.Desktop ?? !_opts.NoDesktop,
                    startMenu = installed?.StartMenu ?? !_opts.NoStartMenu,
                },
                requiredBytes = InstallerCore.RequiredBytes,
                freeBytes = InstallerCore.FreeBytes(dir),
                logs = new { found = logs.Found, dir = logs.Dir, sessions = logs.Sessions, projects = logs.Projects },
                page = _opts.Page,
            });
        }

        private void Post(object o)
        {
            try { BeginInvoke((Action)(() => Send(o))); } catch (InvalidOperationException) { }
        }

        private void Send(object o)
        {
            try { _web.CoreWebView2?.PostWebMessageAsJson(_json.Serialize(o)); } catch { }
        }

        private static string Get(Dictionary<string, object> m, string k) => m.TryGetValue(k, out var v) ? v as string : null;
        private static bool GetBool(Dictionary<string, object> m, string k) => m.TryGetValue(k, out var v) && v is bool b && b;
    }

    /// <summary>Without the WebView2 runtime there is no fancy UI — and the app couldn't run either.</summary>
    internal static class Fallback
    {
        public static void Run(Options opts, IWin32Window owner)
        {
            var r = MessageBox.Show(owner,
                "Claude Code Spend needs the Microsoft Edge WebView2 Runtime, which is part of Windows 11 " +
                "and most up-to-date Windows 10 PCs, but it isn't installed here.\n\n" +
                "Open the download page now? Run this setup again after installing it.",
                "Claude Code Spend Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true }); } catch { }
            }
        }
    }
}
