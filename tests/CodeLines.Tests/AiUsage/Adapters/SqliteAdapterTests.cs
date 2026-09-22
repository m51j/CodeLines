using CodeLines.Core.AiUsage.Agents;
using CodeLines.Core.AiUsage.Agents.Adapters;
using static CodeLines.Tests.AiUsage.Adapters.AdapterTestEnv;

namespace CodeLines.Tests.AiUsage.Adapters;

public sealed class SqliteAdapterTests : IDisposable
{
    private readonly AdapterTestEnv _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task ZCodeSubtractsCacheFromInput()
    {
        Sqlite(Path.Combine(_t.Env.Home, ".zcode", "cli", "db", "db.sqlite"),
            Sql("create table session (id text, directory text)"),
            Sql("create table model_usage (id text, session_id text, model_id text, started_at integer, input_tokens integer, output_tokens integer, reasoning_tokens integer, cache_creation_input_tokens integer, cache_read_input_tokens integer)"),
            Sql("insert into session values ('s1', 'C:\\work')"),
            Sql("insert into model_usage values ('u1', 's1', 'glm-4.6', 1700000000000, 100, 40, 10, 20, 30)"),
            Sql("insert into model_usage values ('u2', 's9', null, 1700000001000, 0, 5, 0, 0, 0)"),
            Sql("insert into model_usage values ('u3', 's1', 'glm-4.6', 1700000002000, 0, 0, 0, 0, 0)"));
        var adapter = new ZCodeAdapter(_t.Env);
        Assert.True(adapter.Detect());

        var records = await adapter.LoadAsync(false);

        Assert.Equal(2, records.Count);
        var a = records.Single(r => r.Request == "u1");
        Assert.Equal(1_700_000_000_000, a.Ts);
        Assert.Equal("glm-4.6", a.Model);
        Assert.Equal(50, a.Input); // 100 − 30 − 20
        Assert.Equal(20, a.CacheWrite);
        Assert.Equal(30, a.CacheRead);
        Assert.Equal(40, a.Output);
        Assert.Equal(10, a.Thinking);
        Assert.Equal("s1", a.Session);
        Assert.Equal(@"C:\work", a.Cwd);
        var b = records.Single(r => r.Request == "u2");
        Assert.Equal("unknown", b.Model);
        Assert.Equal("", b.Cwd);
    }

    [Fact]
    public async Task OpenCodeMapsOutputReasoningAndCost()
    {
        var db = Path.Combine(_t.Env.Home, ".local", "share", "opencode", "opencode.db");
        Sqlite(db,
            Sql("create table session (id text, directory text)"),
            Sql("create table message (id text, session_id text, time_created integer, data text)"),
            Sql("insert into session values ('s1', '/srv/app')"),
            Sql("insert into message values ('m1', 's1', 1, $p0)",
                """{"role":"assistant","modelID":"claude-sonnet-4","cost":0.5,"time":{"created":1700000000000},"path":{"cwd":"/own"},"tokens":{"input":10,"output":20,"reasoning":5,"cache":{"read":100,"write":7}}}"""),
            Sql("insert into message values ('m2', 's1', 1700000001000, $p0)",
                """{"role":"assistant","modelID":"qwen/qwen3:FREE","cost":0,"tokens":{"input":1,"output":2}}"""),
            Sql("insert into message values ('m3', 's1', 1700000002000, $p0)",
                """{"role":"assistant","modelID":"gpt-5","cost":0,"tokens":{"input":1,"output":2}}"""),
            Sql("insert into message values ('m4', 's1', 1700000003000, $p0)",
                """{"role":"assistant","tokens":{"input":0,"output":0}}"""),
            Sql("insert into message values ('m5', 's1', 1700000004000, $p0)",
                """{"role":"user","modelID":"gpt-5","tokens":{"input":9,"output":9}}"""),
            Sql("insert into message values ('m6', 's1', 1700000005000, $p0)",
                """{"role":"assistant","cost":0.01,"tokens":{}}"""));
        var adapter = OpenCodeAdapter.OpenCode(_t.Env);
        Assert.True(adapter.Detect());
        Assert.False(OpenCodeAdapter.Kilo(_t.Env).Detect());

        var records = (await adapter.LoadAsync(false)).ToDictionary(r => r.Request);

        Assert.Equal(["m1", "m2", "m3", "m6"], records.Keys.Order());
        var m1 = records["m1"];
        Assert.Equal(1_700_000_000_000, m1.Ts);
        Assert.Equal(10, m1.Input);
        Assert.Equal(25, m1.Output); // output + reasoning
        Assert.Equal(5, m1.Thinking);
        Assert.Equal(100, m1.CacheRead);
        Assert.Equal(7, m1.CacheWrite);
        Assert.Equal(0.5, m1.Cost);
        Assert.Equal("/own", m1.Cwd);
        Assert.Equal("s1", m1.Session);
        Assert.Equal(0, records["m2"].Cost); // a free model's 0 is a known cost
        Assert.Equal(1_700_000_001_000, records["m2"].Ts);
        Assert.Equal("/srv/app", records["m2"].Cwd);
        Assert.Null(records["m3"].Cost); // 0 on a paid model: unknown
        Assert.Equal("unknown", records["m6"].Model);
        Assert.Equal(0.01, records["m6"].Cost);
    }

