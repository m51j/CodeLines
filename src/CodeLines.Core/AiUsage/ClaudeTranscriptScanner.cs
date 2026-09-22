using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeLines.Core.AiUsage;

/// <summary>One assistant message with usage from a Claude Code transcript (ccstats' F tuple).</summary>
public sealed record ClaudeRecord(long Ts, string Model, long Input, long CacheWrite5m, long CacheWrite1h, long CacheRead,
    long Output, long Thinking, string Session, string Request, string Cwd, string Branch);

/// <summary>
/// Streams Claude Code transcripts (~/.claude/projects/**/*.jsonl), porting ccstats' scan.mjs:
/// only type "assistant" rows with an object usage and a real (non-synthetic) model are counted.
/// </summary>
public static class ClaudeTranscriptScanner
{
    public static string DefaultRoot(string home) => Path.Combine(home, ".claude", "projects");

    public static List<string> ListTranscripts(IEnumerable<string> roots) =>
        roots.Where(Directory.Exists).SelectMany(root => UsageFiles.Walk(root, name => name.EndsWith(".jsonl", StringComparison.Ordinal))).Sorted();

    public static List<ClaudeRecord> ScanFile(string file, CancellationToken cancellationToken = default)
    {
        var records = new List<ClaudeRecord>();
        IEnumerable<string> lines;
        try { lines = File.ReadLines(file, Encoding.UTF8); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return records; }
        try
        {
            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryParseLine(line) is { } record) records.Add(record);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return records;
    }

    public static ClaudeRecord? TryParseLine(string line)
    {
        // Cheap pre-filter before the expensive parse.
        if (line.Length < 80 || !line.Contains("\"usage\"", StringComparison.Ordinal)
            || !line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal)) return null;
        JsonDocument document;
        try { document = JsonDocument.Parse(line); }
        catch (JsonException) { return null; }
        using (document)
        {
            JsonElement? d = document.RootElement;
            if (d.Prop("type").Str() != "assistant") return null;
            var msg = d.Prop("message");
            if (!msg.IsObject()) return null;
            var u = msg.Prop("usage");
            if (!u.IsObject()) return null;
            var model = msg.Prop("model").Str();
            // "<synthetic>" rows are local placeholders (API errors, interrupts), never a billed call.
            if (model.Length == 0 || model.StartsWith('<')) return null;
            if (JsonValues.ParseTimestamp(d.Prop("timestamp").Str()) is not { } ts) return null;

            var cc = u.Prop("cache_creation");
            var cw5m = cc.Prop("ephemeral_5m_input_tokens").Long();
            var cw1h = cc.Prop("ephemeral_1h_input_tokens").Long();
            var cwTotal = u.Prop("cache_creation_input_tokens").Long();
            // A missing or inconsistent TTL breakdown: trust the total and give the rest to the 5-minute TTL.
            if (cw5m + cw1h != cwTotal)
                cw5m = cw5m + cw1h == 0 ? cwTotal : Math.Max(0, cwTotal - cw1h);

            var request = d.Prop("requestId").Str();
            if (request.Length == 0) request = msg.Prop("id").Str();
            return new ClaudeRecord(ts, ClaudePricing.NormalizeModel(model), u.Prop("input_tokens").Long(), cw5m, cw1h,
                u.Prop("cache_read_input_tokens").Long(), u.Prop("output_tokens").Long(),
                u.Prop("output_tokens_details").Prop("thinking_tokens").Long(),
                d.Prop("sessionId").Str(), request, d.Prop("cwd").Str(), d.Prop("gitBranch").Str());
        }
    }
}

/// <summary>Positional cache layout, kept small like ccstats' on-disk tuples.</summary>
public sealed class ClaudeRecordConverter : JsonConverter<ClaudeRecord>
{
    public override ClaudeRecord Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var a = doc.RootElement;
        return new(a[0].GetInt64(), a[1].GetString()!, a[2].GetInt64(), a[3].GetInt64(), a[4].GetInt64(), a[5].GetInt64(),
            a[6].GetInt64(), a[7].GetInt64(), a[8].GetString()!, a[9].GetString()!, a[10].GetString()!, a[11].GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, ClaudeRecord r, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(r.Ts); writer.WriteStringValue(r.Model);
        writer.WriteNumberValue(r.Input); writer.WriteNumberValue(r.CacheWrite5m); writer.WriteNumberValue(r.CacheWrite1h);
        writer.WriteNumberValue(r.CacheRead); writer.WriteNumberValue(r.Output); writer.WriteNumberValue(r.Thinking);
        writer.WriteStringValue(r.Session); writer.WriteStringValue(r.Request); writer.WriteStringValue(r.Cwd); writer.WriteStringValue(r.Branch);
        writer.WriteEndArray();
    }
}
