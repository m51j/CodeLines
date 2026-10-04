using System.Text.RegularExpressions;

namespace CodeLines.Core.AiUsage.Agents;

public sealed record ListRate(double Input, double CacheRead, double CacheWrite, double Output);

/// <summary>
/// List prices for non-Anthropic models, USD per million tokens, standard tier (ccstats' agents/pricing.mjs).
/// Used only when the agent did not record a cost itself; a missing model shows "—" instead of a guess.
/// </summary>
public static partial class AgentPricing
{
    // Official list prices as of 2026-10-05, short-context tier. A null cache write means the provider charges none,
    // so those tokens bill as plain input.
    private static readonly Dictionary<string, (double Input, double? CacheRead, double? CacheWrite, double Output)> Rates = new(StringComparer.Ordinal)
    {
        // OpenAI
        ["gpt-6.1-sol"] = (2, 0.1, 2.5, 10),
        ["gpt-6-astra"] = (10, 1, 12.5, 50),
        ["gpt-6-sol"] = (2, 0.2, 2.5, 10),
        ["gpt-6-luna"] = (0.1, 0.01, 0.125, 0.5),
        ["gpt-5.6-sol"] = (4, 0.4, 5, 20),
        ["gpt-5.6-terra"] = (2, 0.2, 2.5, 12),
        ["gpt-5.6-luna"] = (0.2, 0.02, 0.25, 1.2),
        ["gpt-5.5"] = (5, 0.5, null, 30),
        ["gpt-5.4"] = (2.5, 0.25, null, 15),
        // Google (output includes thinking). 3.6-3.8 Flash are at a promotional price until 2026-12-31; it doubles after.
        ["gemini-3.8-flash"] = (0.75, 0.075, null, 3.75),
        ["gemini-3.7-flash"] = (0.75, 0.075, null, 3.75),
        ["gemini-3.6-flash"] = (0.75, 0.075, null, 3.75),
        ["gemini-3.5-flash"] = (1.5, 0.15, null, 9),
        ["gemini-3.5-flash-lite"] = (0.3, null, null, 2.5),
        ["gemini-3-flash-preview"] = (0.5, 0.05, null, 3),
        ["gemini-3.1-pro-preview"] = (2, 0.2, null, 12),
        // Z.ai; "ox-alpha" was GLM-5.3-Flash's free stealth preview on OpenRouter.
        ["glm-5.3-flash"] = (0.15, 0.03, null, 0.5),
        ["ox-alpha"] = (0, 0, 0, 0),
        // xAI
        ["grok-4.3"] = (1.25, 0.2, null, 2.5)
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["gemini-3-flash"] = "gemini-3-flash-preview"
    };

    /// <summary>Effort or variant suffixes some agents append to the model id ("-high", "-thinking", Antigravity's "-agent").</summary>
    [GeneratedRegex(@"-(thinking|xhigh|high|medium|low|max|agent)$")] private static partial Regex VariantSuffix();

    public static ListRate? For(string model)
    {
        var raw = model.ToLowerInvariant();
        var id = Normalize(raw);
        if (Rates.TryGetValue(raw, out var r) || Rates.TryGetValue(id, out r))
            return new(r.Input, r.CacheRead ?? r.Input, r.CacheWrite ?? r.Input, r.Output);
        // Claude models run through another agent (Antigravity's "claude-opus-4-6-thinking", Copilot's "claude-sonnet-4.6")
        // use the Anthropic table.
        if (!id.StartsWith("claude-", StringComparison.Ordinal)) return null;
        var claude = id.Replace('.', '-');
        if (!ClaudePricing.IsKnownModel(claude)) return null;
        var a = ClaudePricing.RatesFor(claude);
        return new(a.Input, a.CacheRead, a.Write5m, a.Output);
    }

    /// <summary>omniroute/kr/claude-sonnet-4.5-high → claude-sonnet-4.5; z-ai/glm-5.3-flash → glm-5.3-flash</summary>
    private static string Normalize(string id)
    {
        id = id[(id.LastIndexOf('/') + 1)..];
        id = VariantSuffix().Replace(id, "", 1);
        return Aliases.GetValueOrDefault(id, id);
    }
}
