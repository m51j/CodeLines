namespace CodeLines.Core.AiUsage.Agents;

/// <summary>Where agents keep their data. Injected so adapters can be tested against temporary folders.</summary>
public sealed record AgentEnvironment(string Home, string AppData, string LocalAppData, string CacheDirectory)
{
    public static AgentEnvironment ForCurrentUser(string cacheDirectory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Folder(Environment.SpecialFolder folder, string fallback)
        {
            var path = Environment.GetFolderPath(folder);
            return string.IsNullOrEmpty(path) ? Path.Combine(home, fallback) : path;
        }
        return new(home, Folder(Environment.SpecialFolder.ApplicationData, Path.Combine("AppData", "Roaming")),
            Folder(Environment.SpecialFolder.LocalApplicationData, Path.Combine("AppData", "Local")), cacheDirectory);
    }

    public string CacheFile(string agentId) => Path.Combine(CacheDirectory, $"agent-{agentId}.json");
    public string TempDirectory => Path.Combine(CacheDirectory, "tmp");
}

/// <summary>One AI agent whose local usage data can be read (ccstats' agents/adapters/*.mjs).</summary>
public interface IAgentUsageAdapter
{
    string Id { get; }
    string Label { get; }
    /// <summary>A caveat shown on the agent's page, or null.</summary>
    string? Note { get; }
    /// <summary>True when the agent's data folder or database exists on this machine.</summary>
    bool Detect();
    Task<IReadOnlyList<AgentRecord>> LoadAsync(bool rescan, CancellationToken cancellationToken = default);
}
