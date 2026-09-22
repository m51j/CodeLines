namespace CodeLines.Core.AiUsage.Agents.Adapters;

/// <summary>
/// Every agent with its own page, in tab order (ccstats' agents/adapters/index.mjs). Claude Code is not listed: it keeps
/// its own page and joins only the combined view.
/// </summary>
public static class AgentAdapters
{
    public static IReadOnlyList<IAgentUsageAdapter> Create(AgentEnvironment env) =>
    [
        new CodexAdapter(env),
        new AntigravityAdapter(env, "antigravity-ide", "Antigravity IDE", "antigravity-ide"),
        new AntigravityAdapter(env, "antigravity", "Antigravity", "antigravity"),
        new AntigravityAdapter(env, "agy", "agy", "antigravity-cli"),
        new ZCodeAdapter(env),
        OpenCodeAdapter.OpenCode(env),
        OpenCodeAdapter.Kilo(env),
        new ClineAdapter(env),
        new CopilotAdapter(env),
        new HermesAdapter(env),
        new LmStudioAdapter(env)
    ];
}
