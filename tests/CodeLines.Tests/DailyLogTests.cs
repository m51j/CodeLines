using CodeLines.Core.AiUsage;
using CodeLines.Core.AiUsage.Agents;
using CodeLines.Core.Models;
using CodeLines.Core.Services;

namespace CodeLines.Tests;

public sealed class DailyLogTests : IDisposable
{
    private static readonly DateTimeOffset Day1 = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = Day1.AddDays(1);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeLinesDailyLogTests", Guid.NewGuid().ToString("N"));
    private readonly ProjectDefinition _alpha = new() { Name = "Alpha", RootPath = @"D:\Work\alpha" };
    private readonly ProjectDefinition _beta = new() { Name = "Beta", RootPath = @"D:\Work\beta" };

    [Fact]
    public void A_later_scan_on_the_same_day_replaces_the_first()
    {
        var log = new DailyLogFile();
        DailyLogRecorder.RecordLines(log, [Metrics(_alpha, 100), Metrics(_beta, 50)], CountingScope.SourceOnly, "o200k_base", Day1);
        DailyLogRecorder.RecordLines(log, [Metrics(_alpha, 120), Metrics(_beta, 55)], CountingScope.SourceOnly, "o200k_base", Day1.AddHours(5));

        var day = Assert.Single(log.Days);
        Assert.Equal("2026-09-21", day.Date);
        Assert.Equal(2, day.Lines.Count);
        Assert.Equal(120, day.Lines.Single(x => x.ProjectId == _alpha.Id).PhysicalLines);
        Assert.Equal(55, day.Lines.Single(x => x.ProjectId == _beta.Id).PhysicalLines);
        Assert.Equal("C#", Assert.Single(day.Lines[0].Languages).Language);
    }

    [Fact]
    public void Scanning_one_project_keeps_the_others_of_the_day_and_a_new_day_starts_a_new_record()
    {
        var log = new DailyLogFile();
        DailyLogRecorder.RecordLines(log, [Metrics(_alpha, 100), Metrics(_beta, 50)], CountingScope.SourceOnly, "o200k_base", Day1);
        DailyLogRecorder.RecordLines(log, [Metrics(_alpha, 110)], CountingScope.SourceOnly, "o200k_base", Day1.AddHours(1));
        DailyLogRecorder.RecordLines(log, [Metrics(_alpha, 130)], CountingScope.SourceOnly, "o200k_base", Day2);

        Assert.Equal(["2026-09-21", "2026-09-22"], log.Days.Select(d => d.Date));
        Assert.Equal([110L, 50L], log.Days[0].Lines.OrderBy(x => x.Name).Select(x => x.PhysicalLines));
        Assert.Single(log.Days[1].Lines);
    }

    [Fact]
    public void Timeline_carries_projects_that_were_not_scanned_and_computes_deltas()
    {
        var log = new DailyLogFile();
        DailyLogRecorder.RecordLines(log, [Metrics(_alpha, 100), Metrics(_beta, 50)], CountingScope.SourceOnly, "o200k_base", Day1);
        DailyLogRecorder.RecordLines(log, [Metrics(_alpha, 130)], CountingScope.SourceOnly, "o200k_base", Day2);

        var rows = DailyLogComparer.Timeline(log);

        Assert.Equal(2, rows.Count);
        Assert.Equal(150, rows[0].PhysicalLines);
        Assert.Null(rows[0].DeltaLines);
        Assert.Equal(180, rows[1].PhysicalLines);
        Assert.Equal(30, rows[1].DeltaLines);
        Assert.Equal("+30", rows[1].DeltaLinesText);
        Assert.Equal(1, rows[1].CarriedProjects);
        Assert.Contains("1 of 2 projects from earlier days", rows[1].Notes);

        var alphaOnly = DailyLogComparer.Timeline(log, [_alpha.Id]);
        Assert.Equal([100L, 130L], alphaOnly.Select(r => r.PhysicalLines!.Value));
        Assert.All(alphaOnly, r => Assert.Equal(0, r.CarriedProjects));
    }

    [Fact]
    public void Compare_reports_deltas_languages_and_a_scope_warning()
    {
        var log = new DailyLogFile();
        DailyLogRecorder.RecordLines(log, [Metrics(_alpha, 100)], CountingScope.SourceOnly, "o200k_base", Day1);
        DailyLogRecorder.RecordLines(log, [Metrics(_alpha, 150)], CountingScope.SourceOnly, "o200k_base", Day2);

        var same = DailyLogComparer.Compare(log, "2026-09-21", "2026-09-22");
        var lines = same.Metrics.Single(m => m.Metric == "Physical lines");
        Assert.Equal(50, lines.Delta);
        Assert.Equal("+50.0%", lines.DeltaPercentText);
        Assert.Equal(50, Assert.Single(same.Languages).DeltaLines);
        Assert.Equal("", same.Warning);

        DailyLogRecorder.RecordLines(log, [Metrics(_alpha, 200)], CountingScope.AllText, "o200k_base", Day2.AddHours(1));
        var changed = DailyLogComparer.Compare(log, "2026-09-21", "2026-09-22");
        Assert.Contains("not directly comparable", changed.Warning);
        Assert.True(DailyLogComparer.Timeline(log)[1].ScopeChanged);
    }

