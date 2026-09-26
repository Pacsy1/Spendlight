using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ClaudeSpendSetup
{
    /// <summary>Resources compiled into the setup exe: the app, the uninstaller, WebView2's DLLs, the UI.</summary>
    internal static class Embedded
    {
        private static readonly Assembly Me = typeof(Embedded).Assembly;

        public static Stream Open(string name) => Me.GetManifestResourceStream(name);
        public static bool Has(string name) => Me.GetManifestResourceInfo(name) != null;

        public static long Length(string name)
        {
            using (var s = Open(name)) return s?.Length ?? 0;
        }

        public static string Text(string name)
        {
            using (var s = Open(name))
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }

        /// <summary>Loads WebView2's managed DLLs from resources, so Setup is a single file.</summary>
        public static Assembly ResolveAssembly(object sender, ResolveEventArgs e)
        {
            var name = new AssemblyName(e.Name).Name;
            using (var s = Open("lib/" + name + ".dll"))
            {
                if (s == null) return null;
                var buf = new byte[s.Length];
                int read = 0;
                while (read < buf.Length) read += s.Read(buf, read, buf.Length - read);
                return Assembly.Load(buf);
            }
        }

        /// <summary>Setup's scratch folder in %TEMP% (WebView2 loader and browser profile).</summary>
        public static string TempRoot => Path.Combine(Path.GetTempPath(), "ClaudeSpendSetup");

        /// <summary>WebView2Loader.dll is native, so it's written to a temp folder and loaded from there.</summary>
        public static string ExtractLoader()
        {
            var dir = Path.Combine(TempRoot, "loader-" + InstallerCore.Version);
            var path = Path.Combine(dir, "WebView2Loader.dll");
            Directory.CreateDirectory(dir);
            if (!File.Exists(path) || new FileInfo(path).Length != Length("lib/WebView2Loader.dll"))
            {
                using (var src = Open("lib/WebView2Loader.dll"))
                using (var dst = File.Create(path)) src.CopyTo(dst);
            }
            return dir;
        }
    }

    internal static class Native
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void SetDpiAware()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }   // per-monitor v2
            try { SetProcessDPIAware(); } catch { }
        }

        /// <summary>Lets the user drag a borderless window from any element marked as a drag area.</summary>
        public static void BeginDrag(IntPtr hwnd)
        {
            ReleaseCapture();
            SendMessage(hwnd, 0xA1 /* WM_NCLBUTTONDOWN */, new IntPtr(2) /* HTCAPTION */, IntPtr.Zero);
        }

        public static void StyleWindow(IntPtr hwnd, bool dark)
        {
            int round = 2;                                   // DWMWCP_ROUND — Windows 11 rounded corners
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
            int d = dark ? 1 : 0;                            // dark border/shadow tint
            DwmSetWindowAttribute(hwnd, 20, ref d, sizeof(int));
        }

        public static bool SystemUsesDarkTheme()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }
    }

    /// <summary>.lnk creation through the shell's own IShellLink — no scripting host needed.</summary>
    internal static class Shortcut
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        public static void Create(string lnkPath, string target, string workingDir, string description)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath));
            var link = (IShellLinkW)new CShellLink();
            try
            {
                link.SetPath(target);
                link.SetWorkingDirectory(workingDir);
                link.SetDescription(description);
                link.SetIconLocation(target, 0);
                ((IPersistFile)link).Save(lnkPath, false);
            }
            finally { Marshal.ReleaseComObject(link); }
        }
    }

    /// <summary>The modern Windows folder picker (IFileOpenDialog in folder mode).</summary>
    internal static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRcw { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem ppv);

        public static string Pick(IntPtr owner, string title, string startIn)
        {
            var dlg = (IFileDialog)new FileOpenDialogRcw();
            try
            {
                dlg.SetOptions(0x20 | 0x40 | 0x800);   // PICKFOLDERS | FORCEFILESYSTEM | PATHMUSTEXIST
                dlg.SetTitle(title);
                dlg.SetOkButtonLabel("Select folder");
                var start = startIn;
                while (!string.IsNullOrEmpty(start) && !Directory.Exists(start)) start = Path.GetDirectoryName(start);
                if (!string.IsNullOrEmpty(start))
                {
                    try
                    {
                        SHCreateItemFromParsingName(start, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
                        dlg.SetFolder(item);
                    }
                    catch { }
                }
                if (dlg.Show(owner) != 0) return null;   // cancelled
                dlg.GetResult(out var result);
                result.GetDisplayName(0x80058000 /* SIGDN_FILESYSPATH */, out var path);
                return path;
            }
            finally { Marshal.ReleaseComObject(dlg); }
        }
    }
}
