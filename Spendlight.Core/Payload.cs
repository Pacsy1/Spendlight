using System.Diagnostics;
using System.Text.Json;

namespace Spendlight;

/// <summary>
/// Builds the /api/data JSON the dashboard consumes — the same shape spendlight_ui.py serves.
/// Records are compact arrays; see "fields" in the payload for the column order.
/// </summary>
public sealed class DataStore
{
    private readonly LogReader _reader;
    private readonly object _lock = new();

    public DataStore(string root) => _reader = new LogReader(root);

    public string Root => _reader.Root;

    public byte[] BuildJson()
    {
        List<UsageRecord> recs;
        Dictionary<string, string> titles, prompts;
        double seconds;
        int files;
        lock (_lock)
        {
            var sw = Stopwatch.StartNew();
            (recs, titles, prompts) = _reader.ReadAll();
            seconds = sw.Elapsed.TotalSeconds;
            files = _reader.FileCount;
        }

        var models = new Dictionary<string, int>();
        var projects = new Dictionary<string, int>();
        var sessions = new Dictionary<string, int>();
        var unpriced = new Dictionary<string, int>();
        var sessProj = new Dictionary<int, Dictionary<int, int>>();

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("generatedAt", DateTimeOffset.UtcNow.ToString("o"));
            w.WriteString("dir", _reader.Root);
            w.WriteNumber("parseSeconds", Math.Round(seconds, 3));
            w.WriteNumber("files", files);
            w.WriteString("priceSource", "Pricing.cs");
            w.WriteStartArray("fields");
            foreach (var f in new[] { "t", "model", "project", "session", "input", "cacheWrite", "cacheRead", "output", "webSearch",
                                      "costInput", "costCacheWrite", "costCacheRead", "costOutput", "costWeb", "costNoCache", "fast" })
                w.WriteStringValue(f);
            w.WriteEndArray();

            w.WriteStartArray("records");
            foreach (var r in recs)
            {
                var u = r.Usage;
                var b = Pricing.Cost(r.Model, u);
                if (b == null)
                {
                    unpriced[r.Model] = unpriced.GetValueOrDefault(r.Model) + 1;
                    b = new Pricing.Breakdown(0, 0, 0, 0, 0, 0);
                }
                int mi = IndexOf(models, r.Model), pi = IndexOf(projects, r.Project), si = IndexOf(sessions, r.Session);
                if (!sessProj.TryGetValue(si, out var pc)) sessProj[si] = pc = new Dictionary<int, int>();
                pc[pi] = pc.GetValueOrDefault(pi) + 1;

                w.WriteStartArray();
                w.WriteNumberValue(r.Epoch);
                w.WriteNumberValue(mi);
                w.WriteNumberValue(pi);
                w.WriteNumberValue(si);
                w.WriteNumberValue(u.Input);
                w.WriteNumberValue(u.Cache5m + u.Cache1h);
                w.WriteNumberValue(u.CacheRead);
                w.WriteNumberValue(u.Output);
                w.WriteNumberValue(u.WebSearch);
                w.WriteNumberValue(Math.Round(b.Input, 6));
                w.WriteNumberValue(Math.Round(b.CacheWrite, 6));
                w.WriteNumberValue(Math.Round(b.CacheRead, 6));
                w.WriteNumberValue(Math.Round(b.Output, 6));
                w.WriteNumberValue(Math.Round(b.Web, 6));
                w.WriteNumberValue(Math.Round(b.NoCache, 6));
                w.WriteNumberValue(u.Fast ? 1 : 0);
                w.WriteEndArray();
            }
            w.WriteEndArray();

            w.WriteStartArray("models");
            foreach (var m in models.Keys) w.WriteStringValue(m);
            w.WriteEndArray();

            var labels = ProjectLabels(projects.Keys.ToList());
            w.WriteStartArray("projects");
            foreach (var p in projects.Keys)
            {
                w.WriteStartObject();
                w.WriteString("path", p);
                w.WriteString("label", labels[p]);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            // A session belongs to the project it made the most calls in.
            w.WriteStartArray("sessions");
            foreach (var (id, si) in sessions)
            {
                w.WriteStartObject();
                w.WriteString("id", id);
                w.WriteString("title", titles.GetValueOrDefault(id) ?? prompts.GetValueOrDefault(id) ?? "");
                w.WriteNumber("project", sessProj.TryGetValue(si, out var pc) ? pc.MaxBy(kv => kv.Value).Key : 0);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("unpriced");
            foreach (var (m, n) in unpriced) w.WriteNumber(m, n);
            w.WriteEndObject();

            var used = models.Keys.Select(Pricing.Lookup).Where(p => p != null).ToHashSet();
            w.WriteStartArray("prices");
            foreach (var p in Pricing.Prices.OrderBy(p => -p.Input).ThenBy(p => p.Prefix, StringComparer.Ordinal))
            {
                var r = Pricing.RatesFor(p.Prefix)!;
                w.WriteStartObject();
                w.WriteString("model", p.Prefix);
                w.WriteBoolean("used", used.Contains(p.Prefix));
                w.WriteNumber("input", r.Input);
                w.WriteNumber("output", r.Output);
                w.WriteNumber("cache_write_5m", r.CacheWrite5m);
                w.WriteNumber("cache_write_1h", r.CacheWrite1h);
                w.WriteNumber("cache_read", Math.Round(r.CacheRead, 6));
                if (Pricing.FastPrices.TryGetValue(p.Prefix, out var f))
                {
                    w.WriteStartObject("fast");
                    w.WriteNumber("input", f.Input);
                    w.WriteNumber("output", f.Output);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("webSearchPer1k", Pricing.WebSearchPerRequest * 1000);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static int IndexOf(Dictionary<string, int> map, string key)
    {
        if (!map.TryGetValue(key, out var i)) map[key] = i = map.Count;
        return i;
    }

    /// <summary>Short, unique display names: the last path segment, or more when two collide.</summary>
    private static Dictionary<string, string> ProjectLabels(List<string> paths)
    {
        static string Tail(string p, int n)
        {
            var parts = p.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? p : string.Join("/", parts.Skip(Math.Max(0, parts.Length - n)));
        }
        var labels = paths.ToDictionary(p => p, p => Tail(p, 1));
        for (int n = 2; n <= 4; n++)
        {
            var clashes = labels.GroupBy(kv => kv.Value).Where(g => g.Count() > 1).SelectMany(g => g.Select(kv => kv.Key)).ToList();
            if (clashes.Count == 0) break;
            foreach (var p in clashes) labels[p] = Tail(p, n);
        }
        return labels;
    }
}
