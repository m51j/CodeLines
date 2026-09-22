using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodeLines.App;
using CodeLines.App.Services;
using CodeLines.App.ViewModels;
using CodeLines.Core.Abstractions;
using CodeLines.Core.Models;
using CodeLines.Core.Services;

namespace CodeLines.App.Tests;

public sealed class GitHistoryViewModelTests
{
    [Fact]
    public async Task Discovery_uses_indeterminate_progress_until_work_count_is_known()
    {
        var analyzer = new FakeAnalyzer { PauseOnCall = 1, Report = new("Reading commit history") };
        var vm = Create(new(), analyzer);
        await vm.InitializeAsync(); vm.GitHistoryEnabled = true;
        var refresh = vm.RefreshHistoryCommand.ExecuteAsync(null);
        await analyzer.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntil(() => vm.HistoryProgressText.Contains("Reading commit history"));
        Assert.True(vm.IsProgressIndeterminate);
        Assert.Contains("not known", vm.HistoryWorkText);
        Assert.DoesNotContain("ETA", vm.HistoryTimingText);
        vm.CancelHistoryCommand.Execute(null); await refresh;
        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public async Task Unavailable_projects_are_visible_and_do_not_make_aggregate_look_complete()
    {
        var repository = new MemoryRepository();
        repository.Settings.Projects.Add(new() { Name = "Missing", RootPath = Path.GetTempPath() });
        var vm = Create(repository, new() { UnavailableOnCall = 2 });
        await vm.InitializeAsync(); vm.GitHistoryEnabled = true;
        await vm.RefreshHistoryCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.HistoryResults.Count); Assert.Equal(10, vm.HistoryTotals.Added);
        Assert.Equal(1, vm.HistoryTotals.IncompleteProjects);
        Assert.Contains("1 incomplete/unavailable", vm.HistoryTotalsText);
        Assert.Contains("1 incomplete/unavailable", vm.StatusText);
        Assert.True(vm.CanExportHistory);
    }

    [Fact]
    public async Task Running_progress_partial_totals_and_final_totals_are_visible_without_stale_updates()
    {
        var repository = new MemoryRepository();
        repository.Settings.Projects.Add(new() { Name = "Second", RootPath = Path.GetTempPath() });
        var analyzer = new FakeAnalyzer { PauseOnCall = 2 };
        var vm = Create(repository, analyzer);
        await vm.InitializeAsync(); vm.GitHistoryEnabled = true;
        var refresh = vm.RefreshHistoryCommand.ExecuteAsync(null);
        await analyzer.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntil(() => vm.HistoryCurrentPath.Contains("work.cs"));
        Assert.Single(vm.HistoryResults); Assert.Equal(10, vm.HistoryTotals.Added);
        Assert.Contains("Partial", vm.HistoryTotalsText);
        Assert.Contains("Project 2/2", vm.HistoryProgressText);
        Assert.Contains("2/4 commits", vm.HistoryWorkText);
        Assert.Contains("1/3", vm.HistoryWorkText);
        Assert.InRange(vm.HistoryProjectProgress, 50, 75);
        Assert.InRange(vm.ProgressValue, 75, 99);
        Assert.False(vm.HistoryProgressIndeterminate);
        Assert.False(vm.CanExportHistory);
        Assert.False(vm.RefreshHistoryCommand.CanExecute(null));
        Assert.False(vm.ScanAllCommand.CanExecute(null));
        var elapsed = vm.HistoryTimingText;
        await WaitUntil(() => vm.HistoryTimingText != elapsed);
        analyzer.Continue.TrySetResult(); await refresh;
        Assert.Equal(20, vm.HistoryTotals.Added); Assert.Equal(4, vm.HistoryTotals.Deleted);
        Assert.Equal(16, vm.HistoryTotals.Net); Assert.Equal(2, vm.HistoryTotals.Projects);
        Assert.Equal(100, vm.ProgressValue); Assert.False(vm.IsProgressIndeterminate);
        Assert.True(vm.CanExportHistory); Assert.True(vm.RefreshHistoryCommand.CanExecute(null));
        Assert.Equal(20, repository.Settings.LastGitHistory!.Totals.Added);
        var status = vm.StatusText;
        await Task.Delay(350); Assert.Equal(status, vm.StatusText);
    }

