namespace CodeLines.Core.AiUsage.Agents.Adapters;

/// <summary>
/// ZCode (Z.ai): ~/.zcode/cli/db/db.sqlite, table model_usage, one row per model call.
/// input_tokens includes the cache reads and writes (total = input + output), so IN = input − CR − CW.
/// </summary>
public sealed class ZCodeAdapter(AgentEnvironment env) : IAgentUsageAdapter
{
    private readonly string _db = Path.Combine(env.Home, ".zcode", "cli", "db", "db.sqlite");

    public string Id => "zcode";
    public string Label => "ZCode";
    public string? Note => null;

    public bool Detect() => Path.Exists(_db);

    public Task<IReadOnlyList<AgentRecord>> LoadAsync(bool rescan, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<AgentRecord>>(() => !Path.Exists(_db) ? [] : SqliteSource.WithDb(_db, env.TempDirectory, db =>
        {
            if (!SqliteSource.HasTable(db, "model_usage")) return new List<AgentRecord>();
            return SqliteSource.Query(db, """
                select u.id, u.session_id, u.model_id, u.started_at, u.input_tokens, u.output_tokens,
                       u.reasoning_tokens, u.cache_creation_input_tokens cw, u.cache_read_input_tokens cr,
                       s.directory
                from model_usage u left join session s on s.id = u.session_id
                where u.input_tokens > 0 or u.output_tokens > 0
                """).Select(r => AgentRecord.Create((long)r.Num("started_at"), r.Text("model_id"),
                    r.Num("input_tokens") - r.Num("cr") - r.Num("cw"), r.Num("cw"), r.Num("cr"), r.Num("output_tokens"),
                    r.Num("reasoning_tokens"), null, r.Text("session_id"), r.Text("id"), r.Text("directory"))).ToList();
        }), cancellationToken);
}
