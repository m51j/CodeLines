using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeLines.Core.AiUsage.Agents.Adapters;

/// <summary>
/// GitHub Copilot Chat (VS Code). Each chat is a patch log at
/// %APPDATA%/Code/User/workspaceStorage/&lt;ws&gt;/chatSessions/&lt;id&gt;.jsonl:
/// kind 0 is the initial state {requests:[...], ...}, kind 1 sets the value at path k, kind 2 appends v (an array) to the
/// array at path k (or splices it in at index i). Replaying it gives requests[] with timestamp, modelId and, once
/// finished, result.metadata.{promptTokens, outputTokens, toolCallRounds}.
/// LOWER BOUND: Copilot keeps the prompt size of the last tool-call round only, so input is undercounted.
/// completionTokens accumulates over the rounds and is used for output.
/// </summary>
public sealed class CopilotAdapter(AgentEnvironment env) : IAgentUsageAdapter
{
    private readonly string _workspaces = Path.Combine(env.AppData, "Code", "User", "workspaceStorage");

    public string Id => "copilot";
    public string Label => "Copilot Chat";
    public string? Note => "Copilot saves only the last tool-call round’s prompt size for each request, so input tokens here are a lower bound. Model calls count every tool-call round.";

    public bool Detect() => Path.Exists(_workspaces);

    public async Task<IReadOnlyList<AgentRecord>> LoadAsync(bool rescan, CancellationToken cancellationToken = default)
    {
        var files = new List<string>();
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(_workspaces).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { dirs = []; }
        foreach (var d in dirs)
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(Path.Combine(d, "chatSessions"))
                    .Where(n => n.EndsWith(".jsonl", StringComparison.Ordinal)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var cache = new UsageFileCache<AgentRecord>(env.CacheFile(Id), "1.1", new AgentRecordConverter());
        return (await cache.LoadAsync(files.Sorted(), Parse, rescan, null, cancellationToken)).Records;
    }

    internal static List<AgentRecord> Parse(string file, CancellationToken cancellationToken)
    {
        var records = new List<AgentRecord>();
        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return records; }

        JsonNode state = new JsonObject();
        // Completion counts arrive as a stream of set-patches; keep the largest.
        var completion = new Dictionary<double, double>();
        foreach (var line in text.Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (JsonException) { continue; }
            if (node is not JsonObject d) continue;
            var kind = d["kind"] is JsonValue kv && kv.GetValueKind() == JsonValueKind.Number ? kv.GetValue<double>() : double.NaN;
            var v = d["v"];
            d.Remove("v"); // detach so the value can be grafted into the state
            if (kind == 0) state = v is JsonObject or JsonArray ? v : new JsonObject();
            else if (kind == 1 && d["k"] is JsonArray { Count: > 0 } k1)
            {
                if (k1.Count == 3 && IsString(k1[0], "requests") && IsString(k1[2], "completionTokens") && AsNumber(k1[1]) is { } index)
                    completion[index] = Math.Max(completion.GetValueOrDefault(index), Num(v));
                // Response bodies are large and never needed.
                if (IsString(k1[^1], "response")) continue;
                SetPath(state, k1, v);
            }
            else if (kind == 2 && d["k"] is JsonArray { Count: > 0 } k2)
            {
                if (GetPath(state, k2) is not JsonArray arr) SetPath(state, k2, arr = []);
                List<JsonNode?> items;
                if (v is JsonArray list)
                {
                    items = [.. list];
                    list.Clear();
                }
                else items = [v];
                if (d["i"] is JsonValue iv && iv.GetValueKind() == JsonValueKind.Number)
                {
                    // arr.splice(i, arr.length - i, ...items)
                    var i = iv.GetValue<double>();
                    var len = arr.Count;
                    var start = (int)(i < 0 ? Math.Max(len + Math.Truncate(i), 0) : Math.Min(Math.Truncate(i), len));
                    var deleteCount = (int)Math.Clamp(Math.Truncate(len - i), 0, len - start);
                    for (var n = 0; n < deleteCount; n++) arr.RemoveAt(start);
                    for (var n = 0; n < items.Count; n++) arr.Insert(start + n, items[n]);
                }
                else foreach (var item in items) arr.Add(item);
            }
        }

        var cwd = WorkspaceFolder(Path.GetDirectoryName(Path.GetDirectoryName(file))!);
        JsonElement? s = JsonSerializer.SerializeToElement(state);
        var session = s.Prop("sessionId").Truthy() ? JsCompat.Text(s.Prop("sessionId")) : Path.GetFileNameWithoutExtension(file);
        if (s.Prop("requests") is not { ValueKind: JsonValueKind.Array } requests) return records;
        var ri = 0;
        foreach (var request in requests.EnumerateArray())
        {
            var i = ri++;
            if (request.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) continue;
            JsonElement? r = request;
            var md = r.Prop("result").Prop("metadata");
            var input = md.Prop("promptTokens").Num();
            if (input == 0) input = r.Prop("promptTokens").Num();
            var output = Math.Max(completion.GetValueOrDefault(i), Math.Max(r.Prop("completionTokens").Num(), md.Prop("outputTokens").Num()));
            if (input == 0 && output == 0) continue;
            var rounds = md.Prop("toolCallRounds") is { ValueKind: JsonValueKind.Array } tr ? tr.GetArrayLength() : 0;
            var model = JsCompat.TextOr(r.Prop("modelId"), md.Prop("resolvedModel"),
                s.Prop("inputState").Prop("selectedModel").Prop("identifier"));
            if (model.Length == 0) model = "unknown";
            if (model.StartsWith("copilot/", StringComparison.Ordinal)) model = model["copilot/".Length..];
            var ts = r.Prop("timestamp").Num();
            if (ts == 0) ts = s.Prop("creationDate").Num();
            records.Add(AgentRecord.Create((long)ts, model, input, 0, 0, output, 0, null, session,
                r.Prop("requestId").Truthy() ? JsCompat.Text(r.Prop("requestId")) : $"{session}:{i}", cwd, Math.Max(1, rounds)));
        }
        return records;
    }

