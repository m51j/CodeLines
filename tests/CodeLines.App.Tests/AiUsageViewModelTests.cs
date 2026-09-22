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

public sealed class AiUsageViewModelTests
{
    [Fact]
    public async Task Refresh_shows_the_report_agent_status_and_project_usage()
    {
        var repository = new MemoryRepository();
        var builder = new FakeBuilder();
        var vm = Create(repository, builder);
        await vm.InitializeAsync();
        var version = vm.AiUsageReportVersion;

        await vm.RefreshAiUsageCommand.ExecuteAsync(null);

        Assert.True(vm.HasAiUsageReport);
        Assert.True(vm.CanExportAiUsage);
        Assert.Equal(version + 1, vm.AiUsageReportVersion);
        Assert.StartsWith("Updated", vm.AiUsageStatus);
        Assert.Contains("Codex 3.0K", vm.AiUsageDetails);
        Assert.Contains("Copilot Chat: skipped: locked", vm.AiUsageDetails);
        Assert.Equal("1,000 records", vm.AiUsageAgents.Single(a => a.Id == "codex").Status);
        Assert.Equal("not found", vm.AiUsageAgents.Single(a => a.Id == "claude").Status);
        var project = vm.Projects.Single();
        Assert.Equal(3000, project.AiTokens);
        Assert.Equal(3000, project.AiTokens30Days);
        Assert.Equal(1, project.AiSessions);
        Assert.Equal("3.0K", vm.AiTokens30DaysText);
    }

    [Fact]
    public async Task Opening_the_tab_refreshes_once_per_session_when_enabled()
    {
        var builder = new FakeBuilder();
        var vm = Create(new MemoryRepository(), builder);
        await vm.InitializeAsync();

        vm.CurrentPage = MainViewModel.AiUsagePageName;
        await WaitUntil(() => builder.Calls == 1 && !vm.IsAiUsageRunning);
        vm.CurrentPage = "Dashboard";
        vm.CurrentPage = MainViewModel.AiUsagePageName;
        await Task.Delay(50);
        Assert.Equal(1, builder.Calls);

        var manual = Create(new MemoryRepository { Settings = { AiUsage = { AutoRefreshOnOpen = false } } }, new FakeBuilder());
        await manual.InitializeAsync();
        manual.CurrentPage = MainViewModel.AiUsagePageName;
        await Task.Delay(50);
        Assert.False(manual.IsAiUsageRunning);
    }

    [Fact]
    public async Task Cancel_stops_a_running_refresh()
    {
        var builder = new FakeBuilder { WaitForCancellation = true };
        var vm = Create(new MemoryRepository(), builder);
        await vm.InitializeAsync();
        var refresh = vm.RefreshAiUsageCommand.ExecuteAsync(null);
        await builder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsAiUsageRunning);
        Assert.True(vm.CancelAiUsageCommand.CanExecute(null));

        vm.CancelAiUsageCommand.Execute(null);
        await refresh;

