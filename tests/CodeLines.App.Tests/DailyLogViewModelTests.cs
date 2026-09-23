using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodeLines.App;
using CodeLines.App.Services;
using CodeLines.App.ViewModels;
using CodeLines.Core.Abstractions;
using CodeLines.Core.AiUsage;
using CodeLines.Core.AiUsage.Agents;
using CodeLines.Core.Models;
using CodeLines.Core.Services;

namespace CodeLines.App.Tests;

public sealed class DailyLogViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codelines-daily-vm-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task Scans_on_the_same_day_keep_one_record_with_the_last_values_and_refresh_AI_usage()
    {
        var source = Path.Combine(_root, "Program.cs");
        await File.WriteAllTextAsync(source, "class A\n{\n}\n");
        var dailyLog = new MemoryDailyLog();
        var builder = new FakeBuilder(_root);
        var vm = Create(new MemoryRepository(_root), builder, dailyLog);
        await vm.InitializeAsync();

        await vm.ScanAllCommand.ExecuteAsync(null);
        await WaitUntil(() => builder.Calls == 1 && !vm.IsAiUsageRunning);
        await File.AppendAllTextAsync(source, "class B\n{\n}\n");
        await vm.ScanSelectedProjectCommand.ExecuteAsync(null);
        await WaitUntil(() => builder.Calls == 2 && !vm.IsAiUsageRunning);

        var today = DailyLogRecorder.DayKey(DateTimeOffset.Now);
        var day = dailyLog.Saved!.Days.Single(d => d.Date == today);
        Assert.Equal(6, Assert.Single(day.Lines).PhysicalLines);
        Assert.Equal(3000, day.Ai!.Total.Tokens);
        Assert.StartsWith("Completed", vm.StatusText);

        var row = vm.DailyLogRows.First(r => r.Date == today);
        Assert.Equal(6, row.PhysicalLines);
        Assert.Equal(3000, row.AiTokens);
        Assert.Equal(today, vm.CompareDateB);
        Assert.True(vm.CanExportDailyLog);

        var csv = Path.Combine(_root, "daily.csv");
        await vm.ExportDailyLogAsync(csv);
        Assert.Contains(today, await File.ReadAllTextAsync(csv));
    }

    [Fact]
    public async Task The_AI_refresh_after_a_scan_can_be_turned_off()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Program.cs"), "class A { }\n");
        var repository = new MemoryRepository(_root) { Settings = { DailyLog = { RefreshAiAfterScan = false } } };
        var builder = new FakeBuilder(_root);
        var vm = Create(repository, builder, new MemoryDailyLog());
        await vm.InitializeAsync();
        Assert.False(vm.RefreshAiAfterScan);

        await vm.ScanAllCommand.ExecuteAsync(null);
        await Task.Delay(50);

        Assert.Equal(0, builder.Calls);
        vm.RefreshAiAfterScan = true;
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.True(repository.Settings.DailyLog.RefreshAiAfterScan);
    }

    [Fact]
    public async Task Comparing_two_days_and_filtering_by_project()
    {
        var repository = new MemoryRepository(_root);
        var project = repository.Settings.Projects[0];
        var log = new DailyLogFile();
        DailyLogRecorder.RecordLines(log, [Metrics(project, 100)], CountingScope.SourceOnly, "o200k_base", new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero));
        DailyLogRecorder.RecordLines(log, [Metrics(project, 140)], CountingScope.SourceOnly, "o200k_base", new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero));
        var vm = Create(repository, new FakeBuilder(_root), new MemoryDailyLog { Saved = log });
        await vm.InitializeAsync();

        Assert.Equal(["2026-09-22", "2026-09-21"], vm.DailyLogDates);
        Assert.Equal(("2026-09-21", "2026-09-22"), (vm.CompareDateA, vm.CompareDateB));
        Assert.Equal("+40", vm.DailyCompareRows.Single(r => r.Metric == "Physical lines").DeltaText);

        vm.SelectedDailyLogProject = vm.DailyLogProjectOptions.Single(o => o.Id == project.Id);
        Assert.Equal(2, vm.DailyLogRows.Count);
        vm.CompareDateA = "2026-09-22";
        Assert.Equal("0", vm.DailyCompareRows.Single(r => r.Metric == "Physical lines").DeltaText);
    }

    [Fact]
    public void Daily_log_page_renders()
    {
        WpfTestHost.Run(() =>
        {
            var repository = new MemoryRepository(_root);
            var project = repository.Settings.Projects[0];
            var log = new DailyLogFile();
            DailyLogRecorder.RecordLines(log, [Metrics(project, 100)], CountingScope.SourceOnly, "o200k_base", new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero));
            DailyLogRecorder.RecordLines(log, [Metrics(project, 140)], CountingScope.AllText, "o200k_base", new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero));
            var vm = Create(repository, new FakeBuilder(_root), new MemoryDailyLog { Saved = log });
            vm.InitializeAsync().GetAwaiter().GetResult();
            var window = new MainWindow { DataContext = vm };
            vm.CurrentPage = MainViewModel.DailyLogPageName;
            var root = (System.Windows.Controls.Grid)window.Content;
            root.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "AppBackgroundBrush");
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/daily-log-ui"));
            Directory.CreateDirectory(output);
            foreach (var theme in new[] { "Light", "Dark" })
            {
                ThemeManager.Apply(theme);
                root.Measure(new Size(1440, 860)); root.Arrange(new Rect(0, 0, 1440, 860)); root.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1440, 860, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"daily-log-{theme}.png")); encoder.Save(file);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("DailyLogPage")).Visibility);
                Assert.True(((FrameworkElement)window.FindName("DailyLogGrid")).ActualHeight >= 80);
                Assert.True(((FrameworkElement)window.FindName("DailyCompareGrid")).ActualHeight >= 80);
            }
            Assert.Contains("not directly comparable", vm.DailyCompareWarning);
            window.Close();
        });
    }

    private static MainViewModel Create(MemoryRepository repository, FakeBuilder builder, MemoryDailyLog dailyLog) => new(repository,
        new SourceScanner(new FileClassifier(new LanguageRegistry()), new GitIgnoreRuleProvider(), new LineMetricsAnalyzer(), new TokenCounter()),
        new ExportService(), _ => { }, aiUsage: builder, dailyLog: dailyLog);

    private static ProjectMetrics Metrics(ProjectDefinition project, long lines)
    {
        var file = new FileMetrics(project.Name, "Program.cs", ".cs", "C#", FileCategory.SourceCode, lines * 10, lines * 3,
            new LineMetrics(lines, 0, lines, 0, 0, 0));
        return ProjectMetrics.Create(project, [file], [], TimeSpan.Zero);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class MemoryRepository(string root) : IProjectRepository
    {
        public string SettingsPath => "memory";
        public AppSettings Settings { get; set; } = new() { Projects = [new() { Name = "Alpha", RootPath = root }] };
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { Settings = settings; return Task.CompletedTask; }
    }

    private sealed class FakeBuilder(string root) : IAiUsageReportBuilder
    {
        public int Calls;
        public string OutputDirectory { get; } = Directory.CreateDirectory(Path.Combine(root, "ai-usage")).FullName;
        public IReadOnlyList<(string Id, string Label)> KnownAgents => [("codex", "Codex")];

        public Task<AiUsageResult> BuildAsync(AiUsageOptions options, IProgress<AiUsageProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            var record = AgentRecord.Create(DateTimeOffset.Now.ToUnixTimeMilliseconds(), "gpt-5.5", 1000, output: 2000, session: "s1", request: "r1",
                cwd: Path.Combine(root, "src"));
            var combined = AgentUsageAggregator.Aggregate([new("codex", "Codex", null, [record])], "All agents", new UsageClock(false));
            return Task.FromResult(new AiUsageResult
            {
                OutputDirectory = OutputDirectory,
                DashboardFile = Path.Combine(OutputDirectory, "dashboard.html"),
                GeneratedAt = DateTimeOffset.Now,
                Tabs = [new("all", "All agents", "agents/all.html", 3000)],
                Agents = [new("codex", "Codex", AiUsageAgentState.Loaded, 1)],
                Combined = combined
            });
        }
    }
}

internal sealed class MemoryDailyLog : IDailyLogRepository
{
    public DailyLogFile? Saved { get; set; }
    public string LogPath => "memory";
    public Task<DailyLogFile> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Saved ?? new DailyLogFile());
    public Task SaveAsync(DailyLogFile log, CancellationToken cancellationToken = default) { Saved = log; return Task.CompletedTask; }
}
