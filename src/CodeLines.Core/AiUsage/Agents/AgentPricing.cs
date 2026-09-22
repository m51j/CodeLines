namespace CodeLines.Core.AiUsage.Agents;

public sealed record ListRate(double Input, double CacheRead, double CacheWrite, double Output);

/// <summary>
/// List prices for non-Anthropic models, USD per million tokens, standard tier (ccstats' agents/pricing.mjs).
/// Used only when the agent did not record a cost itself; a missing model shows "—" instead of a guess.
/// </summary>
public static class AgentPricing
{
    private static readonly Dictionary<string, (double Input, double? CacheRead, double? CacheWrite, double Output)> Rates = new(StringComparer.Ordinal)
    {
        ["gpt-5.5"] = (5, 0.5, null, 30),
        ["gpt-5.6-sol"] = (4, 0.4, 5, 20),
        ["gpt-5.6-terra"] = (2, 0.2, 2.5, 12),
        ["gpt-5.6-luna"] = (0.2, 0.02, 0.25, 1.2),
        ["gpt-6-astra"] = (10, 1, 12.5, 50),
        ["grok-4.3"] = (1.25, 0.2, null, 2.5)
    };

    public static ListRate? For(string model)
    {
        var id = model.ToLowerInvariant();
        if (Rates.TryGetValue(id, out var r)) return new(r.Input, r.CacheRead ?? r.Input, r.CacheWrite ?? r.Input, r.Output);
        // Claude models run through another agent (e.g. Antigravity's "claude-opus-4-6-thinking") use the Anthropic table.
        var claude = id.EndsWith("-thinking", StringComparison.Ordinal) ? id[..^"-thinking".Length] : id;
        if (!claude.StartsWith("claude-", StringComparison.Ordinal) || !ClaudePricing.IsKnownModel(claude)) return null;
        var a = ClaudePricing.RatesFor(claude);
        return new(a.Input, a.CacheRead, a.Write5m, a.Output);
    }
}
