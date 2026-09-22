using System.Diagnostics;
using CodeLines.Core.Models;
using CodeLines.Core.Services;

namespace CodeLines.Tests;

public sealed class GitHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeLinesGitTests", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _end = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private readonly GitHistoryAnalyzer _analyzer = new(new FileClassifier(new LanguageRegistry()));

    public GitHistoryTests()
    {
        Directory.CreateDirectory(_root);
        Git("init", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "core.autocrlf", "false");
        Git("config", "commit.gpgsign", "false");
    }

    [Fact]
    public async Task Progress_reports_discovery_commits_and_files_before_completion()
    {
        Write("a.cs", "one\n"); Commit(-10);
        Write("a.cs", "two\n"); Commit(-2);
        Write("b.cs", "new\n"); Commit(-1);
        var progress = new ProgressRecorder();
        await _analyzer.AnalyzeAsync(Project(), new(), new() { Enabled = true }, _end, progress: progress);
        Assert.Contains(progress.Values, p => p.Stage == "Reading commit history" && p.TotalComparisons is null);
        Assert.Contains(progress.Values, p => p.TotalComparisons == 2 && p.CompletedComparisons == 0);
        Assert.Contains(progress.Values, p => p.Path == "a.cs" && p.CompletedFiles == 0 && p.TotalFiles == 1);
        Assert.Contains(progress.Values, p => p.Path == "a.cs" && p.CompletedFiles == 1);
        Assert.Equal(2, progress.Values[^1].CompletedComparisons);
        Assert.Equal("Project complete", progress.Values[^1].Stage);
        var known = progress.Values.Where(p => p.TotalComparisons is not null).Select(p => p.CompletedComparisons).ToArray();
        Assert.Equal(known.Order().ToArray(), known);
        progress.Values.Clear();
        await _analyzer.AnalyzeAsync(Project(), new(), new() { Enabled = true, Mode = GitHistoryMode.NetChange }, _end, progress: progress);
        Assert.Equal(1, progress.Values[^1].TotalComparisons);
        Assert.Equal(1, progress.Values[^1].CompletedComparisons);
        progress.Values.Clear();
        await _analyzer.AnalyzeAsync(Project(), new(), new(), _end, progress: progress);
        Assert.Empty(progress.Values);
    }

    [Fact]
    public async Task Aggregate_totals_include_all_projects_and_mark_incomplete_results_in_exports()
    {
        var snapshot = new GitHistorySnapshot { Mode = GitHistoryMode.Activity, Projects = [
            new() { ProjectName = "First", Commits = 3, Files = [new("a.cs", 10, 4, 2, false)] },
            new() { ProjectName = "Second", Commits = 2, Files = [new("b.cs", 5, 8, 3, false), new("image.bin", 0, 0, 0, true)] },
            new() { ProjectName = "Missing", IsIncomplete = true, Status = "Unavailable" }
        ] };
        Assert.Equal(15, snapshot.Totals.Added); Assert.Equal(12, snapshot.Totals.Deleted);
        Assert.Equal(5, snapshot.Totals.ModifiedEstimate); Assert.Equal(3, snapshot.Totals.Net);
        Assert.Equal(3, snapshot.Totals.ChangedFiles); Assert.Equal(1, snapshot.Totals.BinaryFiles);
        Assert.Equal(5, snapshot.Totals.Commits); Assert.Equal(1, snapshot.Totals.IncompleteProjects);
        Assert.Null(GitHistoryTotals.FromProjects(snapshot.Projects, GitHistoryMode.NetChange).Commits);
        var csv = Path.Combine(_root, "totals.csv"); var json = Path.Combine(_root, "totals.json");
        await GitHistoryExportService.ExportAsync(snapshot, csv);
        await GitHistoryExportService.ExportAsync(snapshot, json);
        var lines = await File.ReadAllLinesAsync(csv);
        Assert.EndsWith("RecordType", lines[0]);
        Assert.Single(lines, x => x.EndsWith("\"Totals\""));
        Assert.Contains("1 incomplete/unavailable", lines[^1]);
        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(json));
        Assert.Equal(15, doc.RootElement.GetProperty("totals").GetProperty("added").GetInt64());
    }

    private sealed class ProgressRecorder : IProgress<GitHistoryProgress>
    {
        public List<GitHistoryProgress> Values { get; } = [];
        public void Report(GitHistoryProgress value) => Values.Add(value);
    }

    [Fact]
    public async Task Activity_counts_reverted_edits_but_net_does_not_and_worktree_is_ignored()
    {
        Write("a.cs", "one\ntwo\n"); Commit(-10);
        Write("a.cs", "one\nchanged\n"); Commit(-2);
        Write("a.cs", "one\ntwo\n"); Commit(-1);
        Write("a.cs", "uncommitted\n");
        var activity = await Analyze();
        Assert.Equal("Complete", activity.Status);
        Assert.Equal(2, activity.Added); Assert.Equal(2, activity.Deleted);
        Assert.Equal(2, activity.ModifiedEstimate); Assert.Equal(2, activity.Commits);
        var net = await Analyze(GitHistoryMode.NetChange);
        Assert.Empty(net.Files); Assert.Null(net.Commits);
    }

    [Fact]
    public async Task New_repository_and_exact_period_boundary_are_included()
    {
        Write("a.cs", "one\ntwo\n"); Commit(-7);
        var activity = await Analyze();
        var net = await Analyze(GitHistoryMode.NetChange);
        Assert.Equal(2, activity.Added); Assert.Equal(2, net.Added);
        Assert.Equal(1, activity.Commits);
        var emptyPeriod = await Analyze(days: 1);
        Assert.Empty(emptyPeriod.Files);
    }

    [Fact]
    public async Task Renames_deletions_unicode_and_binary_are_supported()
    {
        Write("قديم.cs", "one\ntwo\nthree\nfour\n");
        Write("delete.cs", "delete\n"); Commit(-10);
        Git("mv", "قديم.cs", "جديد.cs");
        File.Delete(Path.Combine(_root, "delete.cs"));
        File.WriteAllBytes(Path.Combine(_root, "binary.cs"), [0, 1, 2, 0]); Commit(-1);
        var result = await Analyze();
        Assert.Equal("Complete", result.Status);
        Assert.Equal(0, result.Added); Assert.Equal(1, result.Deleted);
        Assert.Contains(result.Files, x => x.Path == "جديد.cs" && x.Added == 0 && x.Deleted == 0);
        Assert.Equal(1, result.BinaryFiles);
        Assert.Equal(3, result.ChangedFiles);
    }

    [Fact]
    public async Task Scope_current_ignore_rules_and_subfolders_filter_deleted_paths()
    {
        Write("sub/include.cs", "a\n"); Write("sub/ignored.cs", "b\n");
        Write("sub/config.json", "{}\n"); Write("outside.cs", "c\n"); Commit(-10);
        File.Delete(Path.Combine(_root, "sub", "include.cs"));
        File.Delete(Path.Combine(_root, "sub", "ignored.cs"));
        Write("sub/config.json", "{ }\n"); Write("outside.cs", "d\n"); Commit(-1);
        Write("sub/.gitignore", "ignored.cs\n");
        var result = await Analyze(subfolder: "sub");
        Assert.Single(result.Files); Assert.Equal("include.cs", result.Files[0].Path);
        Assert.Equal(1, result.Deleted);
        var excluded = await Analyze(subfolder: "sub", exclusions: ["include.cs"]);
        Assert.Empty(excluded.Files);
    }

    [Fact]
    public async Task Rename_into_project_counts_full_file_and_detached_head_is_named()
    {
        Write("outside.cs", "one\ntwo\n"); Commit(-10);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Git("mv", "outside.cs", "sub/inside.cs"); Commit(-1);
        Git("checkout", "--detach");
        var result = await Analyze(subfolder: "sub");
        Assert.Equal(2, result.Added); Assert.Equal(0, result.Deleted);
        Assert.Equal("inside.cs", Assert.Single(result.Files).Path);
        Assert.StartsWith("Detached ", result.Branch);
    }

    [Fact]
    public async Task Merge_is_counted_once_against_first_parent()
    {
        Write("base.cs", "base\n"); Commit(-10);
        Git("checkout", "-b", "feature");
        Write("feature.cs", "feature\n"); Commit(-3);
        Git("checkout", "main");
        Write("main.cs", "main\n"); Commit(-2);
        GitAt(-1, "merge", "--no-ff", "feature", "-m", "merge");
        var result = await Analyze();
        Assert.Equal(2, result.Added); Assert.Equal(2, result.Commits);
        Assert.Equal("main", result.Branch);
    }

    [Fact]
    public async Task Disabled_does_not_start_git_and_missing_git_is_reported()
    {
        var missing = new GitHistoryAnalyzer(new FileClassifier(new LanguageRegistry()), "codelines-nonexistent-git-12345");
        var disabled = await missing.AnalyzeAsync(Project(), new(), new(), _end);
        Assert.Equal("Disabled", disabled.Status);
        var failed = await missing.AnalyzeAsync(Project(), new(), new() { Enabled = true }, _end);
        Assert.StartsWith("Unavailable:", failed.Status);
        Assert.True(failed.IsIncomplete);
    }

    [Fact]
    public async Task Empty_repository_and_cancellation_and_invalid_days()
    {
        Assert.Equal("No commits", (await Analyze()).Status);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _analyzer.AnalyzeAsync(Project(), new(), new() { Enabled = true }, _end, cts.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Analyze(days: 0));
        var project = Project(); project.RootPath = Path.Combine(_root, "not-present");
        Assert.StartsWith("Unavailable:", (await _analyzer.AnalyzeAsync(project, new(), new() { Enabled = true }, _end)).Status);
    }

    [Fact]
    public async Task Shallow_history_never_counts_boundary_as_a_new_repository()
    {
        Write("a.cs", "one\n"); Commit(-10);
        Write("a.cs", "two\n"); Commit(-1);
        var clone = Path.Combine(_root, "shallow");
        Git("clone", "--depth", "1", new Uri(_root + Path.DirectorySeparatorChar).AbsoluteUri, clone);
        var project = Project(); project.RootPath = clone;
        var activity = await _analyzer.AnalyzeAsync(project, new(), new() { Enabled = true }, _end);
        Assert.True(activity.IsIncomplete); Assert.Empty(activity.Files);
        var net = await _analyzer.AnalyzeAsync(project, new(), new() { Enabled = true, Mode = GitHistoryMode.NetChange }, _end);
        Assert.StartsWith("Unavailable:", net.Status); Assert.Empty(net.Files);
    }

    [Fact]
    public async Task Settings_and_history_exports_roundtrip_and_old_settings_default_disabled()
    {
        var path = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(path, "{}");
        var repository = new JsonProjectRepository(path);
        Assert.False((await repository.LoadAsync()).GitHistory.Enabled);
        Write("a.cs", "one\n"); Commit(-1);
        var snapshot = new GitHistorySnapshot { Start = _end.AddDays(-7), End = _end, Mode = GitHistoryMode.Activity, Projects = [await Analyze()] };
        await repository.SaveAsync(new() { GitHistory = new() { Enabled = true, Days = 365 }, LastGitHistory = snapshot });
        var loaded = await repository.LoadAsync();
        Assert.True(loaded.GitHistory.Enabled); Assert.Equal(365, loaded.GitHistory.Days);
        Assert.Equal(1, loaded.LastGitHistory!.Projects[0].Added);
        await GitHistoryExportService.ExportAsync(snapshot, Path.Combine(_root, "history.csv"));
        await GitHistoryExportService.ExportAsync(snapshot, Path.Combine(_root, "history.json"));
        Assert.Contains("ModifiedEstimate", await File.ReadAllTextAsync(Path.Combine(_root, "history.csv")));
        Assert.Contains("a.cs", await File.ReadAllTextAsync(Path.Combine(_root, "history.csv")));
        Assert.Contains("\"mode\": \"Activity\"", await File.ReadAllTextAsync(Path.Combine(_root, "history.json")));
    }

    [Fact]
    public void Modified_estimate_pairs_only_adjacent_replacements_and_handles_header_like_source()
    {
        const string patch = "diff --git a/a b/a\n--- a/a\n+++ b/a\n@@ -1 +1 @@\n---source\n+++source\n@@ -3,0 +3 @@\n+addition\n@@ -6 +6,0 @@\n-deletion\n";
        Assert.Equal(1, GitHistoryAnalyzer.EstimateModified(patch));
    }

    private ProjectDefinition Project(string? subfolder = null) => new() { Name = "Test", RootPath = subfolder is null ? _root : Path.Combine(_root, subfolder) };
    private Task<GitProjectHistory> Analyze(GitHistoryMode mode = GitHistoryMode.Activity, int days = 7, string? subfolder = null, List<string>? exclusions = null)
    {
        var project = Project(subfolder); project.ExcludePatterns = exclusions ?? [];
        return _analyzer.AnalyzeAsync(project, new(), new() { Enabled = true, Days = days, Mode = mode }, _end);
    }
    private void Write(string path, string text)
    {
        var full = Path.Combine(_root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, text);
    }
    private void Commit(int days) { Git("add", "."); GitAt(days, "commit", "-m", "test"); }
    private void Git(params string[] args) => GitAt(null, args);
    private void GitAt(int? days, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        if (days is not null)
        {
            var date = _end.AddDays(days.Value).ToString("O");
            info.Environment["GIT_AUTHOR_DATE"] = date; info.Environment["GIT_COMMITTER_DATE"] = date;
        }
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult());
    }
    public void Dispose()
    {
        // Git object files can be read-only on Windows.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
