using System.Diagnostics;
using System.Text;
using CodeLines.Core.AiUsage.Agents;
using CodeLines.Core.AiUsage.Agents.Adapters;

namespace CodeLines.Core.AiUsage;

/// <summary>
/// Every AI agent on this machine, as ccstats' all.mjs builds it: Claude Code's own report, one page per other agent,
/// a combined page and a dashboard with a tab for each. Pages are written to <see cref="OutputDirectory"/>.
/// </summary>
public sealed class AiUsageReportBuilder : IAiUsageReportBuilder
{
    public const string ClaudeId = "claude";
    private const string ClaudeCacheVersion = "2";
    private readonly AgentEnvironment _environment;
    private readonly IReadOnlyList<IAgentUsageAdapter> _adapters;
    private readonly TimeZoneInfo? _localZone;

    public AiUsageReportBuilder(string? dataDirectory = null, AgentEnvironment? environment = null,
        IReadOnlyList<IAgentUsageAdapter>? adapters = null, TimeZoneInfo? localZone = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeLines", "ai-usage");
        _environment = environment ?? AgentEnvironment.ForCurrentUser(Path.Combine(DataDirectory, "cache"));
        _adapters = adapters ?? AgentAdapters.Create(_environment);
        _localZone = localZone;
    }

    public string DataDirectory { get; }
    public string OutputDirectory => Path.Combine(DataDirectory, "report");

    public IReadOnlyList<(string Id, string Label)> KnownAgents =>
        [(ClaudeId, "Claude Code"), .. _adapters.Select(a => (a.Id, a.Label))];

