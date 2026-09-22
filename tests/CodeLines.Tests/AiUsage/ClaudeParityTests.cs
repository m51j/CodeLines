using System.Text.Json.Nodes;
using CodeLines.Core.AiUsage;
using CodeLines.Core.AiUsage.Agents;

namespace CodeLines.Tests.AiUsage;

/// <summary>
/// The C# port against payloads ccstats itself produced for the same transcripts
/// (Fixtures/generate-goldens.mjs). The report pages consume these payloads unchanged,
/// so equal payloads mean the tab shows exactly what ccstats' HTML shows.
/// </summary>
public sealed class ClaudeParityTests
{
    internal static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "AiUsage", "Fixtures");
    private static readonly UsageClock Utc = new(utc: true);

    internal static List<ClaudeRecord> ScanFixture() => ClaudeTranscriptScanner.ListTranscripts([Path.Combine(FixtureRoot, "claude")])
        .SelectMany(file => ClaudeTranscriptScanner.ScanFile(file)).ToList();

    [Fact]
    public void Scanner_reads_the_same_records_as_ccstats()
    {
        var records = ScanFixture();
        Assert.Equal(685, records.Count);
        Assert.DoesNotContain(records, r => r.Model.StartsWith('<'));
        Assert.Contains(records, r => r.Model == "claude-sonnet-4-6");
        Assert.Contains(records, r => r.Model == "claude-sonnet-5");
    }

    [Fact]
    public void Deduplicated_payload_matches_ccstats() =>
        AssertPayload("claude-payload.golden.json", AiUsagePayloadWriter.ToJson(ClaudeUsageAggregator.Aggregate(ScanFixture(), Utc)));

    [Fact]
    public void Raw_payload_matches_ccstats() =>
        AssertPayload("claude-payload-raw.golden.json", AiUsagePayloadWriter.ToJson(ClaudeUsageAggregator.Aggregate(ScanFixture(), Utc, dedupe: false)));

    [Fact]
    public void Combined_agents_payload_matches_ccstats()
    {
        var source = new AgentSource("claude", "Claude Code", null, AgentUsageAggregator.FromClaudeRecords(ScanFixture()));
        AssertPayload("agents-payload.golden.json", AiUsagePayloadWriter.ToJson(AgentUsageAggregator.Aggregate([source], "All agents", Utc)));
    }

    private static void AssertPayload(string golden, string actualJson)
    {
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureRoot, golden)))!.AsObject();
        var actual = JsonNode.Parse(actualJson)!.AsObject();
        expected["meta"]!.AsObject().Remove("generatedAt");
        actual["meta"]!.AsObject().Remove("generatedAt");
        JsonAssert.Equivalent(expected, actual);
    }
}

internal static class JsonAssert
{
    /// <summary>Deep equality where object key order does not matter and numbers compare by value.</summary>
    public static void Equivalent(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        switch (expected)
        {
            case null:
                Assert.True(actual is null, $"{path}: expected null, got {actual?.ToJsonString()}");
                break;
            case JsonObject e:
                var a = Assert.IsType<JsonObject>(actual);
                Assert.True(e.Select(p => p.Key).Order(StringComparer.Ordinal).SequenceEqual(a.Select(p => p.Key).Order(StringComparer.Ordinal)),
                    $"{path}: keys differ.\nexpected: {string.Join(",", e.Select(p => p.Key))}\nactual:   {string.Join(",", a.Select(p => p.Key))}");
                foreach (var (key, value) in e) Equivalent(value, a[key], $"{path}.{key}");
                break;
            case JsonArray e:
                var arr = Assert.IsType<JsonArray>(actual);
                Assert.True(e.Count == arr.Count, $"{path}: expected {e.Count} items, got {arr.Count}");
                for (var i = 0; i < e.Count; i++) Equivalent(e[i], arr[i], $"{path}[{i}]");
                break;
            case JsonValue e when e.GetValueKind() == System.Text.Json.JsonValueKind.Number:
                var x = e.GetValue<double>();
                var y = actual!.GetValue<double>();
                Assert.True(Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(x)), $"{path}: expected {x}, got {y}");
                break;
            default:
                Assert.True(JsonNode.DeepEquals(expected, actual), $"{path}: expected {expected.ToJsonString()}, got {actual?.ToJsonString()}");
                break;
        }
    }
}
