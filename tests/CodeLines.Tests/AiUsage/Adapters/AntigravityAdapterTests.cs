using CodeLines.Core.AiUsage.Agents.Adapters;
using static CodeLines.Tests.AiUsage.Adapters.AdapterTestEnv;

namespace CodeLines.Tests.AiUsage.Adapters;

public sealed class AntigravityAdapterTests : IDisposable
{
    private readonly AdapterTestEnv _t = new();

    public void Dispose() => _t.Dispose();

    private string Conversations => Path.Combine(_t.Env.Home, ".gemini", "antigravity", "conversations");

    private static byte[] Step(ulong seconds, ulong nanos, byte[]? usage) => usage is null
        ? Pb.Msg(Pb.Bytes(1, Pb.Msg(Pb.Int(1, seconds), Pb.Int(2, nanos))), Pb.Int(3, 42))
        : Pb.Msg(Pb.Fixed64(2), Pb.Bytes(1, Pb.Msg(Pb.Int(1, seconds), Pb.Int(2, nanos))), Pb.Fixed32(4), Pb.Bytes(9, usage));

    private static byte[] Usage(ulong model, ulong input, ulong output, ulong cw, ulong cr, ulong think, string? responseId = null) =>
        Pb.Msg(Pb.Int(1, model), Pb.Int(2, input), Pb.Int(3, output), Pb.Int(4, cw), Pb.Int(5, cr), Pb.Int(9, think),
            responseId is null ? [] : Pb.Str(11, responseId));

    private static byte[] GenMetadata(ulong model, string name) =>
        Pb.Msg(Pb.Bytes(1, Pb.Msg(Pb.Bytes(4, Pb.Msg(Pb.Int(1, model), Pb.Int(2, 1))), Pb.Str(19, name))));

    private static byte[] Workspace(string uri) => Pb.Msg(Pb.Bytes(1, Pb.Msg(Pb.Str(1, uri))));

    private static void Conversation(string file, string uri, (ulong Model, string Name)[] names, params byte[]?[] steps)
    {
        var statements = new List<(string, object[])>
        {
            Sql("create table steps (idx integer, metadata blob)"),
            Sql("create table gen_metadata (data blob)"),
            Sql("create table trajectory_metadata_blob (data blob)"),
            Sql("insert into trajectory_metadata_blob values ($p0)", Workspace(uri)),
            Sql("insert into gen_metadata values (null)")
        };
        statements.AddRange(names.Select(n => Sql("insert into gen_metadata values ($p0)", GenMetadata(n.Model, n.Name))));
        statements.AddRange(steps.Select((s, i) => Sql("insert into steps values ($p0, $p1)", i, (object?)s ?? DBNull.Value)));
        Sqlite(file, [.. statements]);
    }

    [Fact]
    public async Task DecodesStepsAndResolvesModelNames()
    {
        // conv-a names enum 7 itself; enum 9 is only named by conv-b; enum 5 by nobody.
        Conversation(Path.Combine(Conversations, "conv-a.db"), "file:///D:/x", [(7, "gemini-3-pro")],
            Step(1_700_000_000, 123_456_789, Usage(7, 5_000_000_000, 300, 20, 400, 100, "resp-1")),
            Step(1_700_000_001, 0, null),
            Step(1_700_000_002, 0, Usage(9, 10, 20, 0, 0, 0)),
            Step(1_700_000_003, 0, Usage(5, 1, 1, 0, 0, 0)),
            Step(0, 0, Usage(7, 1, 1, 0, 0, 0)), // no timestamp: skipped
            null);
        Conversation(Path.Combine(Conversations, "conv-b.db"), "file:///home/u/my%20proj", [(7, "older-name"), (9, "claude-x")],
            Step(1_700_000_100, 0, Usage(7, 1, 2, 0, 0, 0, "resp-b")));
        File.WriteAllText(Path.Combine(Conversations, "broken.db"), "not a database at all");

        var adapter = new AntigravityAdapter(_t.Env, "antigravity", "Antigravity", "antigravity");
        Assert.True(adapter.Detect());
        var records = await adapter.LoadAsync(false);

        Assert.Equal(4, records.Count);
        var a = records[0];
        Assert.Equal(1_700_000_000_123, a.Ts);
        Assert.Equal("gemini-3-pro", a.Model);
        Assert.Equal(5_000_000_000, a.Input);
        Assert.Equal(300, a.Output);
        Assert.Equal(20, a.CacheWrite);
        Assert.Equal(400, a.CacheRead);
        Assert.Equal(100, a.Thinking);
        Assert.Null(a.Cost);
        Assert.Equal("resp-1", a.Request);
        Assert.Equal("conv-a", a.Session);
        Assert.Equal(@"D:\x", a.Cwd);

        Assert.Equal("claude-x", records[1].Model);
        Assert.Equal("conv-a:2", records[1].Request);
        Assert.Equal("unnamed model #5", records[2].Model);

        var b = records[3];
        Assert.Equal("older-name", b.Model); // its own conversation's name wins over conv-a's
        Assert.Equal("/home/u/my proj", b.Cwd);
        Assert.Equal("conv-b", b.Session);
    }

    [Fact]
    public async Task ConversationWithoutStepsTableYieldsNothing()
    {
        Sqlite(Path.Combine(Conversations, "empty.db"), Sql("create table other (x)"));
        Assert.Empty(await new AntigravityAdapter(_t.Env, "antigravity", "Antigravity", "antigravity").LoadAsync(false));
    }

    [Fact]
    public void VariantsLookInTheirOwnFolders()
    {
        Directory.CreateDirectory(Path.Combine(_t.Env.Home, ".gemini", "antigravity-cli", "conversations"));
        var adapters = AgentAdapters.Create(_t.Env).Where(a => a is AntigravityAdapter).ToDictionary(a => a.Id);
        Assert.True(adapters["agy"].Detect());
        Assert.False(adapters["antigravity"].Detect());
        Assert.False(adapters["antigravity-ide"].Detect());
        Assert.StartsWith("Antigravity keeps no official usage log", adapters["agy"].Note);
    }
}
