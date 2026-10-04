using CodeLines.Core.AiUsage;
using CodeLines.Core.AiUsage.Agents;

namespace CodeLines.Tests.AiUsage;

public sealed class AiUsageUnitTests
{
    private const string Line = """{"type":"assistant","sessionId":"s1","requestId":"r1","cwd":"D:\\p","gitBranch":"main","timestamp":"2026-09-01T10:30:00.000Z","message":{"id":"m1","model":"claude-haiku-4-5-20251001","usage":{"input_tokens":10,"cache_creation_input_tokens":100,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":40},"cache_read_input_tokens":5,"output_tokens":7,"output_tokens_details":{"thinking_tokens":3}}}}""";

    [Theory]
    [InlineData("claude-haiku-4-5-20251001", "claude-haiku-4-5", "Haiku 4.5")]
    [InlineData("anthropic.claude-opus-4-8", "claude-opus-4-8", "Opus 4.8")]
    [InlineData("claude-sonnet-5@20260101", "claude-sonnet-5", "Sonnet 5")]
    [InlineData("gpt-5.5", "gpt-5.5", "gpt-5.5")]
    public void Models_are_normalized_and_named(string raw, string id, string label)
    {
        Assert.Equal(id, ClaudePricing.NormalizeModel(raw));
        Assert.Equal(label, ClaudePricing.DisplayModel(raw));
    }

    [Fact]
    public void Pricing_derives_cache_rates()
    {
        var sonnet = ClaudePricing.RatesFor("claude-sonnet-4-6");
        Assert.Equal((3, 15, 3 * 0.1, 3.75, 6.0), (sonnet.Input, sonnet.Output, sonnet.CacheRead, sonnet.Write5m, sonnet.Write1h));
        Assert.Equal(0.2, ClaudePricing.RatesFor("claude-opus-5-5").CacheRead);
        Assert.Equal(0.25, ClaudePricing.RatesFor("claude-fable-5-1").CacheRead);
        Assert.True(ClaudePricing.IsKnownModel("claude-sonnet-5-5"));
        Assert.Equal(0.2, ClaudePricing.RatesFor("claude-sonnet-5-5").CacheRead);
        var unknown = ClaudePricing.RatesFor("claude-future-9");
        Assert.False(unknown.Known);
        Assert.Equal((5, 25), (unknown.Input, unknown.Output));
        Assert.Equal((1_000_000 * 5 + 1_000_000 * 25) / 1e6, ClaudePricing.CostOf(1_000_000, 0, 0, 0, 1_000_000, "claude-opus-4-8"));
    }

    [Fact]
    public void Agent_list_prices_cover_openai_and_claude_models()
    {
        Assert.Equal(new ListRate(4, 0.4, 5, 20), AgentPricing.For("GPT-5.6-SOL"));
        Assert.Equal(new ListRate(5, 0.5, 6.25, 25), AgentPricing.For("claude-opus-4-6-thinking"));
        Assert.Null(AgentPricing.For("mystery-model"));
    }

    [Theory]
    [InlineData("gpt-6-luna", 0.1, 0.01, 0.125, 0.5)]
    [InlineData("omniroute/kr/claude-sonnet-4.5-high", 3, 0.3, 3.75, 15)]
    [InlineData("claude-sonnet-4.6", 3, 0.3, 3.75, 15)]
    [InlineData("gemini-3.8-flash-high", 0.75, 0.075, 0.75, 3.75)]
    [InlineData("gemini-3-flash-agent", 0.5, 0.05, 0.5, 3)]
    [InlineData("z-ai/glm-5.3-flash", 0.15, 0.03, 0.15, 0.5)]
    [InlineData("GLM-5.3-Flash", 0.15, 0.03, 0.15, 0.5)]
    public void Agent_model_ids_are_normalized_before_pricing(string model, double input, double cacheRead, double cacheWrite, double output)
    {
        var r = AgentPricing.For(model)!;
        Assert.Equal((input, cacheRead, cacheWrite, output), (r.Input, Math.Round(r.CacheRead, 6), r.CacheWrite, r.Output));
    }

    [Fact]
    public void Models_without_an_official_price_stay_unpriced() => Assert.Null(AgentPricing.For("gemini-2.0-flash"));

    [Fact]
    public void Scanner_keeps_ccstats_counting_rules()
    {
        var r = ClaudeTranscriptScanner.TryParseLine(Line)!;
        Assert.Equal(("claude-haiku-4-5", 10L, 60L, 40L, 5L, 7L, 3L), (r.Model, r.Input, r.CacheWrite5m, r.CacheWrite1h, r.CacheRead, r.Output, r.Thinking));
        Assert.Equal(("s1", "r1", @"D:\p", "main"), (r.Session, r.Request, r.Cwd, r.Branch));
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T10:30:00Z").ToUnixTimeMilliseconds(), r.Ts);

        Assert.Null(ClaudeTranscriptScanner.TryParseLine(Line.Replace("claude-haiku-4-5-20251001", "<synthetic>")));
        Assert.Null(ClaudeTranscriptScanner.TryParseLine(Line.Replace("\"type\":\"assistant\"", "\"type\":\"user\"")));
        Assert.Null(ClaudeTranscriptScanner.TryParseLine(Line.Replace("2026-09-01T10:30:00.000Z", "not a date")));
        Assert.Null(ClaudeTranscriptScanner.TryParseLine("""{"type":"assistant","usage":1}"""));
        var noRequest = ClaudeTranscriptScanner.TryParseLine(Line.Replace("\"requestId\":\"r1\",", ""))!;
        Assert.Equal("m1", noRequest.Request);
    }

