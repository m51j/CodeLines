using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using CodeLines.Core.Abstractions;
using CodeLines.Core.Models;

namespace CodeLines.Core.Services;

public sealed class SourceScanner(
    IFileClassifier classifier,
    IIgnoreRuleProvider ignoreRules,
    ILineMetricsAnalyzer lineAnalyzer,
    ITokenCounter tokenCounter) : ISourceScanner
{
    public async Task<ProjectMetrics> ScanProjectAsync(ProjectDefinition project, ScanOptions options,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new ConcurrentBag<string>();
        if (!Directory.Exists(project.RootPath))
        {
            return ProjectMetrics.Create(project, [], [$"Project path does not exist: {project.RootPath}"], stopwatch.Elapsed);
        }

        var candidates = DiscoverFiles(project, options, warnings, cancellationToken);
        long processed = 0;
        var results = new ConcurrentBag<FileMetrics>();
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism)
        };

        await Parallel.ForEachAsync(candidates, parallelOptions, async (candidate, token) =>
        {
            try
            {
                var metric = await AnalyzeFileAsync(project, candidate, options, token);
                if (metric is not null) results.Add(metric);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                warnings.Add($"Skipped {candidate.Path}: {ex.Message}");
            }
            finally
            {
                var count = Interlocked.Increment(ref processed);
                if (count == 1 || count % 25 == 0 || count == candidates.Count)
                    progress?.Report(new(project.Name, candidate.RelativePath, count, candidates.Count));
            }
        });

        stopwatch.Stop();
        return ProjectMetrics.Create(project,
            results.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
            warnings.OrderBy(x => x).ToList(), stopwatch.Elapsed);
    }

    private List<CandidateFile> DiscoverFiles(ProjectDefinition project, ScanOptions options,
        ConcurrentBag<string> warnings, CancellationToken cancellationToken)
    {
        var result = new List<CandidateFile>();
        var stack = new Stack<string>();
        stack.Push(Path.GetFullPath(project.RootPath));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = stack.Pop();
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var info = new DirectoryInfo(child);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (!ignoreRules.IsIgnored(project.RootPath, child, true,
                            options.GlobalExcludePatterns, project.ExcludePatterns))
                        stack.Push(child);
                }

                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (ignoreRules.IsIgnored(project.RootPath, file, false,
                            options.GlobalExcludePatterns, project.ExcludePatterns)) continue;
                    var classification = classifier.Classify(file, options.CountingScope);
                    if (classification is null) continue;
                    var info = new FileInfo(file);
                    if (info.Length > options.MaxFileSizeBytes)
                    {
                        warnings.Add($"Skipped oversized file: {file}");
                        continue;
                    }
                    result.Add(new(file, Path.GetRelativePath(project.RootPath, file), info.Length, classification));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Cannot enumerate {directory}: {ex.Message}");
            }
        }
        return result;
    }

    private async Task<FileMetrics?> AnalyzeFileAsync(ProjectDefinition project, CandidateFile candidate,
        ScanOptions options, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(candidate.Path, cancellationToken);
        if (LooksBinary(bytes)) return null;
        var text = Decode(bytes);
        var lines = lineAnalyzer.Analyze(text, candidate.Classification.Language, candidate.Classification.Category);
        var tokens = tokenCounter.CountTokens(text, options.TokenizerEncoding);
        var extension = Path.GetExtension(candidate.Path);
        if (string.IsNullOrEmpty(extension)) extension = "[no extension]";
        return new(project.Name, candidate.RelativePath, extension, candidate.Classification.Language.Name,
            candidate.Classification.Category, candidate.Bytes, tokens, lines);
    }

    private static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            return false;
        var sample = bytes[..Math.Min(bytes.Length, 8192)];
        if (sample.IndexOf((byte)0) >= 0) return true;
        if (sample.Length == 0) return false;
        var controls = 0;
        foreach (var value in sample)
            if (value < 8 || value is 11 or 12 || value is >= 14 and < 32) controls++;
        return controls > sample.Length / 20;
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private sealed record CandidateFile(string Path, string RelativePath, long Bytes, FileClassification Classification);
}
