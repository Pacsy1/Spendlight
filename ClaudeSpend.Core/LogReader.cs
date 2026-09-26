using System.Globalization;
using System.Text.Json;

namespace ClaudeSpend;

public record struct Usage(long Input, long Cache5m, long Cache1h, long CacheRead, long Output, long WebSearch, bool Fast)
{
    /// <summary>Field-wise maximum: one response logged on several lines keeps its largest counts.</summary>
    public Usage Max(Usage o) => new(
        Math.Max(Input, o.Input), Math.Max(Cache5m, o.Cache5m), Math.Max(Cache1h, o.Cache1h),
        Math.Max(CacheRead, o.CacheRead), Math.Max(Output, o.Output), Math.Max(WebSearch, o.WebSearch), Fast || o.Fast);
}

public sealed class UsageRecord
{
    public required string Key;
    public required string Model;
    public required long Epoch;      // seconds, UTC
    public required string Project;  // working directory
    public required string Session;
    public Usage Usage;
}

/// <summary>
/// Reads Claude Code session logs (~/.claude/projects/**/*.jsonl).
///
/// Every assistant turn is a line with message.model and message.usage. One API response is
/// often logged as several lines (one per content block) repeating the same usage, and resumed
/// sessions copy earlier history into a new file, so records are de-duplicated by
/// message id + request id.
/// </summary>
public sealed class LogReader
{
    private sealed record Parsed(List<UsageRecord> Records, Dictionary<string, string> Titles, Dictionary<string, string> Prompts);

    private readonly Dictionary<string, (long Size, DateTime Mtime, Parsed Data)> _cache = new();

    public string Root { get; }
    public int FileCount => _cache.Count;

    public LogReader(string root) => Root = root;

