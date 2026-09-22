using CodeLines.Core.AiUsage;
using CodeLines.Core.AiUsage.Agents;

namespace CodeLines.Tests.AiUsage;

public sealed class AiUsageReportBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codelines-aiu-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private AiUsageReportBuilder CreateBuilder(params IAgentUsageAdapter[] adapters)
    {
        var home = Path.Combine(_root, "home");
        CopyDirectory(Path.Combine(ClaudeParityTests.FixtureRoot, "claude"), Path.Combine(home, ".claude", "projects"));
        var data = Path.Combine(_root, "data");
        var environment = new AgentEnvironment(home, Path.Combine(_root, "roaming"), Path.Combine(_root, "local"), Path.Combine(data, "cache"));
        return new AiUsageReportBuilder(data, environment, adapters, TimeZoneInfo.Utc);
    }

    [Fact]
    public async Task Builds_claude_report_combined_page_and_dashboard()
    {
        var builder = CreateBuilder(new FakeAdapter("codex", "Codex", [AgentRecord.Create(1_788_000_000_000, "gpt-5.5", 100, output: 50, session: "s", request: "r1", cwd: @"D:\Work\beta")]),
            new FakeAdapter("zcode", "ZCode", [], detected: false));
        var progress = new List<AiUsageProgress>();

        var result = await builder.BuildAsync(new AiUsageOptions(), new SyncProgress<AiUsageProgress>(progress.Add));

        Assert.True(result.HasData);
        Assert.Equal(["all", "claude", "codex"], result.Tabs.Select(t => t.Id));
        Assert.Equal(AiUsageAgentState.Loaded, result.Agents.Single(a => a.Id == "claude").State);
        Assert.Equal(AiUsageAgentState.NotFound, result.Agents.Single(a => a.Id == "zcode").State);
        Assert.Contains(progress, p => p.Agent == "claude" && p.Total > 0);
        foreach (var page in new[] { "dashboard.html", "report.html", @"agents\all.html", @"agents\codex.html" })
        {
            var html = await File.ReadAllTextAsync(Path.Combine(builder.OutputDirectory, page));
            Assert.DoesNotContain("__CCSTATS", html);
            Assert.DoesNotContain("/*__", html);
        }
        var dashboard = await File.ReadAllTextAsync(result.DashboardFile);
        Assert.Contains("data-href=\"report.html\"", dashboard);
        Assert.Contains("data-href=\"agents/codex.html\"", dashboard);
        Assert.Contains("<title>Codex Usage</title>", await File.ReadAllTextAsync(Path.Combine(builder.OutputDirectory, "agents", "codex.html")));
        var all = result.Combined!;
        Assert.Equal(["claude", "codex"], all.Agents.Keys.Order());
        // gpt-5.5 at list price: 100 input at $5 and 50 output at $30 per million.
        Assert.Equal((100 * 5 + 50 * 30) / 1e6, all.TotalsByAgent()["codex"][AgentVector.Cost], 12);
    }

    [Fact]
    public async Task Second_build_reuses_the_cache_and_gives_the_same_totals()
    {
        var builder = CreateBuilder();
        var first = await builder.BuildAsync(new AiUsageOptions());
        var cache = Path.Combine(_root, "data", "cache", "claude-index.json");
        Assert.True(File.Exists(cache));
        var second = await builder.BuildAsync(new AiUsageOptions());
        var rescan = await builder.BuildAsync(new AiUsageOptions { Rescan = true });
        Assert.Equal(first.Combined!.Totals(), second.Combined!.Totals());
        Assert.Equal(first.Combined!.Totals(), rescan.Combined!.Totals());
    }

    [Fact]
    public async Task Disabled_agents_are_skipped_and_their_old_pages_removed()
    {
        var builder = CreateBuilder(new FakeAdapter("codex", "Codex", [AgentRecord.Create(1_788_000_000_000, "gpt-5.5", 1, request: "r")]));
        await builder.BuildAsync(new AiUsageOptions());
        Assert.True(File.Exists(Path.Combine(builder.OutputDirectory, "agents", "codex.html")));

        var result = await builder.BuildAsync(new AiUsageOptions { DisabledAgents = ["codex"] });

        Assert.Equal(AiUsageAgentState.Disabled, result.Agents.Single(a => a.Id == "codex").State);
        Assert.False(File.Exists(Path.Combine(builder.OutputDirectory, "agents", "codex.html")));
        Assert.Equal(["all", "claude"], result.Tabs.Select(t => t.Id));
    }

    [Fact]
    public async Task No_data_removes_the_dashboard()
    {
        var builder = CreateBuilder(new FakeAdapter("codex", "Codex", []));
        await builder.BuildAsync(new AiUsageOptions());
        var result = await builder.BuildAsync(new AiUsageOptions { DisabledAgents = ["claude"] });
        Assert.False(result.HasData);
        Assert.Equal(AiUsageAgentState.Empty, result.Agents.Single(a => a.Id == "codex").State);
        Assert.False(File.Exists(result.DashboardFile));
    }

    [Fact]
    public async Task A_failing_agent_is_reported_without_stopping_the_others()
    {
        var builder = CreateBuilder(new FakeAdapter("codex", "Codex", [], fail: true));
        var result = await builder.BuildAsync(new AiUsageOptions());
        var codex = result.Agents.Single(a => a.Id == "codex");
        Assert.Equal(AiUsageAgentState.Failed, codex.State);
        Assert.Equal("skipped: boom", codex.Summary);
        Assert.True(result.HasData);
    }

    [Fact]
    public async Task Days_window_limits_the_report()
    {
        var builder = CreateBuilder();
        var all = await builder.BuildAsync(new AiUsageOptions());
        var week = await builder.BuildAsync(new AiUsageOptions { Days = 7 });
        var last = DateTimeOffset.FromUnixTimeMilliseconds(all.Combined!.Meta.LastTs!.Value).UtcDateTime.Date;
        Assert.True(week.Combined!.Meta.CountedRecords < all.Combined.Meta.CountedRecords);
        Assert.All(week.Combined.Hours.Keys, day => Assert.True(DateTime.Parse(day) >= last.AddDays(-6)));
    }

    [Fact]
    public async Task Single_file_export_embeds_every_page()
    {
        var builder = CreateBuilder(new FakeAdapter("codex", "Codex", [AgentRecord.Create(1_788_000_000_000, "gpt-5.5", 1, request: "r")]));
        var result = await builder.BuildAsync(new AiUsageOptions());
        var file = Path.Combine(_root, "export.html");

        await AiUsageReportBuilder.ExportSingleFileAsync(result, file);

        var html = await File.ReadAllTextAsync(file);
        Assert.Contains("f.srcdoc = JSON.parse(", html);
        Assert.DoesNotContain("f.src = b.dataset.href;", html);
        foreach (var tab in result.Tabs) Assert.Contains($"data-page=\"{tab.Href}\"", html);
        // Embedded pages cannot close the script element that carries them.
        Assert.Equal(html.Split("<script").Length, html.Split("</script>").Length);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to));
    }

    private sealed class FakeAdapter(string id, string label, IReadOnlyList<AgentRecord> records, bool detected = true, bool fail = false)
        : IAgentUsageAdapter
    {
        public string Id => id;
        public string Label => label;
        public string? Note => null;
        public bool Detect() => detected;
        public Task<IReadOnlyList<AgentRecord>> LoadAsync(bool rescan, CancellationToken cancellationToken = default) =>
            fail ? throw new InvalidOperationException("boom") : Task.FromResult(records);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly Lock _gate = new();
        public void Report(T value) { lock (_gate) report(value); }
    }
}
