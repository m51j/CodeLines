using CodeLines.Core.Models;

namespace CodeLines.Core.Abstractions;

public interface IGitHistoryAnalyzer
{
    Task<GitProjectHistory> AnalyzeAsync(ProjectDefinition project, ScanOptions scanOptions,
        GitHistoryOptions options, DateTimeOffset end, CancellationToken cancellationToken = default,
        IProgress<GitHistoryProgress>? progress = null);
}

public interface IProjectRepository
{
    string SettingsPath { get; }
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface ILanguageRegistry
{
    IReadOnlyList<LanguageDefinition> Languages { get; }
    LanguageDefinition? Find(string path);
}

public interface IFileClassifier
{
    FileClassification? Classify(string path, CountingScope scope);
}

public sealed record FileClassification(LanguageDefinition Language, FileCategory Category);

public interface IIgnoreRuleProvider
{
    bool IsIgnored(string projectRoot, string fullPath, bool isDirectory,
        IReadOnlyList<string> globalPatterns, IReadOnlyList<string> projectPatterns);
}

public interface ILineMetricsAnalyzer
{
    LineMetrics Analyze(string text, LanguageDefinition language, FileCategory category);
}

public interface ITokenCounter
{
    IReadOnlyList<string> SupportedEncodings { get; }
    long CountTokens(string text, string encodingName);
}

public interface ISourceScanner
{
    Task<ProjectMetrics> ScanProjectAsync(ProjectDefinition project, ScanOptions options,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);
}

public interface IExportService
{
    Task ExportJsonAsync(ScanSnapshot snapshot, string path, CancellationToken cancellationToken = default);
    Task ExportCsvAsync(ScanSnapshot snapshot, string path, CancellationToken cancellationToken = default);
}
