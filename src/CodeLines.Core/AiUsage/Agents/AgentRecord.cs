using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeLines.Core.AiUsage.Agents;

/// <summary>
/// The one record shape every agent adapter produces (ccstats' agents/record.mjs).
/// Input is uncached input only, Thinking is already inside Output, Cost is what the agent recorded (null when unknown),
/// and Calls is how many model calls the record stands for.
/// </summary>
public sealed record AgentRecord(long Ts, string Model, double Input, double CacheWrite, double CacheRead, double Output,
    double Thinking, double? Cost, string Session, string Request, string Cwd, int Calls)
{
    public static AgentRecord Create(long ts, string? model, double input = 0, double cacheWrite = 0, double cacheRead = 0,
        double output = 0, double thinking = 0, double? cost = null, string? session = null, string? request = null,
        string? cwd = null, int calls = 1) =>
        new(ts, string.IsNullOrEmpty(model) ? "unknown" : model, Math.Max(0, Finite(input)), Finite(cacheWrite), Finite(cacheRead),
            Finite(output), Finite(thinking), cost is { } c && double.IsFinite(c) ? c : null, session ?? "", request ?? "", cwd ?? "", calls);

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
}

public sealed class AgentRecordConverter : JsonConverter<AgentRecord>
{
    public override AgentRecord Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var a = doc.RootElement;
        return new(a[0].GetInt64(), a[1].GetString()!, a[2].GetDouble(), a[3].GetDouble(), a[4].GetDouble(), a[5].GetDouble(),
            a[6].GetDouble(), a[7].ValueKind == JsonValueKind.Null ? null : a[7].GetDouble(), a[8].GetString()!, a[9].GetString()!,
            a[10].GetString()!, a[11].GetInt32());
    }

    public override void Write(Utf8JsonWriter writer, AgentRecord r, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(r.Ts); writer.WriteStringValue(r.Model);
        writer.WriteNumberValue(r.Input); writer.WriteNumberValue(r.CacheWrite); writer.WriteNumberValue(r.CacheRead);
        writer.WriteNumberValue(r.Output); writer.WriteNumberValue(r.Thinking);
        if (r.Cost is { } cost) writer.WriteNumberValue(cost); else writer.WriteNullValue();
        writer.WriteStringValue(r.Session); writer.WriteStringValue(r.Request); writer.WriteStringValue(r.Cwd);
        writer.WriteNumberValue(r.Calls);
        writer.WriteEndArray();
    }
}
