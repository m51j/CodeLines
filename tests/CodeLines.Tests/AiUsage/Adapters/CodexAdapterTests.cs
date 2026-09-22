using CodeLines.Core.AiUsage.Agents.Adapters;

namespace CodeLines.Tests.AiUsage.Adapters;

public sealed class CodexAdapterTests : IDisposable
{
    private readonly AdapterTestEnv _t = new();

    public void Dispose() => _t.Dispose();

    private const string Rollout = """
        {"timestamp":"2025-01-01T00:00:00.000Z","type":"session_meta","payload":{"id":"sess-1","cwd":"C:\\first"}}
        {"timestamp":"2025-01-01T00:00:01.000Z","type":"turn_context","payload":{"model":"gpt-5","cwd":"C:\\proj"}}
        {"type":"response_item","payload":{"type":"message","content":"ignored by the pre-filter"}}
        {"timestamp":"2025-01-01T00:00:02.000Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"cached_input_tokens":40,"output_tokens":20,"reasoning_output_tokens":5},"total_token_usage":{"total_tokens":120}}}}
        {"timestamp":"2025-01-01T00:00:03.000Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"cached_input_tokens":40,"output_tokens":20},"total_token_usage":{"total_tokens":120}}}}
        {"timestamp":"2025-01-01T00:00:04.000Z","type":"event_msg","payload":{"type":"token_count","info":null}}
        {"timestamp":"2025-01-01T00:00:05.500Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":10,"cached_input_tokens":0,"cache_write_input_tokens":3,"output_tokens":7},"total_token_usage":{"total_tokens":137}}}}
        """;

    [Fact]
    public async Task MapsTokenCountsAndSkipsReEmits()
    {
        AdapterTestEnv.Write(_t.Env.Home, @".codex\sessions\2025\01\01\rollout-a.jsonl", Rollout);
        var adapter = new CodexAdapter(_t.Env);
        Assert.True(adapter.Detect());

        var records = await adapter.LoadAsync(false);

        Assert.Equal(2, records.Count);
        var a = records[0];
        Assert.Equal(DateTimeOffset.Parse("2025-01-01T00:00:02Z").ToUnixTimeMilliseconds(), a.Ts);
        Assert.Equal("gpt-5", a.Model);
        Assert.Equal(60, a.Input); // input − cached
        Assert.Equal(40, a.CacheRead);
        Assert.Equal(20, a.Output);
        Assert.Equal(5, a.Thinking);
        Assert.Null(a.Cost);
        Assert.Equal("sess-1", a.Session);
        Assert.Equal("C:\\proj", a.Cwd);
        Assert.Equal("codex:2025-01-01T00:00:02.000Z:120", a.Request);
        var b = records[1];
        Assert.Equal(10, b.Input);
        Assert.Equal(3, b.CacheWrite);
        Assert.Equal("codex:2025-01-01T00:00:05.500Z:137", b.Request);
    }

    [Fact]
    public async Task ReadsArchivedSessionsAndOnlyRolloutFiles()
    {
        AdapterTestEnv.Write(_t.Env.Home, @".codex\archived_sessions\rollout-b.jsonl", Rollout);
        AdapterTestEnv.Write(_t.Env.Home, @".codex\archived_sessions\other.jsonl", Rollout);
        var records = await new CodexAdapter(_t.Env).LoadAsync(false);
        Assert.Equal(2, records.Count);
    }

    [Fact]
    public async Task SecondLoadReusesTheCache()
    {
        AdapterTestEnv.Write(_t.Env.Home, @".codex\sessions\rollout-a.jsonl", Rollout);
        var adapter = new CodexAdapter(_t.Env);
        await adapter.LoadAsync(false);

        // Tamper with the cached copy: a reused entry comes back tampered, a re-parsed one does not.
        var cacheFile = _t.Env.CacheFile("codex");
        var json = File.ReadAllText(cacheFile);
        Assert.Contains("\"version\":\"1.1\"", json);
        File.WriteAllText(cacheFile, json.Replace("\"gpt-5\"", "\"from-cache\""));

        Assert.All(await adapter.LoadAsync(false), r => Assert.Equal("from-cache", r.Model));
        Assert.All(await adapter.LoadAsync(true), r => Assert.Equal("gpt-5", r.Model));
    }

    [Fact]
    public void DetectIsFalseWithoutFolders() => Assert.False(new CodexAdapter(_t.Env).Detect());
}