    [Fact]
    public void Deduplication_keeps_the_copy_with_the_most_output()
    {
        ClaudeRecord R(long ts, string req, long output) => new(ts, "claude-opus-4-8", 1, 0, 0, 0, output, 0, "s", req, "", "");
        var payload = ClaudeUsageAggregator.Aggregate([R(1000, "a", 5), R(1001, "a", 9), R(1002, "a", 9), R(1003, "", 1), R(1004, "", 1)], new UsageClock(true));
        Assert.Equal((5L, 3L, 1L, 2L), (payload.Meta.RawRecords, payload.Meta.CountedRecords, payload.Meta.PartialRecords, payload.Meta.DuplicateRecords));
        var v = payload.Hours["1970-01-01"][0]["claude-opus-4-8"];
        Assert.Equal((11L, 3L), (v[ClaudeVector.Out], v[ClaudeVector.Req]));
        Assert.Equal(5L, payload.AppBasis["claude-opus-4-8"][ClaudeVector.Req]);
    }

    [Fact]
    public void Local_time_buckets_follow_the_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus3", TimeSpan.FromHours(3), "plus3", "plus3");
        var clock = new UsageClock(false, zone);
        Assert.Equal(("2026-09-02", 1), clock.Parts(DateTimeOffset.Parse("2026-09-01T22:00:00Z").ToUnixTimeMilliseconds()));
        Assert.Equal("local (UTC+03:00)", clock.Label);
        Assert.Equal("UTC", new UsageClock(true).Label);
    }

    [Fact]
    public void Templates_escape_and_format_like_ccstats()
    {
        Assert.Equal("a&#38;b&#60;&#62;&#34;", AiUsageTemplates.Escape("a&b<>\""));
        Assert.Equal(["1.23B", "4.6M", "12.0K", "999"], new[] { 1.234e9, 4.56e6, 12_000, 999 }.Select(AiUsageTemplates.FormatTokens));
        var html = AiUsageTemplates.RenderAgent("A <b>", "{}");
        Assert.Contains("<title>A &#60;b&#62; Usage</title>", html);
        Assert.Contains("<script id=\"payload\" type=\"application/json\">{}</script>", html);
        var dashboard = AiUsageTemplates.RenderDashboard([new("all", "All agents", "agents/all.html", 2_500_000)],
            DateTimeOffset.Parse("2026-09-23T15:04:05Z"), TimeZoneInfo.Utc);
        Assert.Contains("<button role=\"tab\" data-id=\"all\" data-href=\"agents/all.html\" aria-selected=\"true\">All agents <span class=\"n\">2.5M</span></button>", dashboard);
        Assert.Contains("generated 9/23/2026, 3:04:05 PM", dashboard);
    }

    [Fact]
    public void Payload_json_is_safe_inside_a_script_element()
    {
        var record = AgentRecord.Create(0, "</script><b>", 1, request: "x", cwd: "D:\\العمل");
        var json = AiUsagePayloadWriter.ToJson(AgentUsageAggregator.Aggregate([new("a", "A", null, [record])], "t", new UsageClock(true)));
        Assert.DoesNotContain("<", json);
        Assert.Equal(@"D:\العمل", System.Text.Json.Nodes.JsonNode.Parse(json)!["sessions"]![0]![4]!.GetValue<string>());
    }

    [Fact]
    public void Sessions_are_attributed_to_the_innermost_project()
    {
        AgentRecord R(string session, string cwd, long ts) => AgentRecord.Create(ts, "gpt-5.5", 1000, output: 1000, session: session, request: session + ts, cwd: cwd);
        var payload = AgentUsageAggregator.Aggregate([new("codex", "Codex", null,
            [R("a", @"D:\Work\alpha", 1000), R("b", "/d/Work/alpha/sub/x", 2000), R("c", @"D:\Work\alphabet", 3000), R("d", "D:/work/beta", 4000), R("e", "", 5000)])],
            "t", new UsageClock(true));
        var roots = new Dictionary<string, string> { ["alpha"] = @"D:\Work\alpha", ["sub"] = @"D:\Work\alpha\sub\", ["beta"] = @"d:\work\beta", ["none"] = @"E:\x" };

        var usage = AiProjectUsageMatcher.Match(payload, roots);

        Assert.Equal((1, 1, 1, 0), (usage["alpha"].Sessions, usage["sub"].Sessions, usage["beta"].Sessions, usage["none"].Sessions));
        Assert.Equal(2000, usage["alpha"].Tokens);
        Assert.Equal("$0.04", usage["alpha"].CostText);
        Assert.Equal("—", usage["none"].CostText);
        var recent = AiProjectUsageMatcher.Match(payload, roots, DateTimeOffset.FromUnixTimeMilliseconds(2500));
        Assert.Equal((0, 0, 1), (recent["alpha"].Sessions, recent["sub"].Sessions, recent["beta"].Sessions));
    }

    [Fact]
    public void Days_window_counts_back_from_the_newest_record_at_local_midnight()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus3", TimeSpan.FromHours(3), "plus3", "plus3");
        var last = DateTimeOffset.Parse("2026-09-10T12:00:00+03:00").ToUnixTimeMilliseconds();
        var (since, until) = new UsageClock(false, zone).Window(new AiUsageOptions { Days = 7 }, last);
        Assert.Equal(DateTimeOffset.Parse("2026-09-04T00:00:00+03:00").ToUnixTimeMilliseconds(), since);
        Assert.Null(until);
        var (s2, u2) = new UsageClock(true).Window(new AiUsageOptions { Since = new(2026, 8, 1), Until = new(2026, 8, 31) }, last);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T00:00:00Z").ToUnixTimeMilliseconds(), s2);
        Assert.Equal(DateTimeOffset.Parse("2026-08-31T23:59:59.999Z").ToUnixTimeMilliseconds(), u2);
    }
}
