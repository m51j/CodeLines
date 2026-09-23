namespace CodeLines.Core.Models;

/// <summary>One record per day of line counts and AI usage. A later scan on the same day replaces that day's values.</summary>
public sealed class DailyLogFile
{
    public int Version { get; set; } = 1;
    /// <summary>Oldest first.</summary>
    public List<DailyLogDay> Days { get; set; } = [];
}

public sealed class DailyLogDay
{
    /// <summary>yyyy-MM-dd.</summary>
    public string Date { get; set; } = "";
    /// <summary>The last scan of each project on this day.</summary>
    public List<DailyLinesEntry> Lines { get; set; } = [];
    public DailyAiUsage? Ai { get; set; }
}

public sealed class DailyLinesEntry
{
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string RootPath { get; set; } = "";
    public DateTimeOffset ScannedAt { get; set; }
    public CountingScope Scope { get; set; }
    public string Tokenizer { get; set; } = "";
    public long Files { get; set; }
    public long Bytes { get; set; }
    public long Tokens { get; set; }
    public long PhysicalLines { get; set; }
    public long BlankLines { get; set; }
    public long CodeLines { get; set; }
    public long CommentLines { get; set; }
    public long ContentLines { get; set; }
    public List<DailyLanguageEntry> Languages { get; set; } = [];
}

public sealed record DailyLanguageEntry(
    string Language,
    FileCategory Category,
    long Files,
    long Tokens,
    long PhysicalLines,
    long CodeLines,
    long CommentLines,
    long ContentLines);

public sealed class DailyAiUsage
{
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>"local" or "utc": the clock the AI usage was bucketed by.</summary>
    public string Tz { get; set; } = "local";
    public DailyAiAmount Total { get; set; } = DailyAiAmount.Zero;
    public Dictionary<string, DailyAiAmount> ByAgent { get; set; } = [];
    /// <summary>Sessions attributed to a project, on the day they started.</summary>
    public List<DailyAiProjectEntry> Projects { get; set; } = [];
}

public sealed record DailyAiAmount(double Input, double CacheWrite, double CacheRead, double Output, double Requests, double Cost, bool CostPartial)
{
    public static readonly DailyAiAmount Zero = new(0, 0, 0, 0, 0, 0, false);

    /// <summary>Input, cache and output tokens.</summary>
    public double Tokens => Input + CacheWrite + CacheRead + Output;
}

public sealed record DailyAiProjectEntry(Guid ProjectId, int Sessions, double Tokens, double Cost, bool CostPartial);

public sealed class DailyLogSettings
{
    /// <summary>Refresh the AI usage report after every scan, so the day's record holds both.</summary>
    public bool RefreshAiAfterScan { get; set; } = true;
}
