using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CodeLines.Core.AiUsage.Agents;

namespace CodeLines.Core.AiUsage;

/// <summary>
/// Serializes payloads in the exact shape ccstats' pages parse. The default encoder escapes &lt;, &gt; and &amp;,
/// so the JSON is safe to embed in a &lt;script&gt; element (ccstats replaces &lt; with < for the same reason).
/// </summary>
public static class AiUsagePayloadWriter
{
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.Default };

    public static string ToJson(ClaudeUsagePayload payload) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteStartObject("meta");
        var m = payload.Meta;
        w.WriteNumber("generatedAt", m.GeneratedAt);
        w.WriteString("tz", m.Tz);
        w.WriteString("tzLabel", m.TzLabel);
        w.WriteBoolean("dedupe", m.Dedupe);
        w.WriteNumber("rawRecords", m.RawRecords);
        w.WriteNumber("countedRecords", m.CountedRecords);
        w.WriteNumber("duplicateRecords", m.DuplicateRecords);
        w.WriteNumber("partialRecords", m.PartialRecords);
        w.WriteNumber("inflation", m.Inflation);
        WriteNullable(w, "firstTs", m.FirstTs);
        WriteNullable(w, "lastTs", m.LastTs);
        w.WriteNumber("sessions", m.Sessions);
        w.WriteStartArray("unknownModels");
        foreach (var model in m.UnknownModels) w.WriteStringValue(model);
        w.WriteEndArray();
        w.WriteEndObject();

        w.WriteStartObject("rates");
        foreach (var (model, (r, label)) in payload.Rates)
        {
            w.WriteStartObject(model);
            w.WriteNumber("input", r.Input);
            w.WriteNumber("output", r.Output);
            w.WriteNumber("cacheRead", r.CacheRead);
            w.WriteNumber("write5m", r.Write5m);
            w.WriteNumber("write1h", r.Write1h);
            w.WriteBoolean("known", r.Known);
            w.WriteString("label", label);
            w.WriteEndObject();
        }
        w.WriteEndObject();

        w.WriteStartArray("models");
        foreach (var model in payload.Models) w.WriteStringValue(model);
        w.WriteEndArray();

        WriteHours(w, payload.Hours, (writer, v) => { foreach (var x in v) writer.WriteNumberValue(x); });

        w.WriteStartArray("sessions");
        foreach (var s in payload.Sessions)
        {
            w.WriteStartArray();
            w.WriteStringValue(s.Id);
            w.WriteNumberValue(s.Start); w.WriteNumberValue(s.End); w.WriteNumberValue(s.Requests);
            w.WriteNumberValue(s.Input); w.WriteNumberValue(s.CacheWrite5m); w.WriteNumberValue(s.CacheWrite1h);
            w.WriteNumberValue(s.CacheRead); w.WriteNumberValue(s.Output);
            w.WriteStringValue(s.Cwd); w.WriteStringValue(s.Branch); w.WriteStringValue(s.TopModel);
            w.WriteEndArray();
        }
        w.WriteEndArray();

        w.WriteStartObject("appBasis");
        foreach (var (model, v) in payload.AppBasis)
        {
            w.WriteStartArray(model);
            foreach (var x in v) w.WriteNumberValue(x);
            w.WriteEndArray();
        }
        w.WriteEndObject();
        w.WriteEndObject();
    });

    public static string ToJson(AgentUsagePayload payload) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteStartObject("meta");
        var m = payload.Meta;
        w.WriteString("title", m.Title);
        w.WriteNumber("generatedAt", m.GeneratedAt);
        w.WriteString("tz", m.Tz);
        w.WriteString("tzLabel", m.TzLabel);
        w.WriteNumber("countedRecords", m.CountedRecords);
        w.WriteNumber("droppedDuplicates", m.DroppedDuplicates);
        WriteNullable(w, "firstTs", m.FirstTs);
        WriteNullable(w, "lastTs", m.LastTs);
        w.WriteNumber("sessions", m.Sessions);
        w.WriteEndObject();

        w.WriteStartObject("agents");
        foreach (var (id, (label, note)) in payload.Agents)
        {
            w.WriteStartObject(id);
            w.WriteString("label", label);
            w.WriteString("note", note);
            w.WriteEndObject();
        }
        w.WriteEndObject();

        w.WriteStartArray("keys");
        foreach (var key in payload.Keys) w.WriteStringValue(key);
        w.WriteEndArray();

        WriteHours(w, payload.Hours, (writer, v) => { foreach (var x in v) writer.WriteNumberValue(x); });

        w.WriteStartArray("sessions");
        foreach (var s in payload.Sessions)
        {
            w.WriteStartArray();
            w.WriteStringValue(s.Agent); w.WriteStringValue(s.Id);
            w.WriteNumberValue(s.Start); w.WriteNumberValue(s.End);
            w.WriteStringValue(s.Cwd); w.WriteStringValue(s.TopKey);
            foreach (var x in s.Vector) w.WriteNumberValue(x);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    });

    private static void WriteHours<T>(Utf8JsonWriter w, Dictionary<string, Dictionary<int, Dictionary<string, T>>> hours,
        Action<Utf8JsonWriter, T> writeVector)
    {
        w.WriteStartObject("hours");
        foreach (var (day, byHour) in hours)
        {
            w.WriteStartObject(day);
            // JavaScript enumerates integer keys in ascending order; match it so the output is stable.
            foreach (var (hour, byKey) in byHour.OrderBy(p => p.Key))
            {
                w.WriteStartObject(hour.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var (key, v) in byKey)
                {
                    w.WritePropertyName(key);
                    w.WriteStartArray();
                    writeVector(w, v);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } v) w.WriteNumber(name, v); else w.WriteNull(name);
    }

    private static string Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Options)) write(writer);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