    private static string WorkspaceFolder(string workspaceDir)
    {
        try
        {
            using var w = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspaceDir, "workspace.json")));
            JsonElement? root = w.RootElement;
            var uri = root.Prop("folder").Truthy() ? root.Prop("folder") : root.Prop("workspace");
            if (!uri.Truthy()) return "";
            // A non-string uri makes ccstats' uri.startsWith throw, which its catch turns into ''.
            if (uri is not { ValueKind: JsonValueKind.String } u) return "";
            var text = u.GetString()!;
            if (!text.StartsWith("file:///", StringComparison.Ordinal)) return text;
            return JsCompat.DecodeUriComponent(text[8..])?.Replace('/', '\\') ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return ""; }
    }

    private static bool IsString(JsonNode? node, string value) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == value;

    private static double? AsNumber(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    private static double Num(JsonNode? node) => AsNumber(node) is { } d && double.IsFinite(d) ? d : 0;

    // ---- JavaScript property access on the replayed state ----

    private static void SetPath(JsonNode root, JsonArray k, JsonNode? v)
    {
        var o = root;
        for (var i = 0; i < k.Count - 1; i++)
        {
            var next = Get(o, k[i]);
            if (next is not (JsonObject or JsonArray))
            {
                next = AsNumber(k[i + 1]) is not null ? new JsonArray() : new JsonObject();
                Set(o, k[i], next);
            }
            o = next;
        }
        Set(o, k[^1], v);
    }

    private static JsonNode? GetPath(JsonNode root, JsonArray k)
    {
        JsonNode? o = root;
        foreach (var p in k)
        {
            if (o is null) return null;
            o = Get(o, p);
        }
        return o;
    }

    private static JsonNode? Get(JsonNode o, JsonNode? key) => o switch
    {
        JsonObject obj => obj.TryGetPropertyValue(KeyText(key), out var value) ? value : null,
        JsonArray arr => ArrayIndex(key) is { } i && i < arr.Count ? arr[i] : null,
        _ => null
    };

    private static void Set(JsonNode o, JsonNode? key, JsonNode? value)
    {
        if (o is JsonObject obj) obj[KeyText(key)] = value;
        else if (o is JsonArray arr && ArrayIndex(key) is { } i)
        {
            if (i < arr.Count) arr[i] = value;
            else
            {
                // Writing past the end leaves holes, which forEach skips; nulls are skipped the same way below.
                while (arr.Count < i) arr.Add(null);
                arr.Add(value);
            }
        }
        // A non-index key on an array (or any key on a primitive) never reaches the fields read here.
    }

    /// <summary>The property name JavaScript would use for <paramref name="key"/>.</summary>
    private static string KeyText(JsonNode? key) => key switch
    {
        null => "null",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => JsCompat.Text(JsonSerializer.SerializeToElement(key))
    };

    private static int? ArrayIndex(JsonNode? key)
    {
        double d;
        if (AsNumber(key) is { } n) d = n;
        else if (key is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is var s
                 && JsCompat.Number(JsCompat.ToNumber(s)) == s) d = JsCompat.ToNumber(s);
        else return null;
        // Guard against a stray huge index materialising millions of holes.
        return d >= 0 && d == Math.Floor(d) && d < 1 << 24 ? (int)d : null;
    }
}
