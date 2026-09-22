namespace CodeLines.Core.AiUsage.Agents.Adapters;

/// <summary>
/// Hermes Agent (Nous): %LOCALAPPDATA%/hermes/state.db keeps usage per session only, so each session becomes one record
/// at its start time that stands for api_call_count model calls. input_tokens excludes the cache reads.
/// </summary>
public sealed class HermesAdapter(AgentEnvironment env) : IAgentUsageAdapter
{
    private readonly string _db = Path.Combine(env.LocalAppData, "hermes", "state.db");

    public string Id => "hermes";
    public string Label => "Hermes";
    public string? Note => "Hermes stores usage per session, not per call, so all of a session’s tokens land on the hour it started.";

    public bool Detect() => Path.Exists(_db);

    public Task<IReadOnlyList<AgentRecord>> LoadAsync(bool rescan, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<AgentRecord>>(() => !Path.Exists(_db) ? [] : SqliteSource.WithDb(_db, env.TempDirectory, db =>
        {
            if (!SqliteSource.HasTable(db, "sessions")) return new List<AgentRecord>();
            return SqliteSource.Query(db, "select * from sessions")
                .Where(r => r.Num("input_tokens") != 0 || r.Num("output_tokens") != 0)
                .Select(r =>
                {
                    // estimated_cost_usd is 0 for subscription (oauth) billing: unknown, not free.
                    double? cost = !r.IsNull("actual_cost_usd") ? r.Num("actual_cost_usd")
                        : r.Num("estimated_cost_usd") > 0 ? r.Num("estimated_cost_usd") : null;
                    return AgentRecord.Create((long)JsCompat.Round(r.Num("started_at") * 1000), r.Text("model"),
                        r.Num("input_tokens"), r.Num("cache_write_tokens"), r.Num("cache_read_tokens"), r.Num("output_tokens"),
                        r.Num("reasoning_tokens"), cost, r.Text("id"), $"hermes:{JsCompat.Text(r.GetValueOrDefault("id"))}",
                        r.Text("cwd"), (int)Math.Max(1, r.Num("api_call_count")));
                }).ToList();
        }), cancellationToken);
}
