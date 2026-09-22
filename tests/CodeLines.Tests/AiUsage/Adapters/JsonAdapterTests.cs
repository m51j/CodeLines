using CodeLines.Core.AiUsage.Agents.Adapters;

namespace CodeLines.Tests.AiUsage.Adapters;

public sealed class JsonAdapterTests : IDisposable
{
    private readonly AdapterTestEnv _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task ClineMapsPerMessageMetrics()
    {
        var sessions = Path.Combine(_t.Env.Home, ".cline", "data", "sessions");
        AdapterTestEnv.Write(sessions, @"abc\abc.json",
            """{"session_id":"sess-abc","model":"head-model","workspace_root":"C:\\repo","started_at":"2025-02-01T00:00:00Z"}""");
        AdapterTestEnv.Write(sessions, @"abc\abc.messages.json", """
            {"messages":[
              {"role":"user","metrics":{"inputTokens":5,"outputTokens":5}},
              {"id":"m1","role":"assistant","ts":1700000000000,"modelInfo":{"id":"claude-sonnet-4"},
               "metrics":{"inputTokens":1000,"outputTokens":50,"cacheReadTokens":600,"cacheWriteTokens":100,"cost":0.02}},
              {"role":"assistant","ts":1700000001000,"metrics":{"inputTokens":10,"outputTokens":0,"cost":"n/a"}},
              {"role":"assistant","metrics":{"inputTokens":0,"outputTokens":0}},
              {"role":"assistant","metrics":{"inputTokens":3,"outputTokens":1}}
            ]}
            """);
        Directory.CreateDirectory(Path.Combine(sessions, "no-messages"));
        var adapter = new ClineAdapter(_t.Env);
        Assert.True(adapter.Detect());

        var records = await adapter.LoadAsync(false);

        Assert.Equal(3, records.Count);
        var a = records[0];
        Assert.Equal(1_700_000_000_000, a.Ts);
        Assert.Equal("claude-sonnet-4", a.Model);
        Assert.Equal(300, a.Input); // 1000 − 600 − 100
        Assert.Equal(600, a.CacheRead);
        Assert.Equal(100, a.CacheWrite);
        Assert.Equal(50, a.Output);
        Assert.Equal(0.02, a.Cost);
        Assert.Equal("sess-abc", a.Session);
        Assert.Equal("cline:m1", a.Request);
        Assert.Equal(@"C:\repo", a.Cwd);

        var b = records[1];
        Assert.Equal("head-model", b.Model);
        Assert.Null(b.Cost); // not a number
        Assert.Equal("cline:1700000001000", b.Request);

        var c = records[2];
        Assert.Equal(DateTimeOffset.Parse("2025-02-01T00:00:00Z").ToUnixTimeMilliseconds(), c.Ts); // header start time
        Assert.Equal("cline:undefined", c.Request);
    }

    [Fact]
    public async Task ClineReusesTheCache()
    {
        var sessions = Path.Combine(_t.Env.Home, ".cline", "data", "sessions");
        AdapterTestEnv.Write(sessions, @"s\s.messages.json",
            """{"messages":[{"role":"assistant","ts":1,"modelInfo":{"id":"live-model"},"metrics":{"inputTokens":1,"outputTokens":1}}]}""");
        var adapter = new ClineAdapter(_t.Env);
        await adapter.LoadAsync(false);
        var cacheFile = _t.Env.CacheFile("cline");
        File.WriteAllText(cacheFile, File.ReadAllText(cacheFile).Replace("live-model", "cached-model"));
        Assert.Equal("cached-model", Assert.Single(await adapter.LoadAsync(false)).Model);
        Assert.Equal("live-model", Assert.Single(await adapter.LoadAsync(true)).Model);
    }

