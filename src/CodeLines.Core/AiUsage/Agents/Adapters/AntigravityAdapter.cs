using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodeLines.Core.AiUsage.Agents.Adapters;

/// <summary>
/// Google Antigravity: the IDE, the standalone app and the <c>agy</c> CLI. Each keeps
/// ~/.gemini/&lt;variant&gt;/conversations/&lt;conversation id&gt;.db, one SQLite file per conversation (sub-agents get
/// their own file). Everything inside is protobuf with no published schema; the fields were matched by hand:
/// <code>
///   steps.metadata                      a step
///     1  {1 seconds, 2 nanos}           created at
///     9  usage (output = thinking + response adds up)
///        1 model enum   2 input (excludes cache reads)   3 output
///        4 cache write  5 cache read   9 thinking   11 response id
///   gen_metadata.data  1 {4 usage, 19 model name}   -> enum -> name map
///   trajectory_metadata_blob.data  1 {1 workspace uri}
/// </code>
/// Only steps that called a model carry usage. The response id is unique per call and serves as the request key.
/// </summary>
public sealed partial class AntigravityAdapter(AgentEnvironment env, string id, string label, string dir) : IAgentUsageAdapter
{
    private readonly string _root = Path.Combine(env.Home, ".gemini", dir, "conversations");

    public string Id => id;
    public string Label => label;
    public string? Note => "Antigravity keeps no official usage log; these counts are decoded from its conversation databases (the per-call usage record each model step stores).";

    public bool Detect() => Path.Exists(_root);

    public Task<IReadOnlyList<AgentRecord>> LoadAsync(bool rescan, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<AgentRecord>>(() => Load(cancellationToken), cancellationToken);

    private List<AgentRecord> Load(CancellationToken cancellationToken)
    {
        List<string> files;
        try { files = Directory.EnumerateFiles(_root).Where(f => f.EndsWith(".db", StringComparison.Ordinal)).Sorted(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { files = []; }
        var conversations = new List<Conversation>();
        var global = new Dictionary<double, string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var c = SqliteSource.WithDb(file, env.TempDirectory, db => ReadConversation(db, file));
                conversations.Add(c);
                foreach (var (k, v) in c.Names) global.TryAdd(k, v);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // unreadable conversation: skip it, keep the rest
            }
        }
        // A model is named by its own conversation first: the same enum has carried different names over time.
        var records = new List<AgentRecord>();
        foreach (var c in conversations)
            foreach (var x in c.Calls)
            {
                var model = c.Names.GetValueOrDefault(x.Model) ?? global.GetValueOrDefault(x.Model) ?? $"unnamed model #{JsCompat.Number(x.Model)}";
                records.Add(AgentRecord.Create(x.Ts, model, x.Input, x.CacheWrite, x.CacheRead, x.Output, x.Thinking, null,
                    x.Session, x.Request, x.Cwd));
            }
        return records;
    }

    private sealed record Call(long Ts, double Model, double Input, double Output, double CacheWrite, double CacheRead,
        double Thinking, string Request, string Session, string Cwd);

    private sealed record Conversation(List<Call> Calls, Dictionary<double, string> Names);

    private static Conversation ReadConversation(SqliteConnection db, string file)
    {
        var names = new Dictionary<double, string>();
        if (!SqliteSource.HasTable(db, "steps")) return new([], names);

        if (SqliteSource.HasTable(db, "gen_metadata"))
            foreach (var r in SqliteSource.Query(db, "select data from gen_metadata"))
            {
                if (r["data"] is null or "") continue;
                var one = Protobuf.Sub(Protobuf.Decode(JsCompat.Bytes(r["data"])), 1);
                var usage = Protobuf.Sub(one, 4);
                var name = Protobuf.Str(one, 19);
                if (usage is not null && name.Length > 0) names[Protobuf.Int(usage, 1)] = name;
            }

        var cwd = "";
        if (SqliteSource.HasTable(db, "trajectory_metadata_blob")
            && SqliteSource.Query(db, "select data from trajectory_metadata_blob").FirstOrDefault() is { } blob
            && blob["data"] is not (null or "")
            && Protobuf.Sub(Protobuf.Decode(JsCompat.Bytes(blob["data"])), 1) is { } ws)
            cwd = UriToPath(Protobuf.Str(ws, 1));

        var session = Path.GetFileNameWithoutExtension(file);
        var calls = new List<Call>();
        foreach (var r in SqliteSource.Query(db, "select idx, metadata from steps where metadata is not null"))
        {
            var m = Protobuf.Decode(JsCompat.Bytes(r["metadata"]));
            var u = Protobuf.Sub(m, 9);
            if (u is null) continue;
            var at = Protobuf.Sub(m, 1);
            var ts = Protobuf.Int(at, 1) * 1000 + Math.Floor(Protobuf.Int(at, 2) / 1e6);
            if (ts == 0) continue;
            var req = Protobuf.Str(u, 11);
            if (req.Length == 0) req = $"{session}:{JsCompat.Text(r["idx"])}";
            calls.Add(new((long)ts, Protobuf.Int(u, 1), Protobuf.Int(u, 2), Protobuf.Int(u, 3), Protobuf.Int(u, 4),
                Protobuf.Int(u, 5), Protobuf.Int(u, 9), req, session, cwd));
        }
        return new(calls, names);
    }

    internal static string UriToPath(string uri)
    {
        if (!uri.StartsWith("file:///", StringComparison.Ordinal)) return uri;
        // decodeURIComponent throws on a malformed escape, which makes the whole conversation unreadable.
        var p = JsCompat.DecodeUriComponent(uri[8..]) ?? throw new FormatException("Malformed workspace URI.");
        return DriveLetter().IsMatch(p) ? p.Replace('/', '\\') : "/" + p;
    }

    [GeneratedRegex("^[a-zA-Z]:")]
    private static partial Regex DriveLetter();
}
