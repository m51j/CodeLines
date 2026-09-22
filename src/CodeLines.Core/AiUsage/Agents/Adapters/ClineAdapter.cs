using System.Text.Json;

namespace CodeLines.Core.AiUsage.Agents.Adapters;

/// <summary>
/// Cline: ~/.cline/data/sessions/&lt;id&gt;/&lt;id&gt;.json holds the session header (cwd, model); &lt;id&gt;.messages.json holds
/// every message, and each assistant message that came from an API call has per-call <c>metrics</c>. inputTokens
/// includes the cache reads/writes (OpenAI style). The per-message metrics are used rather than the header's
/// metadata.usage: the header total covers only the last run of a resumed session.
/// </summary>
public sealed class ClineAdapter(AgentEnvironment env) : IAgentUsageAdapter
{
    private readonly string _root = Path.Combine(env.Home, ".cline", "data", "sessions");

    public string Id => "cline";
    public string Label => "Cline";
    public string? Note => null;

    public bool Detect() => Path.Exists(_root);

    public async Task<IReadOnlyList<AgentRecord>> LoadAsync(bool rescan, CancellationToken cancellationToken = default)
    {
        List<string> dirs;
        // readdir order in ccstats; sorted here so the output is deterministic on every file system.
        try { dirs = Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName).OfType<string>().Sorted(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { dirs = []; }
        var files = dirs.Select(d => Path.Combine(_root, d, $"{d}.messages.json")).Where(Path.Exists).ToList();
        var cache = new UsageFileCache<AgentRecord>(env.CacheFile(Id), "1.1", new AgentRecordConverter());
        return (await cache.LoadAsync(files, Parse, rescan, null, cancellationToken)).Records;
    }

    private static JsonDocument? ReadJson(string file)
    {
        try { return JsonDocument.Parse(File.ReadAllText(file)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static List<AgentRecord> Parse(string file, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(file)!;
        var id = Path.GetFileName(dir);
        using var headDocument = ReadJson(Path.Combine(dir, $"{id}.json"));
        using var bodyDocument = ReadJson(file);
        var records = new List<AgentRecord>();
        // `readJson(...) || {}`: a falsy header reads as an empty object.
        JsonElement? head = headDocument?.RootElement is { } h && ((JsonElement?)h).Truthy() ? h : null;
        if (bodyDocument?.RootElement.Prop("messages") is not { ValueKind: JsonValueKind.Array } messages) return records;
        foreach (var message in messages.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement? m = message;
            var x = m.Prop("metrics");
            if (!x.Truthy() || m.Prop("role").Str() != "assistant") continue;
            var cr = x.Prop("cacheReadTokens").Num();
            var cw = x.Prop("cacheWriteTokens").Num();
            if (x.Prop("inputTokens").Num() == 0 && x.Prop("outputTokens").Num() == 0) continue;
            var ts = m.Prop("ts").Num();
            if (ts == 0) ts = JsonValues.ParseTimestamp(head.Prop("started_at").Str()) ?? 0;
            var cost = x.Prop("cost");
            records.Add(AgentRecord.Create((long)ts, JsCompat.TextOr(m.Prop("modelInfo").Prop("id"), head.Prop("model")),
                x.Prop("inputTokens").Num() - cr - cw, cw, cr, x.Prop("outputTokens").Num(), 0,
                cost.IsNumber() ? cost!.Value.GetDouble() : null,
                head.Prop("session_id").Truthy() ? JsCompat.Text(head.Prop("session_id")) : id,
                $"cline:{JsCompat.Text(m.Prop("id").Truthy() ? m.Prop("id") : m.Prop("ts"))}",
                JsCompat.TextOr(head.Prop("cwd"), head.Prop("workspace_root"))));
        }
        return records;
    }
}
