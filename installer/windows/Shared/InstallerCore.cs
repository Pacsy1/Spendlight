using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace ClaudeSpendSetup
{
    /// <summary>What is on disk and in the registry, and how to put it there or take it away.
    /// Installs per user (no administrator rights): %LOCALAPPDATA%\Programs\Claude Code Spend.</summary>
    internal static class InstallerCore
    {
        public const string AppName = "Claude Code Spend";
        public const string AppExe = "ClaudeSpend.exe";
        public const string UninstallExe = "Uninstall.exe";
        private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClaudeCodeSpend";
        private const string PayloadApp = "payload/ClaudeSpend.exe";
        private const string PayloadUninstaller = "payload/Uninstall.exe";

        /// <summary>Progress callback: percent (0-100), step id, human-readable detail.</summary>
        public delegate void Progress(int percent, string step, string detail);

        public static string Version
        {
            get
            {
                var v = typeof(InstallerCore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
                var plus = v.IndexOf('+');
                return plus >= 0 ? v.Substring(0, plus) : v;
            }
        }

        public static bool HasPayload => Embedded.Has(PayloadApp);

        public static string DefaultDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

        public static string StartMenuShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

        public static string DesktopShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

        /// <summary>The app's own WebView2 profile (saved filters, theme).</summary>
        public static string AppDataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeSpend");

        public static long RequiredBytes => Embedded.Length(PayloadApp) + Embedded.Length(PayloadUninstaller);

        public sealed class Installed
        {
            public string Version;
            public string Dir;
            public bool Desktop;
            public bool StartMenu;
        }

        public static Installed GetInstalled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RegPath))
            {
                var dir = key?.GetValue("InstallLocation") as string;
                if (string.IsNullOrEmpty(dir)) return null;
                return new Installed
                {
                    Version = key.GetValue("DisplayVersion") as string ?? "",
                    Dir = dir,
                    Desktop = File.Exists(DesktopShortcut),
                    StartMenu = File.Exists(StartMenuShortcut),
                };
            }
        }

        public static long FreeBytes(string dir)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))).AvailableFreeSpace; }
            catch { return -1; }
        }

        /// <summary>Claude Code's logs, so the welcome page can say what the dashboard will show.</summary>
        public static (bool Found, string Dir, int Sessions, int Projects) FindLogs()
        {
            var cfg = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            var baseDir = string.IsNullOrEmpty(cfg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                : cfg;
            var root = Path.Combine(baseDir, "projects");
            try
            {
                if (!Directory.Exists(root)) return (false, root, 0, 0);
                var files = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories);
                var projects = files.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                return (files.Length > 0, root, files.Length, projects);
            }
            catch { return (false, root, 0, 0); }
        }

        /// <summary>Throws with a readable message if the folder can't be used.</summary>
        public static string ValidateDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) throw new InvalidOperationException("Choose a folder to install into.");
            string full;
            try { full = Path.GetFullPath(dir.Trim()); }
            catch { throw new InvalidOperationException("That doesn't look like a valid folder path."); }
            if (!Path.IsPathRooted(full) || full.StartsWith(@"\\", StringComparison.Ordinal))
                throw new InvalidOperationException("Choose a folder on a local drive.");
            var free = FreeBytes(full);
            if (free >= 0 && free < RequiredBytes + 10 * 1024 * 1024)
                throw new InvalidOperationException($"There isn't enough free space on {Path.GetPathRoot(full)} ({Mb(free)} free, {Mb(RequiredBytes)} needed).");
            try
            {
                Directory.CreateDirectory(full);
                var probe = Path.Combine(full, ".write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Setup can't write to that folder without administrator rights. Pick a folder inside your user profile, such as the default location.");
            }
            return full;
        }

        public static void Install(string dir, bool desktop, bool startMenu, Progress progress)
        {
            dir = ValidateDir(dir);
            var previous = GetInstalled();

            progress(2, "prepare", "Checking for a running copy…");
            CloseRunningApp(dir);
            if (previous != null && !SamePath(previous.Dir, dir))
                progress(4, "prepare", "Moving from " + previous.Dir);
            Pace();

            // 1. The app itself (the bulk of the work).
            var target = Path.Combine(dir, AppExe);
            var total = Embedded.Length(PayloadApp);
            ExtractTo(PayloadApp, target, copied =>
                progress(6 + (int)(74 * copied / Math.Max(total, 1)), "copy", $"Copying {Mb(copied)} of {Mb(total)}"));

            progress(81, "copy", "Adding the uninstaller");
            ExtractTo(PayloadUninstaller, Path.Combine(dir, UninstallExe), null);

            // Moving to a new folder: remove the old copy.
            if (previous != null && !SamePath(previous.Dir, dir)) RemoveAppFiles(previous.Dir);
            Pace();

            // 2. Shortcuts.
            progress(86, "shortcuts", startMenu ? "Adding to the Start menu" : "Skipping the Start menu");
            if (startMenu) Shortcut.Create(StartMenuShortcut, target, dir, "See what your Claude Code usage would cost");
            else TryDelete(StartMenuShortcut);
            Pace();
            progress(90, "shortcuts", desktop ? "Adding a desktop shortcut" : "Skipping the desktop shortcut");
            if (desktop) Shortcut.Create(DesktopShortcut, target, dir, "See what your Claude Code usage would cost");
            else TryDelete(DesktopShortcut);
            Pace();

            // 3. Apps & features entry.
            progress(95, "register", "Adding to Apps & features");
            Register(dir);
            Pace();

            progress(100, "done", "Installed");
        }

        public static void Uninstall(bool removeData, Progress progress)
        {
            var info = GetInstalled();
            var dir = info?.Dir ?? Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);

            progress(5, "close", "Closing Claude Code Spend if it's open");
            CloseRunningApp(dir);
            Pace();

            progress(30, "shortcuts", "Removing shortcuts");
            TryDelete(StartMenuShortcut);
            TryDelete(DesktopShortcut);
            Pace();

            progress(55, "files", "Removing app files");
            RemoveAppFiles(dir);
            Pace();

            progress(75, "register", "Removing from Apps & features");
            try { Registry.CurrentUser.DeleteSubKeyTree(RegPath, false); } catch { }
            Pace();

            if (removeData)
            {
                progress(88, "data", "Deleting saved settings");
                for (int i = 0; i < 5; i++)
                {
                    try { if (Directory.Exists(AppDataDir)) Directory.Delete(AppDataDir, true); break; }
                    catch { Thread.Sleep(400); }   // WebView2 may still be letting go of its files
                }
                Pace();
            }

            _pendingSelfDelete = dir;   // Uninstall.exe removes itself once it exits (see FinishSelfCleanup)
            progress(100, "done", "Removed");
        }

        private static string _pendingSelfDelete;

        /// <summary>Deletes only files Setup put there; the folder goes only if it ends up empty.</summary>
        private static void RemoveAppFiles(string dir)
        {
            TryDelete(Path.Combine(dir, AppExe));
            TryDelete(Path.Combine(dir, AppExe + ".new"));
            var me = Assembly.GetEntryAssembly().Location;
            var uninstaller = Path.Combine(dir, UninstallExe);
            if (!SamePath(me, uninstaller)) TryDelete(uninstaller);
            try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }

        /// <summary>
        /// A running Uninstall.exe can't delete itself: call this as the process exits, and a hidden
        /// cmd removes it (and the folder, only if empty) a moment later. `ping` is the delay because
        /// `timeout` refuses to run without a console.
        /// </summary>
        public static void FinishSelfCleanup()
        {
            var dir = _pendingSelfDelete;
            if (dir == null) return;
            var me = Assembly.GetEntryAssembly().Location;
            if (!SamePath(Path.GetDirectoryName(me), dir)) return;
            var wait = "ping -n 3 127.0.0.1 >NUL";
            var cmd = $"/c {wait} & del /f /q \"{me}\" & {wait} & del /f /q \"{me}\" 2>NUL & rd \"{dir}\"";
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", cmd)
                {
                    CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetTempPath(),
                });
            }
            catch { }
        }

        private static void Register(string dir)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RegPath))
            {
                var exe = Path.Combine(dir, AppExe);
                var uninstaller = Path.Combine(dir, UninstallExe);
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", AppName);
                key.SetValue("DisplayIcon", exe + ",0");
                key.SetValue("InstallLocation", dir);
                key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
                key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall --quiet");
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("Comments", "See what your Claude Code usage would cost at API list prices.");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(RequiredBytes / 1024), RegistryValueKind.DWord);
            }
        }

        private static void ExtractTo(string resource, string path, Action<long> onProgress)
        {
            var tmp = path + ".new";
            using (var src = Embedded.Open(resource))
            {
                if (src == null) throw new InvalidOperationException("This setup file is incomplete (missing " + resource + "). Download it again.");
                using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    var buf = new byte[1 << 18];
                    long copied = 0;
                    int n, tick = 0;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, n);
                        copied += n;
                        if (onProgress != null && (++tick % 4 == 0)) onProgress(copied);
                    }
                    onProgress?.Invoke(copied);
                }
            }
            // Swap in the new file; retry briefly in case something still holds the old one.
            for (int i = 0; ; i++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                    return;
                }
                catch (IOException) when (i < 10) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) when (i < 10) { Thread.Sleep(300); }
            }
        }

        private static void CloseRunningApp(string dir)
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExe)))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path == null || !SamePath(Path.GetDirectoryName(path), dir)) continue;
                    p.CloseMainWindow();
                    if (!p.WaitForExit(4000)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static bool SamePath(string a, string b) =>
            a != null && b != null &&
            string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        /// <summary>A short beat between steps so the progress reads as steps, not a flash.</summary>
        private static void Pace() => Thread.Sleep(220);

        public static string Mb(long bytes) =>
            bytes >= 1024L * 1024 * 1024 ? (bytes / (1024.0 * 1024 * 1024)).ToString("0.0") + " GB" : (bytes / (1024.0 * 1024)).ToString("0.0") + " MB";
    }
}