    public static string DefaultRoot()
    {
        var cfg = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var baseDir = string.IsNullOrEmpty(cfg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : cfg;
        return Path.Combine(baseDir, "projects");
    }

    public (List<UsageRecord> Records, Dictionary<string, string> Titles, Dictionary<string, string> Prompts) ReadAll()
    {
        var seen = new Dictionary<string, UsageRecord>();
        var titles = new Dictionary<string, string>();
        var prompts = new Dictionary<string, string>();
        var live = new HashSet<string>();

        var files = Directory.Exists(Root)
            ? Directory.EnumerateFiles(Root, "*.jsonl", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
            : Enumerable.Empty<string>();

        foreach (var path in files)
        {
            FileInfo fi;
            try { fi = new FileInfo(path); _ = fi.Length; } catch { continue; }
            live.Add(path);
            Parsed data;
            if (_cache.TryGetValue(path, out var c) && c.Size == fi.Length && c.Mtime == fi.LastWriteTimeUtc)
                data = c.Data;
            else
            {
                data = ParseFile(path);
                _cache[path] = (fi.Length, fi.LastWriteTimeUtc, data);
            }

            foreach (var (k, v) in data.Titles) titles[k] = v;
            foreach (var (k, v) in data.Prompts) prompts.TryAdd(k, v);
            foreach (var r in data.Records)
            {
                if (seen.TryGetValue(r.Key, out var prev))
                    prev.Usage = prev.Usage.Max(r.Usage);
                else
                    seen[r.Key] = new UsageRecord   // copy: cached records must stay untouched
                    {
                        Key = r.Key, Model = r.Model, Epoch = r.Epoch, Project = r.Project, Session = r.Session, Usage = r.Usage,
                    };
            }
        }
        foreach (var stale in _cache.Keys.Where(k => !live.Contains(k)).ToList()) _cache.Remove(stale);

        var list = seen.Values.ToList();
        list.Sort((a, b) => a.Epoch.CompareTo(b.Epoch));
        return (list, titles, prompts);
    }

    private static Parsed ParseFile(string path)
    {
        var records = new List<UsageRecord>();
        var titles = new Dictionary<string, string>();
        var prompts = new Dictionary<string, string>();
        var stem = Path.GetFileNameWithoutExtension(path);
        var parentName = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";

        IEnumerable<string> lines;
        try
        {
            // FileShare.ReadWrite: Claude Code may be appending to the file right now.
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            lines = ReadLines(new StreamReader(fs));
        }
        catch { return new Parsed(records, titles, prompts); }

        foreach (var line in lines)
        {
            bool isUsage = line.Contains("\"usage\"", StringComparison.Ordinal);
            bool isTitle = line.Contains("\"custom-title\"", StringComparison.Ordinal);
            bool isUser = line.Contains("\"type\":\"user\"", StringComparison.Ordinal);
            if (!(isUsage || isTitle || isUser)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            using (doc)
            {
                var rec = doc.RootElement;
                if (rec.ValueKind != JsonValueKind.Object) continue;
                var kind = Str(rec, "type");
                var sid = Str(rec, "sessionId") ?? stem;

                if (kind == "custom-title")
                {
                    var t = Str(rec, "customTitle");
                    if (!string.IsNullOrEmpty(t)) titles[sid] = t;
                    continue;
                }
                if (!rec.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;

                if (kind == "user")
                {
                    if (!prompts.ContainsKey(sid) && !(rec.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True))
                    {
                        var text = FirstPromptText(msg);
                        if (text != null) prompts[sid] = text;
                    }
                    continue;
                }

                if (!msg.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;
                var model = Str(msg, "model") ?? "unknown";
                if (model.StartsWith('<')) continue;   // "<synthetic>" = local, not billed

                var ts = Str(rec, "timestamp") ?? "";
                if (!DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)) continue;

                var id = Str(msg, "id");
                var key = id != null ? id + "|" + Str(rec, "requestId") : path + "|" + Str(rec, "uuid");
                records.Add(new UsageRecord
                {
                    Key = key,
                    Model = model,
                    Epoch = when.ToUnixTimeSeconds(),
                    Project = Str(rec, "cwd") ?? parentName,
                    Session = sid,
                    Usage = ParseUsage(usage),
                });
            }
        }
        return new Parsed(records, titles, prompts);
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        using (reader)
        {
            string? line;
            while ((line = reader.ReadLine()) != null) yield return line;
        }
    }

    private static Usage ParseUsage(JsonElement u)
    {
        long created = Num(u, "cache_creation_input_tokens");
        long c1h = 0, c5m = 0;
        if (u.TryGetProperty("cache_creation", out var split) && split.ValueKind == JsonValueKind.Object)
        {
            c1h = Num(split, "ephemeral_1h_input_tokens");
            c5m = Num(split, "ephemeral_5m_input_tokens");
        }
        if (c1h + c5m < created) c5m = created - c1h;   // older logs have no TTL split: assume 5m
        long web = 0;
        if (u.TryGetProperty("server_tool_use", out var stu) && stu.ValueKind == JsonValueKind.Object)
            web = Num(stu, "web_search_requests");
        return new Usage(
            Num(u, "input_tokens"), c5m, c1h, Num(u, "cache_read_input_tokens"), Num(u, "output_tokens"),
            web, Str(u, "speed") == "fast");
    }

    private static string? FirstPromptText(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var content)) return null;
        string text;
        if (content.ValueKind == JsonValueKind.String) text = content.GetString() ?? "";
        else if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = content.EnumerateArray()
                .Where(b => b.ValueKind == JsonValueKind.Object && Str(b, "type") == "text")
                .Select(b => Str(b, "text") ?? "").ToList();
            if (parts.Count == 0) return null;
            text = string.Join(" ", parts);
        }
        else return null;

        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        // Skip Claude Code's own wrappers (slash-command echoes, reminders, etc.)
        if (text.Length == 0 || text.StartsWith('<')) return null;
        return text.Length > 120 ? text[..120] : text;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? (v.TryGetInt64(out var l) ? l : (long)v.GetDouble())
            : 0;
}
