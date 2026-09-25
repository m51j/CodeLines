namespace CodeLines.Core.Models;

public sealed class AppSettings
{
    public List<ProjectDefinition> Projects { get; set; } = [];
    public ScanOptions ScanOptions { get; set; } = new();
    public string Theme { get; set; } = "System";
    public ScanSnapshot? LastSnapshot { get; set; }
    public GitHistoryOptions GitHistory { get; set; } = new();
    public GitHistorySnapshot? LastGitHistory { get; set; }
    public AiUsageSettings AiUsage { get; set; } = new();
    public DailyLogSettings DailyLog { get; set; } = new();
}
