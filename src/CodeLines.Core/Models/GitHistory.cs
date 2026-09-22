namespace CodeLines.Core.Models;

public enum GitHistoryMode { Activity, NetChange }

/// <summary>Counts describe the current project, and files describe its current comparison.</summary>
public sealed record GitHistoryProgress(string Stage, int CompletedComparisons = 0, int? TotalComparisons = null,
    int CompletedFiles = 0, int? TotalFiles = null, string? Commit = null, string? Path = null);

public sealed record GitHistoryTotals(long Added, long Deleted, long ModifiedEstimate, long ChangedFiles,
    long BinaryFiles, long? Commits, int Projects, int IncompleteProjects)
{
    public long Net => Added - Deleted;
    public static GitHistoryTotals FromProjects(IEnumerable<GitProjectHistory> projects, GitHistoryMode mode)
    {
        var rows = projects.ToList();
        return new(rows.Sum(x => x.Added), rows.Sum(x => x.Deleted), rows.Sum(x => x.ModifiedEstimate),
            rows.Sum(x => (long)x.ChangedFiles), rows.Sum(x => (long)x.BinaryFiles),
            mode == GitHistoryMode.Activity ? rows.Sum(x => x.Commits ?? 0) : null,
            rows.Count, rows.Count(x => x.IsIncomplete));
    }
}

public sealed class GitHistoryOptions
{
    public bool Enabled { get; set; }
    public int Days { get; set; } = 7;
    public GitHistoryMode Mode { get; set; }
}

public sealed record GitFileChange(string Path, long Added, long Deleted, long ModifiedEstimate, bool Binary)
{
    public long Net => Added - Deleted;
}

public sealed class GitProjectHistory
{
    public Guid ProjectId { get; init; }
    public string ProjectName { get; init; } = "";
    public string Branch { get; set; } = "";
    public string Head { get; set; } = "";
    public string Status { get; set; } = "Complete";
    public bool IsIncomplete { get; set; }
    public long? Commits { get; set; }
    public IReadOnlyList<GitFileChange> Files { get; set; } = [];
    public long Added => Files.Sum(x => x.Added);
    public long Deleted => Files.Sum(x => x.Deleted);
    public long ModifiedEstimate => Files.Sum(x => x.ModifiedEstimate);
    public long Net => Added - Deleted;
    public int ChangedFiles => Files.Count;
    public int BinaryFiles => Files.Count(x => x.Binary);
}

public sealed class GitHistorySnapshot
{
    public DateTimeOffset End { get; init; }
    public DateTimeOffset Start { get; init; }
    public GitHistoryMode Mode { get; init; }
    public CountingScope Scope { get; init; }
    public IReadOnlyList<GitProjectHistory> Projects { get; init; } = [];
    public GitHistoryTotals Totals => GitHistoryTotals.FromProjects(Projects, Mode);
}
