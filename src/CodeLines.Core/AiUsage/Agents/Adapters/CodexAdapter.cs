using System.Text;
using System.Text.Json;

namespace CodeLines.Core.AiUsage.Agents.Adapters;

/// <summary>
/// OpenAI Codex (CLI, VS Code extension, desktop app): ~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl (+ archived_sessions/).
/// Each model call emits an event_msg token_count whose info.last_token_usage is that call's usage and
/// info.total_token_usage the running session total. input_tokens includes cached_input_tokens (OpenAI style), so
/// IN = input − cached; output_tokens includes reasoning. A token_count whose running total did not move is a re-emit.
/// Forked/resumed threads copy earlier events with their original timestamps; the request key (timestamp + running
/// total) lets the aggregator drop those copies.
/// </summary>
public sealed class CodexAdapter(AgentEnvironment env) : IAgentUsageAdapter
{
    private readonly string[] _roots =
        [Path.Combine(env.Home, ".codex", "sessions"), Path.Combine(env.Home, ".codex", "archived_sessions")];

    public string Id => "codex";
    public string Label => "Codex";
    public string? Note => null;

    public bool Detect() => _roots.Any(Path.Exists);

    public async Task<IReadOnlyList<AgentRecord>> LoadAsync(bool rescan, CancellationToken cancellationToken = default)
    {
        var files = _roots.SelectMany(r => UsageFiles.Walk(r, n => n.StartsWith("rollout-", StringComparison.Ordinal)
            && n.EndsWith(".jsonl", StringComparison.Ordinal))).Sorted();
        var cache = new UsageFileCache<AgentRecord>(env.CacheFile(Id), "1.1", new AgentRecordConverter());
        return (await cache.LoadAsync(files, Parse, rescan, null, cancellationToken)).Records;
    }

    internal static List<AgentRecord> Parse(string file, CancellationToken cancellationToken)
    {
        var records = new List<AgentRecord>();
        string session = "", cwd = "", model = "";
        double prevTotal = -1;
        try
        {
            foreach (var line in File.ReadLines(file, Encoding.UTF8))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isTok = line.Contains("\"token_count\"", StringComparison.Ordinal);
                if (!isTok && !line.Contains("\"turn_context\"", StringComparison.Ordinal)
                    && !line.Contains("\"session_meta\"", StringComparison.Ordinal)) continue;
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (document)
                {
                    JsonElement? d = document.RootElement;
                    var p = d.Prop("payload");
                    var type = d.Prop("type").Str();
                    if (type == "session_meta")
                    {
                        if (session.Length == 0) session = JsCompat.TextOr(p.Prop("id"), p.Prop("session_id"));
                        if (cwd.Length == 0 && p.Prop("cwd").Truthy()) cwd = JsCompat.Text(p.Prop("cwd"));
                    }
                    else if (type == "turn_context")
                    {
                        if (p.Prop("model").Truthy()) model = JsCompat.Text(p.Prop("model"));
                        if (p.Prop("cwd").Truthy()) cwd = JsCompat.Text(p.Prop("cwd"));
                    }
                    else if (type == "event_msg" && p.Prop("type").Str() == "token_count" && p.Prop("info").Truthy())
                    {
                        var last = p.Prop("info").Prop("last_token_usage");
                        var total = p.Prop("info").Prop("total_token_usage");
                        if (!last.Truthy() || !total.Truthy()) continue;
                        var t = total.Prop("total_tokens").Num();
                        if (t == prevTotal) continue;
                        prevTotal = t;
                        var timestamp = d.Prop("timestamp").Str();
                        if (JsonValues.ParseTimestamp(timestamp) is not { } ts) continue;
                        var cached = last.Prop("cached_input_tokens").Num();
                        records.Add(AgentRecord.Create(ts, model, last.Prop("input_tokens").Num() - cached,
                            last.Prop("cache_write_input_tokens").Num(), cached, last.Prop("output_tokens").Num(),
                            last.Prop("reasoning_output_tokens").Num(), null, session, $"codex:{timestamp}:{JsCompat.Number(t)}", cwd));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return records;
    }
}
