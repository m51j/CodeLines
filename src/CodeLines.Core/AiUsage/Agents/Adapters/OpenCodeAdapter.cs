using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeLines.Core.AiUsage.Agents.Adapters;

/// <summary>
/// opencode and Kilo Code share one SQLite schema. Every assistant row of the message table carries its call's usage in
/// the JSON <c>data</c> column: tokens.input excludes cache reads; tokens.output excludes reasoning
/// (total = input + output + reasoning + cache), so OUT = output + reasoning. <c>cost</c> is what the provider billed.
/// Free models ("...:free", "-free") and the opencode Zen free tier record 0, which is a real, known cost.
/// </summary>
public sealed partial class OpenCodeAdapter(AgentEnvironment env, string id, string label, string dbPath) : IAgentUsageAdapter
{
    public static OpenCodeAdapter OpenCode(AgentEnvironment env) =>
        new(env, "opencode", "opencode", Path.Combine(env.Home, ".local", "share", "opencode", "opencode.db"));

    public static OpenCodeAdapter Kilo(AgentEnvironment env) =>
        new(env, "kilo", "Kilo Code", Path.Combine(env.Home, ".local", "share", "kilo", "kilo.db"));

    public string Id => id;
    public string Label => label;
    public string? Note => null;

    public bool Detect() => Path.Exists(dbPath);

    public Task<IReadOnlyList<AgentRecord>> LoadAsync(bool rescan, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<AgentRecord>>(() => !Path.Exists(dbPath) ? [] : SqliteSource.WithDb(dbPath, env.TempDirectory, d =>
        {
            var records = new List<AgentRecord>();
            if (!SqliteSource.HasTable(d, "message")) return records;
            var rows = SqliteSource.Query(d, """
                select m.id, m.session_id, m.time_created, m.data, s.directory
                from message m left join session s on s.id = m.session_id
                where json_extract(m.data, '$.role') = 'assistant'
                """);
            foreach (var r in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                JsonDocument document;
                try { document = JsonDocument.Parse(r.Text("data")); }
                catch (JsonException) { continue; }
                using (document)
                {
                    JsonElement? x = document.RootElement;
                    var t = x.Prop("tokens");
                    var c = t.Prop("cache");
                    var reasoning = t.Prop("reasoning").Num();
                    var cost = x.Prop("cost").Num();
                    if (t.Prop("input").Num() == 0 && t.Prop("output").Num() == 0 && reasoning == 0 && c.Prop("read").Num() == 0
                        && c.Prop("write").Num() == 0 && !(cost > 0)) continue;
                    var model = x.Prop("modelID").Truthy() ? JsCompat.Text(x.Prop("modelID")) : "unknown";
                    var created = x.Prop("time").Prop("created").Num();
                    var cwd = x.Prop("path").Prop("cwd");
                    records.Add(AgentRecord.Create((long)(created != 0 ? created : r.Num("time_created")), model,
                        t.Prop("input").Num(), c.Prop("write").Num(), c.Prop("read").Num(), t.Prop("output").Num() + reasoning,
                        reasoning, cost > 0 || IsFree(model) ? cost : null, r.Text("session_id"), r.Text("id"),
                        cwd.Truthy() ? JsCompat.Text(cwd) : r.Text("directory")));
                }
            }
            return records;
        }), cancellationToken);

    internal static bool IsFree(string model) => FreeModel().IsMatch(model);

    // \z, not $: JavaScript's $ does not match before a trailing newline.
    [GeneratedRegex(@"(:free|-free)\z", RegexOptions.IgnoreCase)]
    private static partial Regex FreeModel();
}
