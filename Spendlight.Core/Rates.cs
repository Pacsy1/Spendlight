using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Spendlight;

/// <summary>
/// Currency conversion rates: the European Central Bank's daily euro reference rates.
/// Fetched only when the dashboard asks for them (the user picked a currency other than USD);
/// nothing about the user's usage is sent. Cached on disk for 12 hours, and an older cached copy
/// is served (marked stale) when there's no network. Same cache file and format as spendlight.py.
/// </summary>
public static class Rates
{
    private const string Url = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Spendlight/" + Dashboard.Version);
        return c;
    }

    private static string CachePath
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spendlight", "rates.json");
            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            var baseDir = string.IsNullOrEmpty(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
                : xdg;
            return Path.Combine(baseDir, "spendlight", "rates.json");
        }
    }

    private sealed record Snapshot(string Date, Dictionary<string, double> PerEur, double Fetched);

    /// <summary>{"source","date","base":"EUR","rates":{code: units per EUR},"stale"} as UTF-8 JSON.</summary>
    public static async Task<byte[]> GetJsonAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var cached = ReadCache();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            if (cached != null && now - cached.Fetched < MaxAge.TotalSeconds) return ToJson(cached, false);
            try
            {
                var xml = await Http.GetStringAsync(Url);
                var fresh = Parse(xml, now);
                WriteCache(fresh);
                return ToJson(fresh, false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or IOException)
            {
                if (cached != null) return ToJson(cached, true);
                throw new InvalidOperationException("Couldn't get exchange rates from the European Central Bank (" + ex.Message + ").");
            }
        }
        finally { Gate.Release(); }
    }

    private static Snapshot Parse(string xml, double fetched)
    {
        var day = Regex.Match(xml, @"time=['""](\d{4}-\d{2}-\d{2})['""]");
        var rates = new Dictionary<string, double>();
        foreach (Match m in Regex.Matches(xml, @"currency=['""]([A-Z]{3})['""]\s+rate=['""]([0-9.]+)['""]"))
            rates[m.Groups[1].Value] = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (!day.Success || !rates.ContainsKey("USD")) throw new FormatException("unexpected response");
        rates["EUR"] = 1.0;
        return new Snapshot(day.Groups[1].Value, rates, fetched);
    }

    private static Snapshot? ReadCache()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(CachePath));
            var root = doc.RootElement;
            var rates = new Dictionary<string, double>();
            foreach (var p in root.GetProperty("rates").EnumerateObject()) rates[p.Name] = p.Value.GetDouble();
            var fetched = root.TryGetProperty("fetched", out var f) ? f.GetDouble() : 0;
            return new Snapshot(root.GetProperty("date").GetString() ?? "", rates, fetched);
        }
        catch { return null; }
    }

    private static void WriteCache(Snapshot s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllBytes(CachePath, Serialize(s, null, includeFetched: true));
        }
        catch { /* a cache is a nicety */ }
    }

    private static byte[] ToJson(Snapshot s, bool stale) => Serialize(s, stale, includeFetched: false);

    private static byte[] Serialize(Snapshot s, bool? stale, bool includeFetched)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("source", "European Central Bank");
            w.WriteString("date", s.Date);
            w.WriteString("base", "EUR");
            w.WriteStartObject("rates");
            foreach (var (k, v) in s.PerEur) w.WriteNumber(k, v);
            w.WriteEndObject();
            if (includeFetched) w.WriteNumber("fetched", s.Fetched);
            if (stale != null) w.WriteBoolean("stale", stale.Value);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>{"error": message} as UTF-8 JSON.</summary>
    public static byte[] ErrorJson(string message)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("error", message);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }
}