    [Fact]
    public void AI_usage_fills_every_day_with_totals_agents_and_projects()
    {
        var log = new DailyLogFile();
        var payload = Payload(
            AgentRecord.Create(Day1.ToUnixTimeMilliseconds(), "gpt-5.5", 1000, output: 500, session: "s1", request: "r1", cwd: @"D:\Work\alpha\src"),
            AgentRecord.Create(Day2.ToUnixTimeMilliseconds(), "gpt-5.5", 2000, output: 1000, session: "s2", request: "r2", cwd: @"D:\Other"));

        DailyLogRecorder.RecordAi(log, payload, Roots(), Day2);

        Assert.Equal(["2026-09-21", "2026-09-22"], log.Days.Select(d => d.Date));
        var first = log.Days[0].Ai!;
        Assert.Equal(1500, first.Total.Tokens);
        Assert.Equal(1500, first.ByAgent["codex"].Tokens);
        Assert.Equal("utc", first.Tz);
        var project = Assert.Single(first.Projects);
        Assert.Equal(_alpha.Id, project.ProjectId);
        Assert.Equal(1, project.Sessions);
        Assert.Empty(log.Days[1].Ai!.Projects);

        var alpha = DailyLogComparer.Timeline(log, [_alpha.Id], allAgents: false);
        Assert.Equal("2026-09-21", Assert.Single(alpha).Date);
        Assert.Null(alpha[0].Lines);
        Assert.Equal("Not scanned yet", alpha[0].Notes);
    }

    [Fact]
    public void Past_AI_days_never_shrink_but_today_is_replaced()
    {
        var log = new DailyLogFile();
        var full = Payload(
            AgentRecord.Create(Day1.ToUnixTimeMilliseconds(), "gpt-5.5", 1000, session: "s1", request: "r1"),
            AgentRecord.Create(Day2.ToUnixTimeMilliseconds(), "gpt-5.5", 1000, session: "s2", request: "r2"));
        DailyLogRecorder.RecordAi(log, full, Roots(), Day2);

        // The old agent logs were deleted, and today's usage went down after an agent was turned off.
        var smaller = Payload(
            AgentRecord.Create(Day1.ToUnixTimeMilliseconds(), "gpt-5.5", 10, session: "s1", request: "r1"),
            AgentRecord.Create(Day2.ToUnixTimeMilliseconds(), "gpt-5.5", 10, session: "s2", request: "r2"));
        DailyLogRecorder.RecordAi(log, smaller, Roots(), Day2);

        Assert.Equal(1000, log.Days[0].Ai!.Total.Tokens);
        Assert.Equal(10, log.Days[1].Ai!.Total.Tokens);
    }

    [Fact]
    public async Task Repository_round_trips_and_backs_up_a_corrupt_file()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "daily-log.json");
        var repository = new JsonDailyLogRepository(path);
        var log = new DailyLogFile();
        DailyLogRecorder.RecordLines(log, [Metrics(_alpha, 100)], CountingScope.AllText, "cl100k_base", Day1);
        DailyLogRecorder.RecordAi(log, Payload(AgentRecord.Create(Day1.ToUnixTimeMilliseconds(), "gpt-5.5", 1000, session: "s1", request: "r1")), Roots(), Day1);

        await repository.SaveAsync(log);
        var loaded = await repository.LoadAsync();

        var entry = Assert.Single(Assert.Single(loaded.Days).Lines);
        Assert.Equal(CountingScope.AllText, entry.Scope);
        Assert.Equal(100, entry.PhysicalLines);
        Assert.Equal(1000, loaded.Days[0].Ai!.Total.Tokens);

        await File.WriteAllTextAsync(path, "{ not json");
        Assert.Empty((await repository.LoadAsync()).Days);
        Assert.Single(Directory.GetFiles(_root, "daily-log.json.invalid-*"));
    }

    private Dictionary<Guid, string> Roots() => new() { [_alpha.Id] = _alpha.RootPath, [_beta.Id] = _beta.RootPath };

    private static AgentUsagePayload Payload(params AgentRecord[] records) =>
        AgentUsageAggregator.Aggregate([new("codex", "Codex", null, records)], "All agents", new UsageClock(true));

    private static ProjectMetrics Metrics(ProjectDefinition project, long lines)
    {
        var file = new FileMetrics(project.Name, "Program.cs", ".cs", "C#", FileCategory.SourceCode, lines * 10, lines * 3,
            new LineMetrics(lines, 0, lines, 0, 0, 0));
        return ProjectMetrics.Create(project, [file], [], TimeSpan.Zero);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
