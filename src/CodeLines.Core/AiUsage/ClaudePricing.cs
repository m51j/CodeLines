using System.Text.RegularExpressions;

namespace CodeLines.Core.AiUsage;

/// <summary>USD per million tokens. Rates and derivations mirror ccstats' src/pricing.js.</summary>
public sealed record ModelRates(double Input, double Output, double CacheRead, double Write5m, double Write1h, bool Known);

public static partial class ClaudePricing
{
    public const double Write5mMultiplier = 1.25;
    public const double Write1hMultiplier = 2.0;
    public const double CacheReadMultiplier = 0.1;

    // First-party Anthropic API list rates: input / output, plus an optional explicit cache-read rate.
    private static readonly Dictionary<string, (double Input, double Output, double? CacheRead)> Rates = new(StringComparer.Ordinal)
    {
        ["claude-opus-5-5"] = (4, 20, 0.2),
        ["claude-opus-5"] = (5, 25, null),
        ["claude-opus-4-8"] = (5, 25, null),
        ["claude-opus-4-7"] = (5, 25, null),
        ["claude-opus-4-6"] = (5, 25, null),
        ["claude-fable-5-1"] = (10, 50, 0.25),
        ["claude-mythos-5-1"] = (10, 50, 0.25),
        ["claude-fable-5"] = (10, 50, null),
        ["claude-mythos-5"] = (10, 50, null),
        ["claude-sonnet-5"] = (2, 10, null),
        ["claude-sonnet-4-6"] = (3, 15, null),
        ["claude-sonnet-4-5"] = (3, 15, null),
        ["claude-haiku-4-5"] = (1, 5, null),
        ["claude-haiku-3-5"] = (0.8, 4, null)
    };

    private static readonly (double Input, double Output, double? CacheRead) Fallback = (5, 25, null);

    [GeneratedRegex(@"-\d{8}$")] private static partial Regex DateSuffix();
    [GeneratedRegex(@"^anthropic\.")] private static partial Regex BedrockPrefix();
    [GeneratedRegex(@"@\d{8}$")] private static partial Regex VertexSuffix();
    [GeneratedRegex(@"^claude-(opus|sonnet|haiku|fable|mythos)-(\d+)(?:-(\d+))?$")] private static partial Regex ClaudeId();

    /// <summary>claude-haiku-4-5-20251001 → claude-haiku-4-5</summary>
    public static string NormalizeModel(string? model) => model is null ? "unknown"
        : VertexSuffix().Replace(BedrockPrefix().Replace(DateSuffix().Replace(model, "", 1), "", 1), "", 1);

    /// <summary>claude-opus-4-8 → Opus 4.8</summary>
    public static string DisplayModel(string model)
    {
        var id = NormalizeModel(model);
        var m = ClaudeId().Match(id);
        if (!m.Success) return id;
        var family = char.ToUpperInvariant(m.Groups[1].Value[0]) + m.Groups[1].Value[1..];
        return m.Groups[3].Success ? $"{family} {m.Groups[2].Value}.{m.Groups[3].Value}" : $"{family} {m.Groups[2].Value}";
    }

    public static bool IsKnownModel(string model) => Rates.ContainsKey(NormalizeModel(model));

    public static ModelRates RatesFor(string model)
    {
        var known = Rates.TryGetValue(NormalizeModel(model), out var r);
        if (!known) r = Fallback;
        return new(r.Input, r.Output, r.CacheRead ?? r.Input * CacheReadMultiplier,
            r.Input * Write5mMultiplier, r.Input * Write1hMultiplier, known);
    }

    public static double CostOf(long input, long cacheWrite5m, long cacheWrite1h, long cacheRead, long output, string model)
    {
        var r = RatesFor(model);
        return (input * r.Input + cacheWrite5m * r.Write5m + cacheWrite1h * r.Write1h + cacheRead * r.CacheRead + output * r.Output) / 1e6;
    }
}
