using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace SpendlightSetup
{
    /// <summary>What is on disk and in the registry, and how to put it there or take it away.
    /// Installs per user (no administrator rights): %LOCALAPPDATA%\Programs\Spendlight.
    ///
    /// Ground rules: Setup only ever touches its own install. It deletes files by exact name,
    /// removes a shortcut only if it points into the install being removed, removes the registry
    /// entry only if it describes that install, and never force-kills a process.</summary>
    internal static class InstallerCore
    {
        public const string AppName = "Spendlight";
        public const string AppExe = "Spendlight.exe";
        public const string UninstallExe = "Uninstall.exe";
        public const string LicenseFile = "LICENSE.txt";
        public const string SourceUrl = "https://github.com/Pacsy1/spendlight";
        private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Spendlight";
        private const string PayloadApp = "payload/Spendlight.exe";
        private const string PayloadUninstaller = "payload/Uninstall.exe";
        private const string PayloadLicense = "payload/LICENSE.txt";
        private const string TempCopyPrefix = "Spendlight-Uninstall-";
        private const string ShortcutText = "See what your Claude Code usage would cost";

        // The app's earlier name. Installing Spendlight cleans an old install of it up.
        private const string LegacyName = "Claude Code Spend";
        private const string LegacyExe = "ClaudeSpend.exe";
        private const string LegacyRegPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClaudeCodeSpend";
        private const string LegacyTempPrefix = "ClaudeSpend-Uninstall-";

        /// <summary>Progress callback: percent (0-100), step id, human-readable detail.</summary>
        public delegate void Progress(int percent, string step, string detail);

        /// <summary>What an install did, for the finish page.</summary>
        public sealed class Result
        {
            public string StartMenu = "skipped";   // created | skipped | failed
            public string Desktop = "skipped";
            public readonly List<string> Warnings = new List<string>();
            public bool RemovedLegacy;
        }

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

        private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static string DefaultDir => Path.Combine(LocalAppData, "Programs", AppName);
        public static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
        public static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

        /// <summary>The app's own WebView2 profile (saved filters, theme).</summary>
        public static string AppDataDir => Path.Combine(LocalAppData, AppName);

        public static long RequiredBytes => Embedded.Length(PayloadApp) + Embedded.Length(PayloadUninstaller);

        // ───────────────────────────── log ─────────────────────────────

        /// <summary>Every run is logged to %TEMP%\Spendlight-Setup.log, so a failure can be diagnosed.</summary>
        public static readonly string LogPath = Path.Combine(Path.GetTempPath(), "Spendlight-Setup.log");
        private static readonly object LogLock = new object();

        public static void Log(string message)
        {
            try
            {
                lock (LogLock)
                {
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > 512 * 1024) info.Delete();   // keep it small
                    // UTF-8 with a byte-order mark, so Notepad and PowerShell show accented paths correctly.
                    File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}", System.Text.Encoding.UTF8);
                }
            }
            catch { }
        }

        // ───────────────────────────── what's installed ─────────────────────────────

        public sealed class Installed
        {
            public string Version;
            public string Dir;
            public bool Desktop;      // what was chosen last time
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
                    Desktop = !(key.GetValue("ShortcutDesktop") is int d) || d != 0,
                    StartMenu = !(key.GetValue("ShortcutStartMenu") is int s) || s != 0,
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

        // ───────────────────────────── install ─────────────────────────────

        public static Result Install(string dir, bool desktop, bool startMenu, Progress progress)
        {
            var result = new Result();
            dir = ValidateDir(dir);
            var previous = GetInstalled();
            Log($"Install {Version} into {dir} (desktop={desktop}, startMenu={startMenu}, previous={previous?.Dir ?? "none"})");

            progress(2, "prepare", "Checking for a running copy…");
            CloseRunningApp(dir, AppExe);
            if (previous != null && !SamePath(previous.Dir, dir))
            {
                progress(4, "prepare", "Moving from " + previous.Dir);
                CloseRunningApp(previous.Dir, AppExe);
            }
            Pace();

            // 1. Files. The app is the bulk of the work.
            var target = Path.Combine(dir, AppExe);
            var total = Embedded.Length(PayloadApp);
            ExtractTo(PayloadApp, target, copied =>
                progress(6 + (int)(70 * copied / Math.Max(total, 1)), "copy", $"Copying {Mb(copied)} of {Mb(total)}"));
            progress(77, "copy", "Adding the uninstaller");
            ExtractTo(PayloadUninstaller, Path.Combine(dir, UninstallExe), null);
            // GPL: the license travels with the program.
            if (Embedded.Has(PayloadLicense)) ExtractTo(PayloadLicense, Path.Combine(dir, LicenseFile), null);
            Log("Files copied");
            Pace();

            // 2. Register right away, so the app can always be uninstalled from Settings › Apps,
            //    even if something later (like a shortcut) doesn't work out.
            progress(82, "register", "Adding to Apps & features");
            Register(dir, desktop, startMenu);
            Log("Registered");
            if (previous != null && !SamePath(previous.Dir, dir))
            {
                RemoveShortcutsInto(previous.Dir);
                RemoveAppFiles(previous.Dir);
                Log("Removed previous install in " + previous.Dir);
            }
            result.RemovedLegacy = RemoveLegacyInstall(progress);
            Pace();

            // 3. Shortcuts. A shortcut that can't be made is reported, never fatal.
            progress(90, "shortcuts", startMenu ? "Adding to the Start menu" : "Skipping the Start menu");
            result.StartMenu = ApplyShortcut(startMenu, StartMenuShortcut, target, dir, "Start menu", result);
            Pace();
            progress(95, "shortcuts", desktop ? "Adding a desktop shortcut" : "Skipping the desktop shortcut");
            result.Desktop = ApplyShortcut(desktop, DesktopShortcut, target, dir, "desktop", result);
            Pace();

            progress(100, "done", "Installed");
            Log($"Install finished (startMenu={result.StartMenu}, desktop={result.Desktop}, legacyRemoved={result.RemovedLegacy})");
            return result;
        }

        /// <summary>Creates (or, when not wanted, removes our own) shortcut. Returns created/skipped/failed.</summary>
        private static string ApplyShortcut(bool wanted, string lnk, string target, string dir, string where, Result result)
        {
            if (!wanted)
            {
                if (Shortcut.PointsInto(lnk, dir)) TryDelete(lnk);
                return "skipped";
            }
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    Shortcut.Create(lnk, target, dir, ShortcutText);
                    if (!File.Exists(lnk)) throw new IOException("The shortcut file wasn't there after saving it.");
                    Log($"Created {where} shortcut: {lnk}");
                    return "created";
                }
                catch (Exception ex) when (attempt < 3)
                {
                    // e.g. a stale file or a cloud-synced desktop that was busy: clear the way and retry.
                    Log($"{where} shortcut attempt {attempt} failed: {ex.GetType().Name}: {ex.Message}");
                    TryDelete(lnk);
                    Thread.Sleep(400 * attempt);
                }
                catch (Exception ex)
                {
                    Log($"{where} shortcut failed: {ex.GetType().Name}: {ex.Message}");
                    // Shown under "Couldn't create the … shortcut" on the finish page.
                    result.Warnings.Add($"{where}: {ex.Message.Split('(')[0].Trim()} You can still open Spendlight from {dir}.");
                    return "failed";
                }
            }
        }

        // ───────────────────────────── the old name ─────────────────────────────

        /// <summary>
        /// Removes an install of the app under its old name ("Claude Code Spend"), including a
        /// half-finished one that never got registered. Only its own files, by exact name.
        /// </summary>
        private static bool RemoveLegacyInstall(Progress progress)
        {
            string dir = null;
            using (var key = Registry.CurrentUser.OpenSubKey(LegacyRegPath))
                dir = key?.GetValue("InstallLocation") as string;
            var defaultLegacyDir = Path.Combine(LocalAppData, "Programs", LegacyName);
            if (dir == null && File.Exists(Path.Combine(defaultLegacyDir, LegacyExe))) dir = defaultLegacyDir;
            if (dir == null) return false;

            progress(86, "register", "Removing the old Claude Code Spend");
            Log("Removing legacy install in " + dir);
            try
            {
                CloseRunningApp(dir, LegacyExe);
                foreach (var lnk in new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), LegacyName + ".lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), LegacyName + ".lnk"),
                })
                    if (Shortcut.PointsInto(lnk, dir)) TryDelete(lnk);
                foreach (var f in new[] { LegacyExe, LegacyExe + ".new", UninstallExe, UninstallExe + ".new", LicenseFile })
                    TryDelete(Path.Combine(dir, f));
                try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
                bool ownsKey;
                using (var key = Registry.CurrentUser.OpenSubKey(LegacyRegPath))
                    ownsKey = key != null && SamePath(key.GetValue("InstallLocation") as string, dir);
                if (ownsKey) Registry.CurrentUser.DeleteSubKeyTree(LegacyRegPath, false);
                // The old app's window data (%LOCALAPPDATA%\ClaudeSpend) is left alone: it's yours,
                // and its saved view settings belong to the old app's page, so they can't carry over.
                CleanTempCopies(LegacyTempPrefix);
            }
            catch (Exception ex)
            {
                Log("Legacy cleanup incomplete: " + ex.Message);   // never block the new install over it
            }
            return true;
        }

        // ───────────────────────────── uninstall ─────────────────────────────

        /// <summary>Removes the install this uninstaller belongs to — nothing else.</summary>
        /// <param name="fromDir">The install folder, when this uninstaller runs as a temp copy (see RelaunchFromTemp).</param>
        public static void Uninstall(bool removeData, Progress progress, string fromDir = null)
        {
            var registered = GetInstalled();
            var dir = fromDir ?? registered?.Dir;
            if (dir == null) throw new InvalidOperationException("Spendlight isn't installed.");
            Log($"Uninstall from {dir} (removeData={removeData})");

            progress(5, "close", "Closing Spendlight if it's open");
            CloseRunningApp(dir, AppExe);
            Pace();

            progress(30, "shortcuts", "Removing shortcuts");
            RemoveShortcutsInto(dir);
            Pace();

            progress(55, "files", "Removing app files");
            RemoveAppFiles(dir);
            Pace();

            progress(75, "register", "Removing from Apps & features");
            if (registered != null && SamePath(registered.Dir, dir))
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
            Log("Uninstall finished");
        }

        /// <summary>Our Start menu and desktop shortcuts, but only if they point into this install.</summary>
        private static void RemoveShortcutsInto(string dir)
        {
            foreach (var lnk in new[] { StartMenuShortcut, DesktopShortcut })
                if (Shortcut.PointsInto(lnk, dir)) TryDelete(lnk);
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

        /// <summary>
        /// A running program can't delete its own file. So, like Inno Setup and NSIS, an installed
        /// Uninstall.exe copies itself to %TEMP%, starts that copy for its own folder and exits; the
        /// copy then removes the install, including the original Uninstall.exe. Returns true if it
        /// relaunched (the caller should exit).
        /// </summary>
        public static bool RelaunchFromTemp(string[] args)
        {
            var me = Assembly.GetEntryAssembly().Location;
            if (!string.Equals(Path.GetFileName(me), UninstallExe, StringComparison.OrdinalIgnoreCase)) return false;
            var myDir = Path.GetDirectoryName(me);
            if (!File.Exists(Path.Combine(myDir, AppExe)) && !SamePath(GetInstalled()?.Dir, myDir)) return false;

            var copy = Path.Combine(Path.GetTempPath(), TempCopyPrefix + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            File.Copy(me, copy, true);
            var passOn = string.Join(" ", args.Select(Quote));
            Log($"Uninstaller handing over to {copy} for {myDir}");
            Process.Start(new ProcessStartInfo(copy, $"{passOn} --from \"{myDir}\" --wait-pid {Process.GetCurrentProcess().Id}")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
            return true;
        }

        /// <summary>Removes temp uninstaller copies left by earlier uninstalls (never the running one).</summary>
        public static void CleanTempCopies(string prefix = TempCopyPrefix)
        {
            var me = Assembly.GetEntryAssembly().Location;
            try
            {
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), prefix + "*.exe"))
                    if (!SamePath(f, me)) TryDelete(f);
            }
            catch { }
        }

        public static void WaitForExit(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) p.WaitForExit(10000); } catch { /* already gone */ }
        }

        private static string Quote(string a) => a.IndexOfAny(new[] { ' ', '"' }) >= 0 ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;

        private static void Register(string dir, bool desktop, bool startMenu)
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
                // Remembered so an update offers the same choices.
                key.SetValue("ShortcutDesktop", desktop ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("ShortcutStartMenu", startMenu ? 1 : 0, RegistryValueKind.DWord);
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
        /// Asks a running copy of the app (from that folder only) to close, the same way clicking
        /// its X does. Setup never force-kills anything: if the app doesn't close, it stops and asks
        /// you to close it yourself.
        /// </summary>
        private static void CloseRunningApp(string dir, string exeName)
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName)))
            {
                bool stillRunning = false;
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path == null || !SamePath(Path.GetDirectoryName(path), dir)) continue;
                    Log($"Asking {path} (pid {p.Id}) to close");
                    p.CloseMainWindow();
                    stillRunning = !p.WaitForExit(8000);
                }
                catch { }
                finally { p.Dispose(); }
                if (stillRunning)
                    throw new InvalidOperationException($"{Path.GetFileNameWithoutExtension(exeName)} is still open. Close it, then choose Try again.");
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Log($"Couldn't delete {path}: {ex.Message}"); }
        }

        internal static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>A short beat between steps so the progress reads as steps, not a flash.</summary>
        private static void Pace() => Thread.Sleep(220);

        public static string Mb(long bytes) =>
            bytes >= 1024L * 1024 * 1024 ? (bytes / (1024.0 * 1024 * 1024)).ToString("0.0") + " GB" : (bytes / (1024.0 * 1024)).ToString("0.0") + " MB";
    }
}