        Assert.False(vm.IsAiUsageRunning);
        Assert.Equal("AI usage refresh cancelled", vm.AiUsageStatus);
        Assert.False(vm.HasAiUsageReport);
    }

    [Fact]
    public async Task Settings_reach_the_builder_and_are_saved()
    {
        var repository = new MemoryRepository();
        var builder = new FakeBuilder();
        var vm = Create(repository, builder);
        await vm.InitializeAsync();
        vm.AiUsageAgents.Single(a => a.Id == "codex").IsEnabled = false;
        vm.AiUsageUseUtc = true;
        vm.AiUsageDeduplicate = false;
        vm.AiUsageExtraRoots = @"E:\transcripts" + Environment.NewLine + "  ";

        await vm.RescanAiUsageCommand.ExecuteAsync(null);
        await vm.SaveSettingsCommand.ExecuteAsync(null);

        var options = builder.LastOptions!;
        Assert.Equal(["codex"], options.DisabledAgents);
        Assert.True(options.UseUtc && options.Rescan && !options.Deduplicate);
        Assert.Equal([@"E:\transcripts"], options.ExtraClaudeRoots);
        var saved = repository.Settings.AiUsage;
        Assert.Equal(["codex"], saved.DisabledAgents);
        Assert.True(saved.UseUtc);
        Assert.False(saved.Deduplicate);
        Assert.Equal([@"E:\transcripts"], saved.ExtraClaudeRoots);
    }

    [Fact]
    public async Task Turning_the_feature_off_leaves_the_tab()
    {
        var vm = Create(new MemoryRepository { Settings = { AiUsage = { AutoRefreshOnOpen = false } } }, new FakeBuilder());
        await vm.InitializeAsync();
        vm.CurrentPage = MainViewModel.AiUsagePageName;
        vm.AiUsageEnabled = false;
        Assert.Equal("Settings", vm.CurrentPage);
        Assert.False(vm.RefreshAiUsageCommand.CanExecute(null));
    }

    [Fact]
    public async Task Export_writes_html_or_json()
    {
        var builder = new FakeBuilder();
        var vm = Create(new MemoryRepository(), builder);
        await vm.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.ExportAiUsageAsync(Path.Combine(builder.OutputDirectory, "x.json")));
        await vm.RefreshAiUsageCommand.ExecuteAsync(null);

        var json = Path.Combine(builder.OutputDirectory, "export.json");
        await vm.ExportAiUsageAsync(json);
        Assert.Contains("\"codex::gpt-5.5\"", await File.ReadAllTextAsync(json));
    }

    [Fact]
    public void AI_usage_page_renders_with_an_empty_state()
    {
        WpfTestHost.Run(() =>
        {
            var window = new MainWindow();
            var vm = Create(new MemoryRepository(), new FakeBuilder());
            window.DataContext = vm;
            vm.AiUsageAutoRefresh = false;
            vm.CurrentPage = MainViewModel.AiUsagePageName;
            vm.AiUsageStatus = "Reading Claude Code transcripts • 120/2,085 files";
            vm.IsAiUsageRunning = true;
            var root = (System.Windows.Controls.Grid)window.Content;
            root.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "AppBackgroundBrush");
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ai-usage-ui"));
            Directory.CreateDirectory(output);
            foreach (var theme in new[] { "Light", "Dark" })
            {
                ThemeManager.Apply(theme);
                root.Measure(new Size(1440, 860)); root.Arrange(new Rect(0, 0, 1440, 860)); root.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1440, 860, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"ai-usage-{theme}.png")); encoder.Save(file);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("AiUsagePage")).Visibility);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("AiUsageEmptyState")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("AiUsageBrowser")).Visibility);
            }
            vm.HasAiUsageReport = true; root.UpdateLayout();
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("AiUsageBrowser")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("AiUsageEmptyState")).Visibility);
            vm.AiUsageEnabled = false; root.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("AiUsagePage")).Visibility);
            window.Close();
        });
    }

    private static MainViewModel Create(MemoryRepository repository, FakeBuilder builder) => new(repository,
        new SourceScanner(new FileClassifier(new LanguageRegistry()), new GitIgnoreRuleProvider(), new LineMetricsAnalyzer(), new TokenCounter()),
        new ExportService(), _ => { }, aiUsage: builder);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class MemoryRepository : IProjectRepository
    {
        public string SettingsPath => "memory";
        public AppSettings Settings { get; set; } = new() { Projects = [new() { Name = "Alpha", RootPath = @"D:\Work\alpha" }] };
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { Settings = settings; return Task.CompletedTask; }
    }

    private sealed class FakeBuilder : IAiUsageReportBuilder
    {
        public int Calls;
        public bool WaitForCancellation;
        public AiUsageOptions? LastOptions;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string OutputDirectory { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codelines-aiu-vm-" + Guid.NewGuid().ToString("N"))).FullName;
        public IReadOnlyList<(string Id, string Label)> KnownAgents => [("claude", "Claude Code"), ("codex", "Codex"), ("copilot", "Copilot Chat")];

        public async Task<AiUsageResult> BuildAsync(AiUsageOptions options, IProgress<AiUsageProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            LastOptions = options;
            progress?.Report(new("codex", "Reading Codex"));
            Started.TrySetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            var now = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
            var record = AgentRecord.Create(now, "gpt-5.5", 1000, output: 2000, session: "s1", request: "r1", cwd: @"D:\Work\alpha\src");
            var combined = AgentUsageAggregator.Aggregate([new("codex", "Codex", null, [record])], "All agents", new UsageClock(true));
            return new AiUsageResult
            {
                OutputDirectory = OutputDirectory,
                DashboardFile = Path.Combine(OutputDirectory, "dashboard.html"),
                GeneratedAt = DateTimeOffset.UtcNow,
                Tabs = [new("all", "All agents", "agents/all.html", 3000), new("codex", "Codex", "agents/codex.html", 3000)],
                Agents = [new("claude", "Claude Code", AiUsageAgentState.NotFound), new("codex", "Codex", AiUsageAgentState.Loaded, 1000),
                    new("copilot", "Copilot Chat", AiUsageAgentState.Failed, Error: "locked")],
                Combined = combined
            };
        }
    }
}
