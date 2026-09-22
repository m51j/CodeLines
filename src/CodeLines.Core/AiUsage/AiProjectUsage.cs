using CodeLines.Core.AiUsage.Agents;

namespace CodeLines.Core.AiUsage;

/// <summary>AI tokens and cost of the sessions that ran inside one project folder.</summary>
public sealed record AiProjectUsage(int Sessions, double Tokens, double Cost, bool CostPartial)
{
    public static readonly AiProjectUsage None = new(0, 0, 0, false);

    public string CostText => Sessions == 0 || Cost == 0 && CostPartial ? "—" : $"${Cost:N2}{(CostPartial ? "*" : "")}";
}

/// <summary>Attributes agent sessions to CodeLines projects by their working directory.</summary>
public static class AiProjectUsageMatcher
{
    /// <param name="roots">Project root folders, keyed by project id.</param>
    /// <param name="since">Only sessions that started at or after this time; null = all.</param>
    public static Dictionary<TKey, AiProjectUsage> Match<TKey>(AgentUsagePayload payload, IReadOnlyDictionary<TKey, string> roots,
        DateTimeOffset? since = null) where TKey : notnull
    {
        // Longest root first, so a session in a nested project counts for the inner project only.
        var normalized = roots.Select(p => (p.Key, Root: Normalize(p.Value))).Where(p => p.Root.Length > 0)
            .OrderByDescending(p => p.Root.Length).ToList();
        var totals = roots.Keys.ToDictionary(k => k, _ => (Sessions: 0, V: new double[AgentVector.Length]));
        var from = since?.ToUnixTimeMilliseconds() ?? long.MinValue;
        foreach (var session in payload.Sessions)
        {
            if (session.Start < from || session.Cwd.Length == 0) continue;
            var cwd = Normalize(session.Cwd);
            foreach (var (key, root) in normalized)
            {
                if (!cwd.Equals(root, StringComparison.OrdinalIgnoreCase)
                    && !cwd.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                var t = totals[key];
                for (var i = 0; i < AgentVector.Length; i++) t.V[i] += session.Vector[i];
                totals[key] = (t.Sessions + 1, t.V);
                break;
            }
        }
        return totals.ToDictionary(p => p.Key, p =>
        {
            var tokens = AgentVector.RealTokens(p.Value.V);
            return new AiProjectUsage(p.Value.Sessions, tokens, p.Value.V[AgentVector.Cost], p.Value.V[AgentVector.CostTokens] < tokens * 0.999);
        });
    }

    private static string Normalize(string path)
    {
        var p = path.Trim().Replace('/', Path.DirectorySeparatorChar);
        // Git Bash / WSL style drive paths: /d/work or /mnt/d/work → d:\work
        if (p.Length >= 3 && p[0] == Path.DirectorySeparatorChar && char.IsLetter(p[1]) && p[2] == Path.DirectorySeparatorChar)
            p = p[1] + ":" + p[2..];
        else if (p.StartsWith(@"\mnt\", StringComparison.OrdinalIgnoreCase) && p.Length >= 7 && char.IsLetter(p[5]) && p[6] == '\\')
            p = p[5] + ":" + p[6..];
        try { p = Path.GetFullPath(p); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        return p.TrimEnd(Path.DirectorySeparatorChar);
    }
}
