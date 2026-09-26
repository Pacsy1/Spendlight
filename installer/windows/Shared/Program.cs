using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace ClaudeSpendSetup
{
    /// <summary>
    /// ClaudeSpend-Setup.exe [--quiet] [--dir PATH] [--no-desktop] [--no-startmenu] [--launch]
    /// Uninstall.exe          [--uninstall] [--quiet] [--remove-data]
    /// </summary>
    internal sealed class Options
    {
        public bool Uninstall, Quiet, NoDesktop, NoStartMenu, Launch, RemoveData, Debug;
        public string Dir, Page, Theme;
        public string FromDir;    // set on the temp copy of the uninstaller: the install folder
        public int WaitPid;       // the original uninstaller, which must exit before its file can go

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i].ToLowerInvariant();
                switch (a)
                {
                    case "--uninstall": case "/uninstall": o.Uninstall = true; break;
                    case "--quiet": case "-q": case "/s": case "/quiet": case "/silent": o.Quiet = true; break;
                    case "--no-desktop": o.NoDesktop = true; break;
                    case "--no-startmenu": o.NoStartMenu = true; break;
                    case "--launch": o.Launch = true; break;
                    case "--remove-data": o.RemoveData = true; break;
                    case "--debug": o.Debug = true; break;
                    case "--dir": case "/dir": if (i + 1 < args.Length) o.Dir = args[++i]; break;
                    case "--page": if (i + 1 < args.Length) o.Page = args[++i]; break;   // design preview
                    case "--theme": if (i + 1 < args.Length) o.Theme = args[++i]; break; // design preview
                    case "--from": if (i + 1 < args.Length) o.FromDir = args[++i]; break;
                    case "--wait-pid": if (i + 1 < args.Length) int.TryParse(args[++i], out o.WaitPid); break;
                }
            }
            // The uninstaller carries no app payload, so it can only uninstall.
            if (!InstallerCore.HasPayload) o.Uninstall = true;
            return o;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Must be hooked before any WebView2 type is touched.
            AppDomain.CurrentDomain.AssemblyResolve += Embedded.ResolveAssembly;
            return Run(args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            var opts = Options.Parse(args);

            // The installed Uninstall.exe hands over to a temp copy of itself, then exits.
            if (opts.Uninstall && opts.FromDir == null && InstallerCore.RelaunchFromTemp(args)) return 0;
            if (opts.WaitPid > 0) InstallerCore.WaitForExit(opts.WaitPid);
            InstallerCore.CleanTempCopies();

            if (opts.Quiet) return RunQuiet(opts);

            Native.SetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(opts));
            CleanTemp();
            return 0;
        }

        private static int RunQuiet(Options opts)
        {
            try
            {
                if (opts.Uninstall)
                    InstallerCore.Uninstall(opts.RemoveData, (p, s, d) => { }, opts.FromDir);
                else
                {
                    InstallerCore.Install(opts.Dir ?? InstallerCore.GetInstalled()?.Dir ?? InstallerCore.DefaultDir,
                        !opts.NoDesktop, !opts.NoStartMenu, (p, s, d) => { });
                    if (opts.Launch)
                    {
                        var exe = Path.Combine(InstallerCore.GetInstalled().Dir, InstallerCore.AppExe);
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "ClaudeSpend-Setup-error.txt"), ex.ToString()); } catch { }
                return 1;
            }
        }

        /// <summary>WebView2 releases its files a moment after the window closes.</summary>
        private static void CleanTemp()
        {
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    var web = Path.Combine(Embedded.TempRoot, "webview");
                    if (Directory.Exists(web)) Directory.Delete(web, true);
                    return;
                }
                catch { System.Threading.Thread.Sleep(500); }
            }
        }
    }
}
