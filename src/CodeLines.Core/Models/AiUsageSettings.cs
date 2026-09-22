namespace CodeLines.Core.Models;

public sealed class AiUsageSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Bucket times in UTC instead of local time.</summary>
    public bool UseUtc { get; set; }
    /// <summary>Count each Claude Code API request once (ccstats' default; off = --raw).</summary>
    public bool Deduplicate { get; set; } = true;
    /// <summary>Refresh the report the first time the tab is opened in a session.</summary>
    public bool AutoRefreshOnOpen { get; set; } = true;
    /// <summary>Agent ids to leave out, e.g. "copilot". "claude" is Claude Code.</summary>
    public List<string> DisabledAgents { get; set; } = [];
    /// <summary>Extra Claude Code transcript folders besides ~/.claude/projects.</summary>
    public List<string> ExtraClaudeRoots { get; set; } = [];
}
