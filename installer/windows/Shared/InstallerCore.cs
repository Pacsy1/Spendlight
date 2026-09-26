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
        private const string PayloadLicense = "payload/LICENSE.txt";
        public const string LicenseFile = "LICENSE.txt";
        public const string SourceUrl = "https://github.com/Pacsy1/claude-code-spend";

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
            // GPL: the license travels with the program.
            if (Embedded.Has(PayloadLicense)) ExtractTo(PayloadLicense, Path.Combine(dir, LicenseFile), null);

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

        /// <param name="fromDir">The install folder, when this uninstaller runs as a temp copy (see RelaunchFromTemp).</param>
        public static void Uninstall(bool removeData, Progress progress, string fromDir = null)
        {
            var info = GetInstalled();
            var dir = info?.Dir ?? fromDir ?? Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);

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

            progress(100, "done", "Removed");
        }

        /// <summary>Deletes only files Setup put there; the folder goes only if it ends up empty.</summary>
        private static void RemoveAppFiles(string dir)
        {
            TryDelete(Path.Combine(dir, AppExe));
            TryDelete(Path.Combine(dir, AppExe + ".new"));
            TryDelete(Path.Combine(dir, LicenseFile));
            var me = Assembly.GetEntryAssembly().Location;
            var uninstaller = Path.Combine(dir, UninstallExe);
            if (!SamePath(me, uninstaller))
            {
                // A just-exited original may still hold its file for a moment.
                for (int i = 0; i < 10 && File.Exists(uninstaller); i++) { TryDelete(uninstaller); if (File.Exists(uninstaller)) Thread.Sleep(300); }
            }
            try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }

        private const string TempCopyPrefix = "ClaudeSpend-Uninstall-";

        /// <summary>
        /// A running program can't delete its own file. So, like Inno Setup and NSIS, the installed
        /// Uninstall.exe copies itself to %TEMP%, starts that copy and exits; the copy then removes
        /// the installed files, including the original Uninstall.exe. Returns true if it relaunched
        /// (the caller should exit).
        /// </summary>
        public static bool RelaunchFromTemp(string[] args)
        {
            var me = Assembly.GetEntryAssembly().Location;
            var installed = GetInstalled();
            var myDir = Path.GetDirectoryName(me);
            bool runningFromInstall = installed != null ? SamePath(myDir, installed.Dir)
                                                        : string.Equals(Path.GetFileName(me), UninstallExe, StringComparison.OrdinalIgnoreCase);
            if (!runningFromInstall) return false;

            var copy = Path.Combine(Path.GetTempPath(), TempCopyPrefix + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            File.Copy(me, copy, true);
            var passOn = string.Join(" ", args.Select(Quote));
            Process.Start(new ProcessStartInfo(copy, $"{passOn} --from \"{myDir}\" --wait-pid {Process.GetCurrentProcess().Id}")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
            return true;
        }

        /// <summary>Removes temp uninstaller copies left by earlier uninstalls (never the running one).</summary>
        public static void CleanTempCopies()
        {
            var me = Assembly.GetEntryAssembly().Location;
            try
            {
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), TempCopyPrefix + "*.exe"))
                    if (!SamePath(f, me)) TryDelete(f);
            }
            catch { }
        }

        public static void WaitForExit(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) p.WaitForExit(10000); } catch { /* already gone */ }
        }

        private static string Quote(string a) => a.IndexOfAny(new[] { ' ', '"' }) >= 0 ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;

        private static void Register(string dir)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RegPath))
            {
                var exe = Path.Combine(dir, AppExe);
                var uninstaller = Path.Combine(dir, UninstallExe);
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", "Pacsy1");
                key.SetValue("URLInfoAbout", SourceUrl);
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

        /// <summary>
        /// Asks a running copy of the app (from this install folder only) to close, the same way
        /// clicking its X does. Setup never force-kills anything: if the app doesn't close, it
        /// stops and asks you to close it yourself.
        /// </summary>
        private static void CloseRunningApp(string dir)
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExe)))
            {
                bool stillRunning = false;
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path == null || !SamePath(Path.GetDirectoryName(path), dir)) continue;
                    p.CloseMainWindow();
                    stillRunning = !p.WaitForExit(8000);
                }
                catch { }
                finally { p.Dispose(); }
                if (stillRunning)
                    throw new InvalidOperationException($"{AppName} is still open. Close it, then choose Try again.");
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
