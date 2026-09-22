using System.Text.Json;

namespace CodeLines.Core.AiUsage.Agents.Adapters;

/// <summary>
/// LM Studio: ~/.lmstudio/conversations/*.conversation.json; every generation step of an assistant message carries
/// genInfo.stats with the prompt and predicted token counts. Models run locally, so the cost is a known $0.
/// </summary>
public sealed class LmStudioAdapter(AgentEnvironment env) : IAgentUsageAdapter
{
    private const string Suffix = ".conversation.json";
    private readonly string _root = Path.Combine(env.Home, ".lmstudio", "conversations");

    public string Id => "lmstudio";
    public string Label => "LM Studio";
    public string? Note => null;

    public bool Detect() => Path.Exists(_root);

    public async Task<IReadOnlyList<AgentRecord>> LoadAsync(bool rescan, CancellationToken cancellationToken = default)
    {
        List<string> files;
        try { files = Directory.EnumerateFiles(_root).Where(f => f.EndsWith(Suffix, StringComparison.Ordinal)).Sorted(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { files = []; }
        var cache = new UsageFileCache<AgentRecord>(env.CacheFile(Id), "1.1", new AgentRecordConverter());
        return (await cache.LoadAsync(files, Parse, rescan, null, cancellationToken)).Records;
    }

    internal static List<AgentRecord> Parse(string file, CancellationToken cancellationToken)
    {
        var records = new List<AgentRecord>();
        JsonDocument document;
        try { document = JsonDocument.Parse(File.ReadAllText(file)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return records; }
        using (document)
        {
            JsonElement? j = document.RootElement;
            var name = Path.GetFileName(file);
            var session = name[..^Suffix.Length];
            var fallback = j.Prop("assistantLastMessagedAt").Num();
            if (fallback == 0) fallback = j.Prop("userLastMessagedAt").Num();
            if (fallback == 0) fallback = j.Prop("createdAt").Num();
            if (j.Prop("messages") is not { ValueKind: JsonValueKind.Array } messages) return records;
            foreach (var message in messages.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                JsonElement? m = message;
                // Only the branch the user kept counts; regenerated versions were discarded.
                var versions = m.Prop("versions");
                var selected = m.Prop("currentlySelected");
                var v = JsCompat.Index(versions, selected is null or { ValueKind: JsonValueKind.Null } ? Zero : selected);
                if (v is null or { ValueKind: JsonValueKind.Null }) v = JsCompat.Index(versions, Zero);
                if (!v.Truthy() || v.Prop("role").Str() != "assistant") continue;
                if (v.Prop("steps") is not { ValueKind: JsonValueKind.Array } steps) continue;
                foreach (var step in steps.EnumerateArray())
                {
                    JsonElement? s = step;
                    var genInfo = s.Prop("genInfo");
                    var st = genInfo.Prop("stats");
                    if (!st.Truthy()) continue;
                    var stepId = s.Prop("stepIdentifier").Truthy() ? JsCompat.Text(s.Prop("stepIdentifier")) : "";
                    // Number('') is 0 and NaN is falsy: both fall back.
                    var ts = JsCompat.ToNumber(stepId.Split('-')[0]);
                    if (ts == 0 || !double.IsFinite(ts)) ts = fallback;
                    records.Add(AgentRecord.Create((long)ts,
                        JsCompat.TextOr(genInfo.Prop("identifier"), genInfo.Prop("indexedModelIdentifier"), j.Prop("lastUsedModel").Prop("identifier")),
                        st.Prop("promptTokensCount").Num(), 0, 0, st.Prop("predictedTokensCount").Num(), 0, 0, session,
                        $"lmstudio:{(stepId.Length > 0 ? stepId : JsCompat.Number(ts))}"));
                }
            }
        }
        return records;
    }

    private static readonly JsonElement Zero = JsonDocument.Parse("0").RootElement;
}