    [Fact]
    public async Task CopilotReplaysThePatchLog()
    {
        var ws = Path.Combine(_t.Env.AppData, "Code", "User", "workspaceStorage", "ws1");
        AdapterTestEnv.Write(ws, "workspace.json", """{"folder":"file:///d%3A/my%20proj"}""");
        AdapterTestEnv.Write(ws, @"chatSessions\chat-1.jsonl", string.Join("\n",
            """{"kind":0,"v":{"sessionId":"sess","creationDate":1000,"requests":[{"requestId":"r0","timestamp":5000,"modelId":"copilot/gpt-4o"}]}}""",
            """{"kind":2,"k":["requests"],"v":[{"requestId":"r1","timestamp":6000,"modelId":"copilot/claude"}]}""",
            """{"kind":1,"k":["requests",0,"completionTokens"],"v":10}""",
            """{"kind":1,"k":["requests",0,"completionTokens"],"v":40}""",
            """{"kind":1,"k":["requests",0,"completionTokens"],"v":25}""",
            """{"kind":1,"k":["requests",0,"result"],"v":{"metadata":{"promptTokens":100,"outputTokens":30,"toolCallRounds":[{},{},{}]}}}""",
            """{"kind":1,"k":["requests",0,"response"],"v":[{"value":"huge"}]}""",
            "not json",
            """{"kind":1,"k":["requests",1,"result","metadata","promptTokens"],"v":50}""",
            """{"kind":2,"k":["requests"],"i":1,"v":[{"timestamp":7000,"promptTokens":60,"completionTokens":5}]}""",
            """{"kind":1,"k":["inputState","selectedModel","identifier"],"v":"copilot/gpt-5"}""",
            """{"kind":2,"k":["requests"],"v":[{"requestId":"empty","timestamp":8000}]}""",
            """{"kind":1,"k":["requests",4,"promptTokens"],"v":7}""",
            ""));
        var adapter = new CopilotAdapter(_t.Env);
        Assert.True(adapter.Detect());
        Assert.Contains("round’s prompt size", adapter.Note);

        var records = await adapter.LoadAsync(false);

        Assert.Equal(3, records.Count);
        var a = records[0];
        Assert.Equal("r0", a.Request);
        Assert.Equal(5000, a.Ts);
        Assert.Equal("gpt-4o", a.Model);
        Assert.Equal(100, a.Input);
        Assert.Equal(40, a.Output); // the largest completionTokens patch
        Assert.Equal(3, a.Calls);
        Assert.Equal("sess", a.Session);
        Assert.Equal(@"d:\my proj", a.Cwd);

        var b = records[1]; // r1 was spliced out at index 1
        Assert.Equal("sess:1", b.Request);
        Assert.Equal(7000, b.Ts);
        Assert.Equal("gpt-5", b.Model); // from inputState
        Assert.Equal(60, b.Input);
        Assert.Equal(5, b.Output);
        Assert.Equal(1, b.Calls);

        var c = records[2]; // index 4 written past the end: a hole at 3, then this request
        Assert.Equal("sess:4", c.Request);
        Assert.Equal(1000, c.Ts); // creationDate fallback
        Assert.Equal(7, c.Input);
    }

    [Fact]
    public async Task LmStudioCountsOnlyTheSelectedVersion()
    {
        var root = Path.Combine(_t.Env.Home, ".lmstudio", "conversations");
        AdapterTestEnv.Write(root, "chat-1.conversation.json", """
            {"createdAt":1600000000000,"assistantLastMessagedAt":1700000009000,"lastUsedModel":{"identifier":"qwen3-8b"},
             "messages":[
               {"versions":[{"role":"user","content":[]}]},
               {"currentlySelected":1,"versions":[
                 {"role":"assistant","steps":[{"stepIdentifier":"1700000001000-a","genInfo":{"identifier":"discarded","stats":{"promptTokensCount":999,"predictedTokensCount":999}}}]},
                 {"role":"assistant","steps":[
                   {"stepIdentifier":"1700000002000-0.5","genInfo":{"identifier":"llama-3","stats":{"promptTokensCount":120,"predictedTokensCount":30}}},
                   {"stepIdentifier":"x","genInfo":{"stats":{"promptTokensCount":1}}},
                   {"genInfo":{}}
                 ]}]},
               {"currentlySelected":5,"versions":[{"role":"assistant","steps":[{"genInfo":{"indexedModelIdentifier":"idx-model","stats":{"predictedTokensCount":4}}}]}]}
             ]}
            """);
        var adapter = new LmStudioAdapter(_t.Env);
        Assert.True(adapter.Detect());

        var records = await adapter.LoadAsync(false);

        Assert.Equal(3, records.Count);
        var a = records[0];
        Assert.Equal(1_700_000_002_000, a.Ts);
        Assert.Equal("llama-3", a.Model);
        Assert.Equal(120, a.Input);
        Assert.Equal(30, a.Output);
        Assert.Equal(0, a.Cost);
        Assert.Equal("chat-1", a.Session);
        Assert.Equal("lmstudio:1700000002000-0.5", a.Request);

        var b = records[1]; // "x" is not a number: falls back to the last assistant message time
        Assert.Equal(1_700_000_009_000, b.Ts);
        Assert.Equal("qwen3-8b", b.Model);
        Assert.Equal("lmstudio:x", b.Request);

        var c = records[2]; // selected version missing: versions[0]
        Assert.Equal("idx-model", c.Model);
        Assert.Equal("lmstudio:1700000009000", c.Request);
    }
}