    [Fact]
    public async Task KiloReadsItsOwnDatabase()
    {
        Sqlite(Path.Combine(_t.Env.Home, ".local", "share", "kilo", "kilo.db"),
            Sql("create table session (id text, directory text)"),
            Sql("create table message (id text, session_id text, time_created integer, data text)"),
            Sql("insert into message values ('k1', 's', 5, $p0)", """{"role":"assistant","modelID":"x-free","tokens":{"input":3}}"""));
        var kilo = OpenCodeAdapter.Kilo(_t.Env);
        Assert.Equal(("kilo", "Kilo Code"), (kilo.Id, kilo.Label));
        var r = Assert.Single(await kilo.LoadAsync(false));
        Assert.Equal(0, r.Cost);
    }

    [Fact]
    public async Task HermesAppliesTheCostRuleAndCalls()
    {
        Sqlite(Path.Combine(_t.Env.LocalAppData, "hermes", "state.db"),
            Sql("create table sessions (id text, model text, started_at real, input_tokens integer, output_tokens integer, cache_read_tokens integer, cache_write_tokens integer, reasoning_tokens integer, actual_cost_usd real, estimated_cost_usd real, cwd text, api_call_count integer)"),
            Sql("insert into sessions values ('a', 'hermes-4', 1700000000.5, 100, 50, 30, 20, 10, 0.0, 9.0, '/w', 7)"),
            Sql("insert into sessions values ('b', 'hermes-4', 1700000001.0, 1, 1, 0, 0, 0, null, 0.25, null, 0)"),
            Sql("insert into sessions values ('c', null, 1700000002.0, 1, 0, 0, 0, 0, null, 0, null, null)"),
            Sql("insert into sessions values ('d', 'hermes-4', 1700000003.0, 0, 0, 0, 0, 0, 1.0, 1.0, null, 3)"));
        var adapter = new HermesAdapter(_t.Env);
        Assert.True(adapter.Detect());
        Assert.Contains("session’s tokens", adapter.Note);

        var records = (await adapter.LoadAsync(false)).ToDictionary(r => r.Session);

        Assert.Equal(["a", "b", "c"], records.Keys.Order());
        var a = records["a"];
        Assert.Equal(1_700_000_000_500, a.Ts);
        Assert.Equal(0, a.Cost); // actual cost recorded, even 0, wins
        Assert.Equal(7, a.Calls);
        Assert.Equal(100, a.Input);
        Assert.Equal(30, a.CacheRead);
        Assert.Equal(20, a.CacheWrite);
        Assert.Equal(10, a.Thinking);
        Assert.Equal("hermes:a", a.Request);
        Assert.Equal("/w", a.Cwd);
        Assert.Equal(0.25, records["b"].Cost); // no actual cost: a positive estimate
        Assert.Equal(1, records["b"].Calls);
        Assert.Null(records["c"].Cost); // estimate 0 is subscription billing: unknown
        Assert.Equal("unknown", records["c"].Model);
    }

    [Fact]
    public async Task MissingDatabasesDetectFalseAndLoadNothing()
    {
        foreach (var adapter in new IAgentUsageAdapter[]
                 { new ZCodeAdapter(_t.Env), OpenCodeAdapter.OpenCode(_t.Env), OpenCodeAdapter.Kilo(_t.Env), new HermesAdapter(_t.Env) })
        {
            Assert.False(adapter.Detect());
            Assert.Empty(await adapter.LoadAsync(false));
        }
    }
}
