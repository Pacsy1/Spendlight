using System.Reflection;

namespace ClaudeSpend;

public static class Dashboard
{
    private static byte[]? _html;

    /// <summary>The dashboard page (dashboard.html), embedded at build time.</summary>
    public static byte[] Html()
    {
        if (_html != null) return _html;
        using var s = typeof(Dashboard).Assembly.GetManifestResourceStream("dashboard.html")
                      ?? throw new InvalidOperationException("dashboard.html is missing from the build.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return _html = ms.ToArray();
    }

    public static string Version =>
        typeof(Dashboard).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