    [Fact]
    public async Task Cancellation_keeps_finished_projects_marked_partial_and_export_disabled()
    {
        var repository = new MemoryRepository();
        repository.Settings.Projects.Add(new() { Name = "Second", RootPath = Path.GetTempPath() });
        var analyzer = new FakeAnalyzer { PauseOnCall = 2 };
        var vm = Create(repository, analyzer);
        await vm.InitializeAsync(); vm.GitHistoryEnabled = true;
        var refresh = vm.RefreshHistoryCommand.ExecuteAsync(null);
        await analyzer.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.CancelActiveAnalysisCommand.Execute(null);
        await refresh;
        Assert.Single(vm.HistoryResults); Assert.Contains("Partial", vm.HistoryTotalsText);
        Assert.Contains("cancelled", vm.StatusText); Assert.False(vm.IsHistoryRunning);
        Assert.False(vm.IsProgressIndeterminate); Assert.False(vm.CanExportHistory);
        Assert.True(vm.RefreshHistoryCommand.CanExecute(null));
    }

    [Fact]
    public async Task Empty_project_selection_is_not_reported_as_completed_analysis()
    {
        var repository = new MemoryRepository(); repository.Settings.Projects.Clear();
        var analyzer = new FakeAnalyzer(); var vm = Create(repository, analyzer);
        await vm.InitializeAsync(); vm.GitHistoryEnabled = true;
        await vm.RefreshHistoryCommand.ExecuteAsync(null);
        Assert.Equal(0, analyzer.Calls); Assert.Contains("No enabled projects", vm.StatusText);
        Assert.Equal(0, vm.ProgressValue); Assert.False(vm.CanExportHistory);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }

    [Fact]
    public async Task Disabled_guard_option_invalidation_and_settings_persistence()
    {
        var repository = new MemoryRepository();
        var analyzer = new FakeAnalyzer();
        var vm = Create(repository, analyzer);
        await vm.InitializeAsync();
        await vm.RefreshHistoryCommand.ExecuteAsync(null);
        Assert.Equal(0, analyzer.Calls);
        vm.GitHistoryEnabled = true;
        await vm.RefreshHistoryCommand.ExecuteAsync(null);
        Assert.Single(vm.HistoryResults);
        Assert.True(repository.Settings.GitHistory.Enabled);
        Assert.NotNull(repository.Settings.LastGitHistory);
        vm.HistoryPeriod = "30";
        Assert.Empty(vm.HistoryResults);
        await vm.RefreshHistoryCommand.ExecuteAsync(null);
        Assert.Equal(30, repository.Settings.GitHistory.Days);
        vm.SelectedScope = CountingScope.AllText;
        Assert.Empty(vm.HistoryResults);
        await vm.RefreshHistoryCommand.ExecuteAsync(null);
        vm.Projects[0].Exclusions = "*.cs";
        Assert.Empty(vm.HistoryResults);
        vm.CurrentPage = "Code changes";
        vm.GitHistoryEnabled = false;
        Assert.Equal("Settings", vm.CurrentPage);
        var calls = analyzer.Calls;
        await vm.RefreshHistoryCommand.ExecuteAsync(null);
        Assert.Equal(calls, analyzer.Calls);
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.False(repository.Settings.GitHistory.Enabled);
        Assert.Null(repository.Settings.LastGitHistory);
    }

    [Fact]
    public async Task Disabling_cancels_active_history_and_does_not_publish_results()
    {
        var analyzer = new FakeAnalyzer { WaitForCancellation = true };
        var vm = Create(new(), analyzer);
        await vm.InitializeAsync(); vm.GitHistoryEnabled = true;
        var refresh = vm.RefreshHistoryCommand.ExecuteAsync(null);
        await analyzer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.GitHistoryEnabled = false;
        await refresh;
        Assert.Empty(vm.HistoryResults); Assert.False(vm.IsBusy);
    }

