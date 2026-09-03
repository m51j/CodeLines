namespace CodeLines.Core.Models;

public sealed record LineMetrics(
    long PhysicalLines,
    long BlankLines,
    long CodeLines,
    long CommentLines,
    long MixedLines,
    long ContentLines);

public sealed record FileMetrics(
    string ProjectName,
    string RelativePath,
    string Extension,
    string Language,
    FileCategory Category,
    long Bytes,
    long Tokens,
    LineMetrics Lines);

public sealed record LanguageMetrics(
    string Language,
    FileCategory Category,
    long Files,
    long Bytes,
    long Tokens,
    long PhysicalLines,
    long BlankLines,
    long CodeLines,
    long CommentLines,
    long MixedLines,
    long ContentLines)
{
    public static LanguageMetrics FromFiles(string language, FileCategory category, IEnumerable<FileMetrics> files)
    {
        var list = files.ToList();
        return new(language, category, list.Count, list.Sum(x => x.Bytes), list.Sum(x => x.Tokens),
            list.Sum(x => x.Lines.PhysicalLines), list.Sum(x => x.Lines.BlankLines),
            list.Sum(x => x.Lines.CodeLines), list.Sum(x => x.Lines.CommentLines),
            list.Sum(x => x.Lines.MixedLines), list.Sum(x => x.Lines.ContentLines));
    }
}

public sealed record CategoryMetrics(
    FileCategory Category,
    long Files,
    long Tokens,
    long PhysicalLines,
    long CodeOrContentLines);

public sealed class ProjectMetrics
{
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required string RootPath { get; init; }
    public required IReadOnlyList<FileMetrics> Files { get; init; }
    public required IReadOnlyList<LanguageMetrics> Languages { get; init; }
    public required IReadOnlyList<CategoryMetrics> Categories { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public TimeSpan Duration { get; init; }

    public long FileCount => Files.Count;
    public long TotalBytes => Files.Sum(x => x.Bytes);
    public long TotalTokens => Files.Sum(x => x.Tokens);
    public long PhysicalLines => Files.Sum(x => x.Lines.PhysicalLines);
    public long BlankLines => Files.Sum(x => x.Lines.BlankLines);
    public long CodeLines => Files.Sum(x => x.Lines.CodeLines);
    public long CommentLines => Files.Sum(x => x.Lines.CommentLines);
    public long ContentLines => Files.Sum(x => x.Lines.ContentLines);

    public static ProjectMetrics Create(ProjectDefinition project, IReadOnlyList<FileMetrics> files,
        IReadOnlyList<string> warnings, TimeSpan duration)
    {
        var languages = files.GroupBy(x => new { x.Language, x.Category })
            .Select(g => LanguageMetrics.FromFiles(g.Key.Language, g.Key.Category, g))
            .OrderByDescending(x => x.CodeLines + x.ContentLines).ToList();
        var categories = files.GroupBy(x => x.Category)
            .Select(g => new CategoryMetrics(g.Key, g.LongCount(), g.Sum(x => x.Tokens),
                g.Sum(x => x.Lines.PhysicalLines), g.Sum(x => x.Lines.CodeLines + x.Lines.ContentLines)))
            .ToList();
        return new ProjectMetrics
        {
            ProjectId = project.Id,
            ProjectName = project.Name,
            RootPath = project.RootPath,
            Files = files,
            Languages = languages,
            Categories = categories,
            Warnings = warnings,
            Duration = duration
        };
    }
}

public sealed class ScanSnapshot
{
    public DateTimeOffset ScannedAt { get; init; } = DateTimeOffset.Now;
    public CountingScope CountingScope { get; init; }
    public string TokenizerEncoding { get; init; } = "o200k_base";
    public IReadOnlyList<ProjectMetrics> Projects { get; init; } = [];
    public long FileCount => Projects.Sum(x => x.FileCount);
    public long TotalTokens => Projects.Sum(x => x.TotalTokens);
    public long PhysicalLines => Projects.Sum(x => x.PhysicalLines);
    public long CodeLines => Projects.Sum(x => x.CodeLines);
    public long ContentLines => Projects.Sum(x => x.ContentLines);
}

public sealed record ScanProgress(string ProjectName, string CurrentPath, long ProcessedFiles, long DiscoveredFiles);

