using CodeLines.Core.AiUsage.Agents;

namespace CodeLines.Core.AiUsage;

/// <summary>Report options; the equivalents of ccstats' command-line flags.</summary>
public sealed class AiUsageOptions
{
    /// <summary>Bucket times in UTC instead of local time (--tz utc).</summary>
    public bool UseUtc { get; init; }
    /// <summary>Deduplicate Claude Code records by request id (off = --raw).</summary>
    public bool Deduplicate { get; init; } = true;
    /// <summary>Only the last N days, counted back from the newest record (--days). 0 = everything.</summary>
    public int Days { get; init; }
    public DateOnly? Since { get; init; }
    public DateOnly? Until { get; init; }
    /// <summary>Ignore the incremental caches and re-parse everything (--rescan).</summary>
    public bool Rescan { get; init; }
    /// <summary>Agent ids to leave out (--skip). "claude" is Claude Code.</summary>
    public IReadOnlyCollection<string> DisabledAgents { get; init; } = [];
    /// <summary>Extra Claude Code transcript folders (--root).</summary>
    public IReadOnlyList<string> ExtraClaudeRoots { get; init; } = [];
}

public sealed record AiUsageProgress(string Agent, string Message, int Done = 0, int Total = 0);

public enum AiUsageAgentState { NotFound, Disabled, Empty, Loaded, Failed }

public sealed record AiUsageAgentStatus(string Id, string Label, AiUsageAgentState State, long Records = 0, string? Error = null,
    TimeSpan Elapsed = default)
{
    public string Summary => State switch
    {
        AiUsageAgentState.Loaded => $"{Records:N0} records",
        AiUsageAgentState.Empty => "no usage yet",
        AiUsageAgentState.Disabled => "turned off",
        AiUsageAgentState.Failed => "skipped: " + Error,
        _ => "not found"
    };
}

public sealed record AiUsageTab(string Id, string Label, string Href, double Tokens);

public sealed class AiUsageResult
{
    public required string OutputDirectory { get; init; }
    public required string DashboardFile { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required IReadOnlyList<AiUsageTab> Tabs { get; init; }
    public required IReadOnlyList<AiUsageAgentStatus> Agents { get; init; }
    /// <summary>The combined "All agents" payload; null when no agent had usage.</summary>
    public AgentUsagePayload? Combined { get; init; }
    public bool HasData => Combined is not null;
}

public interface IAiUsageReportBuilder
{
    /// <summary>The folder the HTML pages are written to.</summary>
    string OutputDirectory { get; }
    /// <summary>Every agent this build knows about, as (id, label), in tab order.</summary>
    IReadOnlyList<(string Id, string Label)> KnownAgents { get; }
    Task<AiUsageResult> BuildAsync(AiUsageOptions options, IProgress<AiUsageProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
