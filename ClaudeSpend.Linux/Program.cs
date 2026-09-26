using System.Diagnostics;
using ClaudeSpend;

// claude-spend — Claude Code Spend for Linux.
//
// Serves the dashboard on 127.0.0.1 and opens it as an app window in a Chromium-family browser
// (Chromium, Chrome, Brave, Edge, Vivaldi), or in your default browser otherwise. A second
// launch reuses the running instance. The server exits when the app window closes, or a few
// minutes after the last open dashboard stops sending its heartbeat.

const int PreferredPort = 47821;
var idleLimit = TimeSpan.FromMinutes(3);

string root = LogReader.DefaultRoot();
string? export = null;
int port = PreferredPort;
bool openWindow = true;

for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--dir" or "-d": root = Next(); break;
        case "--port" or "-p": port = int.Parse(Next()); break;
        case "--export": export = Next(); break;
        case "--server" or "--no-browser": openWindow = false; break;
        case "--version" or "-v": Console.WriteLine($"claude-spend {Dashboard.Version}"); return 0;
        case "--help" or "-h":
            Console.WriteLine($"""
                Claude Code Spend {Dashboard.Version}
                See what your Claude Code usage would cost at Anthropic API list prices.

                Usage: claude-spend [options]
                  --dir <path>      Claude Code logs folder (default: {LogReader.DefaultRoot()})
                  --port <n>        Port to serve on (default: {PreferredPort}; falls back to a free one)
                  --server          Don't open a window; serve until Ctrl+C
                  --export <file>   Write all usage data as JSON and exit
                  --version         Print the version

                Environment:
                  CLAUDE_SPEND_BROWSER   Browser command to open the dashboard with
                  CLAUDE_CONFIG_DIR      Claude Code config dir (logs are in <dir>/projects)
                """);
            return 0;
        default:
            Console.Error.WriteLine($"Unknown option: {args[i]} (try --help)");
            return 2;
    }
}

if (export != null)
{
    File.WriteAllBytes(export, new DataStore(root).BuildJson());
    Console.WriteLine($"Wrote {export}");
    return 0;
}

if (!Directory.Exists(root))
{
    Notify($"Couldn't find Claude Code's logs at {root}. Run Claude Code at least once, or pass --dir <path>.");
    return 1;
}

// Already running? Just bring up another window on it.
if (openWindow && await DashboardServer.IsRunningOn(port))
{
    var url = $"http://127.0.0.1:{port}/";
    Console.WriteLine($"Claude Code Spend is already running at {url}");
    Launcher.Open(url, out _);
    return 0;
}

using var server = DashboardServer.StartOnFreePort(new DataStore(root), port);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

var serve = server.RunAsync(cts.Token);
Console.WriteLine($"Claude Code Spend {Dashboard.Version} — {server.Url}");
Console.WriteLine($"Reading logs from {root}");

if (!openWindow)
{
    Console.WriteLine("Serving until Ctrl+C.");
    await serve;
    return 0;
}

var window = Launcher.Open(server.Url, out var how);
Console.WriteLine($"Opened in {how}. This process exits when the window closes.");

// A dedicated app window: stop when it closes. (If the browser handed the window to an
// already-running instance, the process ends at once; fall back to the heartbeat then.)
if (window != null)
{
    var started = DateTime.UtcNow;
    try { await window.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { }
    if (DateTime.UtcNow - started > TimeSpan.FromSeconds(4) || cts.IsCancellationRequested)
    {
        cts.Cancel();
        await serve;
        return 0;
    }
}

// Browser tab: stay up while dashboards keep pinging (every 30 s).
var grace = DateTime.UtcNow + TimeSpan.FromMinutes(2);
while (!cts.IsCancellationRequested)
{
    try { await Task.Delay(TimeSpan.FromSeconds(10), cts.Token); } catch (OperationCanceledException) { break; }
    if (DateTime.UtcNow > grace && DateTime.UtcNow - server.LastRequestUtc > idleLimit)
    {
        Console.WriteLine("No open dashboards — exiting.");
        cts.Cancel();
    }
}
await serve;
return 0;

static void Notify(string message)
{
    Console.Error.WriteLine(message);
    if (OperatingSystem.IsLinux() && !Console.IsOutputRedirected) return;   // a terminal already shows it
    Launcher.TryRun("notify-send", "--app-name=Claude Code Spend", "--icon=claude-spend", "Claude Code Spend", message);
}

static class Launcher
{
    private static readonly string[] AppModeBrowsers =
    {
        "chromium", "chromium-browser", "google-chrome", "google-chrome-stable", "brave-browser", "brave",
        "microsoft-edge", "microsoft-edge-stable", "vivaldi", "vivaldi-stable",
    };

    /// <summary>Opens the dashboard. Returns the browser process when it owns a dedicated window.</summary>
    public static Process? Open(string url, out string how)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
        {
            how = "your default browser";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return null;
        }

        var custom = Environment.GetEnvironmentVariable("CLAUDE_SPEND_BROWSER");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            how = custom;
            return TryRun(custom, url);
        }

        foreach (var name in AppModeBrowsers)
        {
            var path = Which(name);
            if (path == null) continue;
            var args = new List<string>
            {
                "--app=" + url, "--class=claude-spend", "--name=claude-spend",
                "--window-size=1400,950", "--no-first-run", "--no-default-browser-check",
            };
            // Snap-packaged browsers can't write to hidden folders like ~/.local, so they share the normal profile.
            if (!IsSnap(path))
            {
                var profile = Path.Combine(DataDir(), "browser");
                Directory.CreateDirectory(profile);
                args.Add("--user-data-dir=" + profile);
            }
            var p = TryRun(path, args.ToArray());
            if (p != null) { how = $"an app window ({name})"; return p; }
        }

        how = "your default browser";
        TryRun("xdg-open", url);
        return null;
    }

    public static Process? TryRun(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            if (p == null) return null;
            // Drain browser chatter so it never blocks on a full pipe.
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }
        catch { return null; }
    }

    private static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':'))
        {
            if (dir.Length == 0) continue;
            var full = Path.Combine(dir, name);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static bool IsSnap(string path)
    {
        if (path.StartsWith("/snap/", StringComparison.Ordinal)) return true;
        try
        {
            var fi = new FileInfo(path);
            var target = fi.ResolveLinkTarget(true)?.FullName ?? path;
            if (target.StartsWith("/snap/", StringComparison.Ordinal) || target.EndsWith("/snap", StringComparison.Ordinal)) return true;
            // Ubuntu's /usr/bin/chromium-browser is a small script that execs the snap.
            if (fi.Length < 8192 && File.ReadAllText(path).Contains("/snap/", StringComparison.Ordinal)) return true;
        }
        catch { }
        return false;
    }

    private static string DataDir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdg;
        return Path.Combine(baseDir, "claude-spend");
    }
}
