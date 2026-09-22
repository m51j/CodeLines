using System.Diagnostics;
using System.Globalization;
using System.Text;
using CodeLines.Core.Abstractions;
using CodeLines.Core.Models;

namespace CodeLines.Core.Services;

public sealed class GitHistoryAnalyzer(IFileClassifier classifier, string gitExecutable = "git") : IGitHistoryAnalyzer
{
    public async Task<GitProjectHistory> AnalyzeAsync(ProjectDefinition project, ScanOptions scanOptions,
        GitHistoryOptions options, DateTimeOffset end, CancellationToken cancellationToken = default,
        IProgress<GitHistoryProgress>? progress = null)
    {
        var result = new GitProjectHistory { ProjectId = project.Id, ProjectName = project.Name };
        if (!options.Enabled) { result.Status = "Disabled"; return result; }
        if (options.Days <= 0 || options.Days > 36500)
            throw new ArgumentOutOfRangeException(nameof(options.Days), "Choose 1–36500 days.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("Opening repository"));
            var start = end.AddDays(-options.Days);
            var root = (await Run(project.RootPath, cancellationToken, "rev-parse", "--show-toplevel")).Trim();
            result.Branch = (await Run(root, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD", allowFailure: true)).Trim();
            result.Head = (await Run(root, cancellationToken, "rev-parse", "--verify", "HEAD", allowFailure: true)).Trim();
            if (result.Head.Length == 0) { result.Status = "No commits"; result.Commits = options.Mode == GitHistoryMode.Activity ? 0 : null; progress?.Report(new("No commits", 0, 0)); return result; }
            if (result.Branch == "HEAD") result.Branch = "Detached " + result.Head[..8];
            result.IsIncomplete = (await Run(root, cancellationToken, "rev-parse", "--is-shallow-repository")).Trim() == "true";
            if (result.IsIncomplete) result.Status = "Incomplete: shallow local history";
            progress?.Report(new("Reading commit history"));
            var log = await Run(root, cancellationToken, "log", "--first-parent", "--format=%H %ct %P", result.Head);
            var commits = log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Select(parts => new Commit(parts[0], DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[1], CultureInfo.InvariantCulture)), parts.Length > 2 ? parts[2] : null))
                .ToList();
            var emptyTree = (await Run(root, cancellationToken, "hash-object", "-t", "tree", "--stdin")).Trim();
            var changes = new Dictionary<string, GitFileChange>(StringComparer.Ordinal);
            // A new provider per analysis reads the current ignore files, including for deleted paths.
            var ignores = new GitIgnoreRuleProvider();
            var selected = commits.Where(c => c.Time >= start && c.Time <= end).ToList();
            result.Commits = options.Mode == GitHistoryMode.Activity ? selected.Count : null;
            var totalComparisons = options.Mode == GitHistoryMode.Activity ? selected.Count : 1;
            var completedComparisons = 0;
            progress?.Report(new("Preparing comparisons", 0, totalComparisons));
            if (options.Mode == GitHistoryMode.Activity)
            {
                foreach (var commit in selected)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Shallow boundary commits must not be treated as newly added entire trees.
                    if (result.IsIncomplete && commit.Parent is null)
                    {
                        result.Status = "Incomplete: shallow boundary excluded";
                        progress?.Report(new("Shallow boundary excluded", ++completedComparisons, totalComparisons));
                        continue;
                    }
                    await Accumulate(commit.Parent ?? emptyTree, commit.Hash);
                    progress?.Report(new("Comparison complete", ++completedComparisons, totalComparisons));
                }
            }
            else
            {
                var baseline = commits.FirstOrDefault(c => c.Time < start);
                if (baseline is null && result.IsIncomplete)
                {
                    result.Status = "Unavailable: period starts before available shallow history";
                    return result;
                }
                await Accumulate(baseline?.Hash ?? emptyTree, result.Head);
                completedComparisons = 1;
            }
            result.Files = changes.Values.OrderByDescending(x => x.Added + x.Deleted).ThenBy(x => x.Path, StringComparer.Ordinal).ToList();
            progress?.Report(new("Project complete", completedComparisons, totalComparisons));
            return result;

            bool Included(string path)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var full = Path.GetFullPath(Path.Combine(root, path));
                var relative = Path.GetRelativePath(project.RootPath, full);
                if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative)) return false;
                return classifier.Classify(path, scanOptions.CountingScope) is not null &&
                    !ignores.IsIgnored(project.RootPath, full, false, scanOptions.GlobalExcludePatterns, project.ExcludePatterns);
            }

            async Task Accumulate(string before, string after)
            {
                progress?.Report(new("Reading changed files", completedComparisons, totalComparisons, Commit: after[..8]));
                var output = await Run(root, cancellationToken, "diff", "--numstat", "-z", "--find-renames=50%", "--no-ext-diff", "--no-textconv", "--no-color", "--diff-algorithm=myers", before, after, "--");
                var entries = new List<Entry>();
                foreach (var entry in ParseNumstat(output))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.OldPath is not null && Included(entry.Path) != Included(entry.OldPath))
                    {
                        // Moving into/out of the counting scope adds/removes the entire scoped file.
                        var split = await Run(root, cancellationToken, "diff", "--numstat", "-z", "--no-renames",
                            "--no-ext-diff", "--no-textconv", "--no-color", "--diff-algorithm=myers", before, after, "--", entry.OldPath, entry.Path);
                        entries.AddRange(ParseNumstat(split));
                    }
                    else entries.Add(entry);
                }
                entries = entries.Where(entry => Included(entry.Path) || (entry.OldPath is not null && Included(entry.OldPath))).ToList();
                var completedFiles = 0;
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new("Analyzing files", completedComparisons, totalComparisons,
                        completedFiles, entries.Count, after[..8], entry.Path));
                    long modified = 0;
                    if (!entry.Binary && entry.Added > 0 && entry.Deleted > 0)
                    {
                        var args = new List<string> { "diff", "--unified=0", "--find-renames=50%", "--no-ext-diff", "--no-textconv", "--no-color", "--diff-algorithm=myers", before, after, "--", entry.Path };
                        if (entry.OldPath is not null) args.Add(entry.OldPath);
                        modified = EstimateModified(await Run(root, cancellationToken, args.ToArray()));
                    }
                    var relative = Path.GetRelativePath(project.RootPath, Path.Combine(root, entry.Path)).Replace('\\', '/');
                    if (relative.StartsWith("../") && entry.OldPath is not null)
                        relative = Path.GetRelativePath(project.RootPath, Path.Combine(root, entry.OldPath)).Replace('\\', '/');
                    var previous = changes.GetValueOrDefault(relative);
                    changes[relative] = new(relative, (previous?.Added ?? 0) + entry.Added,
                        (previous?.Deleted ?? 0) + entry.Deleted, (previous?.ModifiedEstimate ?? 0) + modified,
                        (previous?.Binary ?? false) || entry.Binary);
                    progress?.Report(new("Analyzing files", completedComparisons, totalComparisons,
                        ++completedFiles, entries.Count, after[..8], entry.Path));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            result.Status = "Unavailable: " + ex.Message;
            result.IsIncomplete = true;
            return result;
        }
    }

    private sealed record Commit(string Hash, DateTimeOffset Time, string? Parent);
    private sealed record Entry(string Path, string? OldPath, long Added, long Deleted, bool Binary);

    private static IEnumerable<Entry> ParseNumstat(string output)
    {
        var records = output.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            if (records[i].Length == 0) continue;
            var fields = records[i].Split('\t', 3);
            if (fields.Length != 3) throw new InvalidOperationException("Unexpected Git statistics format.");
            var path = fields[2];
            string? oldPath = null;
            if (path.Length == 0) { oldPath = records[++i]; path = records[++i]; }
            var binary = fields[0] == "-" || fields[1] == "-";
            yield return new(path, oldPath, binary ? 0 : long.Parse(fields[0], CultureInfo.InvariantCulture),
                binary ? 0 : long.Parse(fields[1], CultureInfo.InvariantCulture), binary);
        }
    }

    public static long EstimateModified(string patch)
    {
        long total = 0, added = 0, deleted = 0;
        var inHunk = false;
        void Flush() { total += Math.Min(added, deleted); added = deleted = 0; }
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("@@ ")) { Flush(); inHunk = true; }
            else if (line.StartsWith("diff --git ")) { Flush(); inHunk = false; }
            else if (inHunk && line.StartsWith('+')) added++;
            else if (inHunk && line.StartsWith('-')) deleted++;
            else if (!line.StartsWith("\\ No newline")) Flush();
        }
        Flush();
        return total;
    }

    private Task<string> Run(string directory, CancellationToken token, string arg1, string arg2, string arg3, bool allowFailure)
        => RunProcess(directory, token, [arg1, arg2, arg3], allowFailure);

    private Task<string> Run(string directory, CancellationToken token, params string[] args)
        => RunProcess(directory, token, args, false);

    private async Task<string> RunProcess(string directory, CancellationToken token, string[] args, bool allowFailure)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(gitExecutable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start Git.");
        process.StandardInput.Close();
        using var registration = token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var text = await output;
        var errorText = await error;
        if (process.ExitCode != 0)
        {
            if (allowFailure) return "";
            throw new InvalidOperationException(errorText.Trim());
        }
        return text;
    }
}
