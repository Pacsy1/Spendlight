namespace ClaudeSpend;

/// <summary>
/// Anthropic first-party API list prices, USD per million tokens.
/// Cache writes: 1.25x input (5-minute TTL), 2x input (1-hour TTL).
/// Cache reads: 0.1x input unless a model has its own rate.
/// </summary>
public static class Pricing
{
    // model-id prefix -> (input, output, cache read or null for 0.1x input)
    public static readonly (string Prefix, double Input, double Output, double? CacheRead)[] Prices =
    {
        ("claude-fable-5-1",  10.00, 50.00, 0.25),
        ("claude-mythos-5-1", 10.00, 50.00, 0.25),
        ("claude-fable-5",    10.00, 50.00, 1.00),
        ("claude-mythos-5",   10.00, 50.00, 1.00),
        ("claude-opus-5-5",    4.00, 20.00, 0.20),
        ("claude-opus-5",      5.00, 25.00, null),
        ("claude-opus-4-8",    5.00, 25.00, null),
        ("claude-opus-4-7",    5.00, 25.00, null),
        ("claude-opus-4-6",    5.00, 25.00, null),
        ("claude-opus-4-5",    5.00, 25.00, null),
        ("claude-opus-4-1",   15.00, 75.00, null),
        ("claude-opus-4",     15.00, 75.00, null),
        ("claude-sonnet-5",    2.00, 10.00, null),
        ("claude-sonnet-4",    3.00, 15.00, null),   // 4, 4.5, 4.6
        ("claude-3-7-sonnet",  3.00, 15.00, null),
        ("claude-haiku-4-5",   1.00,  5.00, null),
        ("claude-3-5-haiku",   0.80,  4.00, null),
        ("claude-3-haiku",     0.25,  1.25, null),
    };

    // Fast mode (Opus only) has its own input/output rates.
    public static readonly Dictionary<string, (double Input, double Output)> FastPrices = new()
    {
        ["claude-opus-5-5"] = (8.00, 40.00),
        ["claude-opus-5"] = (10.00, 50.00),
    };

    public const double WebSearchPerRequest = 10.00 / 1000;

    public record Rates(double Input, double Output, double CacheWrite5m, double CacheWrite1h, double CacheRead);

    /// <summary>Longest matching prefix wins, so claude-opus-5-5 beats claude-opus-5.</summary>
    public static string? Lookup(string model)
    {
        var m = model.ToLowerInvariant();
        string? best = null;
        foreach (var p in Prices)
            if (m.StartsWith(p.Prefix, StringComparison.Ordinal) && (best == null || p.Prefix.Length > best.Length))
                best = p.Prefix;
        return best;
    }

    public static Rates? RatesFor(string model, bool fast = false)
    {
        var prefix = Lookup(model);
        if (prefix == null) return null;
        var p = Prices.First(x => x.Prefix == prefix);
        double input = p.Input, output = p.Output;
        if (fast && FastPrices.TryGetValue(prefix, out var f)) (input, output) = f;
        return new Rates(input, output, input * 1.25, input * 2.0, p.CacheRead ?? input * 0.1);
    }

    public record Breakdown(double Input, double CacheWrite, double CacheRead, double Output, double Web, double NoCache)
    {
        public double Total => Input + CacheWrite + CacheRead + Output + Web;
    }

    /// <summary>Dollar cost of one response by token type, or null if the model is unpriced.
    /// NoCache is what the same request would have cost without prompt caching.</summary>
    public static Breakdown? Cost(string model, Usage u)
    {
        var r = RatesFor(model, u.Fast);
        if (r == null) return null;
        double allInput = u.Input + u.Cache5m + u.Cache1h + u.CacheRead;
        double web = u.WebSearch * WebSearchPerRequest;
        return new Breakdown(
            u.Input * r.Input / 1e6,
            (u.Cache5m * r.CacheWrite5m + u.Cache1h * r.CacheWrite1h) / 1e6,
            u.CacheRead * r.CacheRead / 1e6,
            u.Output * r.Output / 1e6,
            web,
            (allInput * r.Input + u.Output * r.Output) / 1e6 + web);
    }
}