    public async Task<AiUsageResult> BuildAsync(AiUsageOptions options, IProgress<AiUsageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clock = new UsageClock(options.UseUtc, _localZone);
        var disabled = new HashSet<string>(options.DisabledAgents, StringComparer.OrdinalIgnoreCase);
        var agentsDirectory = Path.Combine(OutputDirectory, "agents");
        Directory.CreateDirectory(agentsDirectory);

        var sources = new List<AgentSource>();
        var tabs = new List<(string Id, string Label, string Href)>();
        var statuses = new List<AiUsageAgentStatus>();

        // Claude Code keeps its own page, exactly as ccstats' `npm start` renders it.
        var roots = new[] { ClaudeTranscriptScanner.DefaultRoot(_environment.Home) }.Concat(options.ExtraClaudeRoots)
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (disabled.Contains(ClaudeId)) statuses.Add(new(ClaudeId, "Claude Code", AiUsageAgentState.Disabled));
        else if (roots.Count == 0) statuses.Add(new(ClaudeId, "Claude Code", AiUsageAgentState.NotFound));
        else
        {
            var watch = Stopwatch.StartNew();
            progress?.Report(new(ClaudeId, "Scanning Claude Code transcripts"));
            var files = ClaudeTranscriptScanner.ListTranscripts(roots);
            var cache = new UsageFileCache<ClaudeRecord>(Path.Combine(_environment.CacheDirectory, "claude-index.json"),
                ClaudeCacheVersion, new ClaudeRecordConverter());
            var (records, _) = await cache.LoadAsync(files, ClaudeTranscriptScanner.ScanFile, options.Rescan,
                new Progress<(int Done, int Total)>(p => progress?.Report(new(ClaudeId, "Reading Claude Code transcripts", p.Done, p.Total))),
                cancellationToken);
            var (since, until) = clock.Window(options, records.Count > 0 ? records.Max(r => r.Ts) : 0);
            var payload = await Task.Run(() => ClaudeUsageAggregator.Aggregate(records, clock, options.Deduplicate, since, until), cancellationToken);
            await WriteAsync(Path.Combine(OutputDirectory, "report.html"), AiUsageTemplates.RenderClaude(AiUsagePayloadWriter.ToJson(payload)), cancellationToken);
            sources.Add(new(ClaudeId, "Claude Code", null, AgentUsageAggregator.FromClaudeRecords(records)));
            tabs.Add((ClaudeId, "Claude Code", "report.html"));
            statuses.Add(new(ClaudeId, "Claude Code", AiUsageAgentState.Loaded, records.Count, Elapsed: watch.Elapsed));
        }

        foreach (var adapter in _adapters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (disabled.Contains(adapter.Id)) { statuses.Add(new(adapter.Id, adapter.Label, AiUsageAgentState.Disabled)); continue; }
            if (!adapter.Detect()) { statuses.Add(new(adapter.Id, adapter.Label, AiUsageAgentState.NotFound)); continue; }
            var watch = Stopwatch.StartNew();
            progress?.Report(new(adapter.Id, $"Reading {adapter.Label}"));
            try
            {
                var records = await Task.Run(() => adapter.LoadAsync(options.Rescan, cancellationToken), cancellationToken);
                if (records.Count == 0) { statuses.Add(new(adapter.Id, adapter.Label, AiUsageAgentState.Empty, Elapsed: watch.Elapsed)); continue; }
                var source = new AgentSource(adapter.Id, adapter.Label, adapter.Note, records);
                sources.Add(source);
                var (since, until) = clock.Window(options, records.Max(r => r.Ts));
                var payload = AgentUsageAggregator.Aggregate([source], adapter.Label, clock, since, until);
                await WriteAsync(Path.Combine(agentsDirectory, adapter.Id + ".html"),
                    AiUsageTemplates.RenderAgent(adapter.Label, AiUsagePayloadWriter.ToJson(payload)), cancellationToken);
                tabs.Add((adapter.Id, adapter.Label, $"agents/{adapter.Id}.html"));
                statuses.Add(new(adapter.Id, adapter.Label, AiUsageAgentState.Loaded, records.Count, Elapsed: watch.Elapsed));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                statuses.Add(new(adapter.Id, adapter.Label, AiUsageAgentState.Failed, Error: ex.Message, Elapsed: watch.Elapsed));
            }
        }

        var generatedAt = DateTimeOffset.UtcNow;
        var dashboard = Path.Combine(OutputDirectory, "dashboard.html");
        if (sources.Count == 0)
        {
            if (File.Exists(dashboard)) File.Delete(dashboard);
            return new AiUsageResult { OutputDirectory = OutputDirectory, DashboardFile = dashboard, GeneratedAt = generatedAt, Tabs = [], Agents = statuses };
        }

        progress?.Report(new("all", "Combining all agents"));
        var lastAll = sources.Max(s => s.Records.Count > 0 ? s.Records.Max(r => r.Ts) : 0);
        var (allSince, allUntil) = clock.Window(options, lastAll);
        var all = await Task.Run(() => AgentUsageAggregator.Aggregate(sources, "All agents", clock, allSince, allUntil), cancellationToken);
        await WriteAsync(Path.Combine(agentsDirectory, "all.html"), AiUsageTemplates.RenderAgent("All agents", AiUsagePayloadWriter.ToJson(all)), cancellationToken);

        // Tab badges come from the combined payload, so every agent is measured on the same basis.
        var perAgent = all.TotalsByAgent();
        List<AiUsageTab> dashTabs =
        [
            new("all", "All agents", "agents/all.html", AgentVector.RealTokens(all.Totals())),
            .. tabs.Select(t => new AiUsageTab(t.Id, t.Label, t.Href,
                perAgent.TryGetValue(t.Id, out var v) ? AgentVector.RealTokens(v) : 0))
        ];
        await WriteAsync(dashboard, AiUsageTemplates.RenderDashboard(dashTabs, generatedAt, _localZone), cancellationToken);
        // Pages of agents that are gone or turned off would otherwise linger next to the new report.
        var current = dashTabs.Select(t => Path.GetFullPath(Path.Combine(OutputDirectory, t.Href))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var page in Directory.EnumerateFiles(agentsDirectory, "*.html"))
            if (!current.Contains(Path.GetFullPath(page))) File.Delete(page);
        return new AiUsageResult
        {
            OutputDirectory = OutputDirectory, DashboardFile = dashboard, GeneratedAt = generatedAt,
            Tabs = dashTabs, Agents = statuses, Combined = all
        };
    }

    /// <summary>Writes the dashboard and every page it links to into one shareable HTML file.</summary>
    public static async Task ExportSingleFileAsync(AiUsageResult result, string path, CancellationToken cancellationToken = default)
    {
        var pages = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tab in result.Tabs)
            pages[tab.Href] = await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, tab.Href.Replace('/', Path.DirectorySeparatorChar)), cancellationToken);
        var html = AiUsageTemplates.InlineDashboard(await File.ReadAllTextAsync(result.DashboardFile, cancellationToken), pages);
        await WriteAsync(path, html, cancellationToken);
    }

    private static async Task WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), cancellationToken);
        File.Move(temp, path, true);
    }
}