    [Fact]
    public void History_page_renders_in_both_themes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {

                var app = new App(); app.InitializeComponent();
                var window = new MainWindow();
                var vm = Create(new(), new());
                vm.GitHistoryEnabled = true; vm.CurrentPage = "Code changes";
                vm.HistorySummary = "Snapshot 2026-09-06 • 2026-08-30 — 2026-09-06 • Activity • SourceOnly. Refresh to check current branch / HEAD.";
                var result = new GitProjectHistory { ProjectName = "Example project", Branch = "main", Head = new string('a', 40), Commits = 12,
                    Files = [new("src/Program.cs", 150, 40, 25, false), new("src/ملف.cs", 30, 10, 8, false)] };
                vm.HistoryResults.Add(result); vm.SelectedHistory = result;
                vm.IsHistoryRunning = vm.IsBusy = true;
                vm.HistorySummary = "Analysis in progress. Totals include processed projects only; export is available when finished.";
                vm.HistoryTotalsText = "Partial totals • 1/7 projects processed • 0 incomplete/unavailable";
                vm.HistoryProgressText = "Project 2/7 — Current project • Analyzing files";
                vm.HistoryWorkText = "12/30 commits complete • 18 remaining • Files in current comparison: 8/20 (12 remaining) • 5 projects after this one";
                vm.HistoryCurrentPath = "Commit abc12345 • src/Services/GitHistoryAnalyzer.cs";
                vm.HistoryTimingText = "Elapsed 1m 12s • Current project 0m 45s • Current project ETA ≈ 1m 05s (varies by commit size)";
                vm.HistoryProjectProgress = 41; vm.ProgressValue = 20;
                window.DataContext = vm;
                var root = (System.Windows.Controls.Grid)window.Content;
                root.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "AppBackgroundBrush");
                var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/history-ui"));
                Directory.CreateDirectory(output);
                foreach (var theme in new[] { "Light", "Dark" })
                foreach (var width in new[] { 1440, 1100 })
                {
                    ThemeManager.Apply(theme);
                    var height = width == 1440 ? 860 : 660;
                    root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, $"history-{theme}-{width}.png")); encoder.Save(file);
                    Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("HistoryPage")).Visibility);
                    Assert.True(((FrameworkElement)window.FindName("HistoryProjectGrid")).ActualHeight >= 80);
                    Assert.True(((FrameworkElement)window.FindName("HistoryFilesGrid")).ActualHeight >= 80);
                }
                vm.GitHistoryEnabled = false; root.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("HistoryPage")).Visibility);
                window.Close(); app.Shutdown();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF render timed out.");
        if (failure is not null) throw new Exception("WPF smoke test failed", failure);
    }

    private static MainViewModel Create(MemoryRepository repository, FakeAnalyzer analyzer) => new(repository,
        new SourceScanner(new FileClassifier(new LanguageRegistry()), new GitIgnoreRuleProvider(), new LineMetricsAnalyzer(), new TokenCounter()),
        new ExportService(), _ => { }, analyzer);

    private sealed class MemoryRepository : IProjectRepository
    {
        public string SettingsPath => "memory";
        public AppSettings Settings { get; private set; } = new() { Projects = [new() { Name = "Test", RootPath = Path.GetTempPath() }] };
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { Settings = settings; return Task.CompletedTask; }
    }
    private sealed class FakeAnalyzer : IGitHistoryAnalyzer
    {
        public int Calls;
        public bool WaitForCancellation;
        public int? PauseOnCall;
        public int? UnavailableOnCall;
        public GitHistoryProgress Report = new("Analyzing files", 2, 4, 1, 3, "abc12345", "src/work.cs");
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GitProjectHistory> AnalyzeAsync(ProjectDefinition project, ScanOptions scanOptions, GitHistoryOptions options, DateTimeOffset end, CancellationToken cancellationToken = default, IProgress<GitHistoryProgress>? progress = null)
        {
            Interlocked.Increment(ref Calls); Started.TrySetResult();
            progress?.Report(Report);
            if (Calls == PauseOnCall) { Paused.TrySetResult(); await Continue.Task.WaitAsync(cancellationToken); }
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Calls == UnavailableOnCall) return new() { ProjectId = project.Id, ProjectName = project.Name, IsIncomplete = true, Status = "Unavailable: missing Git" };
            return new() { ProjectId = project.Id, ProjectName = project.Name, Branch = "main", Head = new string('a', 40),
                Commits = 4, Files = [new("a.cs", 10, 2, 1, false)] };
        }
    }
}
