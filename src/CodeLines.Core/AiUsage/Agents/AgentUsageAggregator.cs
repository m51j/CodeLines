namespace CodeLines.Core.AiUsage.Agents;

/// <summary>Vector indexes of the agent payload: IN, CW, CR, OUT, REQ (model calls), THINK, COST, COSTTOK (priced tokens).</summary>
public static class AgentVector
{
    public const int In = 0, Cw = 1, Cr = 2, Out = 3, Req = 4, Think = 5, Cost = 6, CostTokens = 7, Length = 8;

    public static double RealTokens(double[] v) => v[In] + v[Cw] + v[Cr] + v[Out];

    internal static void Add(double[] v, AgentRecord r, double? cost)
    {
        v[In] += r.Input;
        v[Cw] += r.CacheWrite;
        v[Cr] += r.CacheRead;
        v[Out] += r.Output;
        v[Req] += r.Calls;
        v[Think] += r.Thinking;
        if (cost is not { } c) return;
        v[Cost] += c;
        v[CostTokens] += r.Input + r.CacheWrite + r.CacheRead + r.Output;
    }
}

public sealed record AgentSource(string Id, string Label, string? Note, IReadOnlyList<AgentRecord> Records);

public sealed record AgentSessionRow(string Agent, string Id, long Start, long End, string Cwd, string TopKey, double[] Vector);

public sealed class AgentUsageMeta
{
    public string Title { get; init; } = "";
    public long GeneratedAt { get; init; }
    public string Tz { get; init; } = "local";
    public string TzLabel { get; init; } = "";
    public long CountedRecords { get; init; }
    public long DroppedDuplicates { get; init; }
    public long? FirstTs { get; init; }
    public long? LastTs { get; init; }
    public long Sessions { get; init; }
}

/// <summary>The payload the agent pages consume (ccstats' aggregateAgents() output).</summary>
public sealed class AgentUsagePayload
{
    public const string Separator = "::";

    public required AgentUsageMeta Meta { get; init; }
    public required Dictionary<string, (string Label, string Note)> Agents { get; init; }
    public required List<string> Keys { get; init; }
    /// <summary>day → hour → "agent::model" → vector.</summary>
    public required Dictionary<string, Dictionary<int, Dictionary<string, double[]>>> Hours { get; init; }
    public required List<AgentSessionRow> Sessions { get; init; }

    public double[] Totals(Func<string, bool>? keyFilter = null, Func<string, bool>? filterDays = null)
    {
        var total = new double[AgentVector.Length];
        foreach (var (day, byHour) in Hours)
        {
            if (filterDays?.Invoke(day) == false) continue;
            foreach (var byKey in byHour.Values)
                foreach (var (key, v) in byKey)
                    if (keyFilter?.Invoke(key) != false)
                        for (var i = 0; i < AgentVector.Length; i++) total[i] += v[i];
        }
        return total;
    }

    public Dictionary<string, double[]> TotalsByAgent()
    {
        var result = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var byHour in Hours.Values)
            foreach (var byKey in byHour.Values)
                foreach (var (key, v) in byKey)
                {
                    var agent = key[..key.IndexOf(Separator, StringComparison.Ordinal)];
                    if (!result.TryGetValue(agent, out var t)) result[agent] = t = new double[AgentVector.Length];
                    for (var i = 0; i < AgentVector.Length; i++) t[i] += v[i];
                }
        return result;
    }
}

/// <summary>Ports ccstats' agents/aggregate.mjs.</summary>
public static class AgentUsageAggregator
{
    public static AgentUsagePayload Aggregate(IReadOnlyList<AgentSource> sources, string title, UsageClock clock,
        long? since = null, long? until = null, long? generatedAt = null)
    {
        var from = since ?? long.MinValue;
        var to = until ?? long.MaxValue;
        var hours = new Dictionary<string, Dictionary<int, Dictionary<string, double[]>>>(StringComparer.Ordinal);
        var sessions = new Dictionary<string, SessionState>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var agents = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        long firstTs = long.MaxValue, lastTs = long.MinValue, counted = 0, dropped = 0;

        foreach (var source in sources)
        {
            agents[source.Id] = (source.Label, source.Note ?? "");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in source.Records)
            {
                if (r.Ts < from || r.Ts > to) continue;
                if (r.Request.Length > 0 && !seen.Add(r.Request)) { dropped++; continue; }
                counted++;
                var key = source.Id + AgentUsagePayload.Separator + r.Model;
                keys.Add(key);
                var cost = CostOf(r);
                var (day, hour) = clock.Parts(r.Ts);
                firstTs = Math.Min(firstTs, r.Ts);
                lastTs = Math.Max(lastTs, r.Ts);
                if (!hours.TryGetValue(day, out var byHour)) hours[day] = byHour = [];
                if (!byHour.TryGetValue(hour, out var byKey)) byHour[hour] = byKey = new(StringComparer.Ordinal);
                if (!byKey.TryGetValue(key, out var vector)) byKey[key] = vector = new double[AgentVector.Length];
                AgentVector.Add(vector, r, cost);

                var sid = source.Id + AgentUsagePayload.Separator + (r.Session.Length > 0 ? r.Session : "unknown");
                if (!sessions.TryGetValue(sid, out var s))
                    sessions[sid] = s = new SessionState { Agent = source.Id, Id = r.Session, Start = r.Ts, End = r.Ts, Cwd = r.Cwd };
                if (r.Ts < s.Start) s.Start = r.Ts;
                if (r.Ts >= s.End)
                {
                    s.End = r.Ts;
                    if (r.Cwd.Length > 0) s.Cwd = r.Cwd;
                }
                AgentVector.Add(s.Vector, r, cost);
                s.Keys[key] = s.Keys.GetValueOrDefault(key) + r.Output + r.CacheRead + r.Input;
            }
        }

        var rows = sessions.Values
            .Select(s => new AgentSessionRow(s.Agent, s.Id, s.Start, s.End, s.Cwd, ClaudeUsageAggregator.TopKey(s.Keys), s.Vector))
            .OrderBy(s => s.Start).ToList();

        return new AgentUsagePayload
        {
            Meta = new AgentUsageMeta
            {
                Title = title,
                GeneratedAt = generatedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Tz = clock.Tz,
                TzLabel = clock.Label,
                CountedRecords = counted,
                DroppedDuplicates = dropped,
                FirstTs = counted > 0 ? firstTs : null,
                LastTs = counted > 0 ? lastTs : null,
                Sessions = rows.Count
            },
            Agents = agents,
            Keys = keys.Sorted(),
            Hours = hours,
            Sessions = rows
        };
    }

    /// <summary>Recorded cost, else list price, else null (unknown).</summary>
    public static double? CostOf(AgentRecord r)
    {
        if (r.Cost is { } cost) return cost;
        if (AgentPricing.For(r.Model) is not { } p) return null;
        return (r.Input * p.Input + r.CacheWrite * p.CacheWrite + r.CacheRead * p.CacheRead + r.Output * p.Output) / 1e6;
    }

    /// <summary>Claude Code joins the combined view with the same one-row-per-request rule and list-price cost as its own page.</summary>
    public static List<AgentRecord> FromClaudeRecords(IReadOnlyList<ClaudeRecord> records)
    {
        var byRequest = new Dictionary<string, ClaudeRecord>(StringComparer.Ordinal);
        var ordered = new List<ClaudeRecord>();
        foreach (var t in records)
        {
            if (t.Request.Length == 0) { ordered.Add(t); continue; }
            if (!byRequest.TryGetValue(t.Request, out var previous) || t.Output > previous.Output) byRequest[t.Request] = t;
        }
        ordered.AddRange(byRequest.Values);
        return ordered.Select(t => AgentRecord.Create(t.Ts, t.Model, t.Input, t.CacheWrite5m + t.CacheWrite1h, t.CacheRead, t.Output,
            t.Thinking, ClaudePricing.CostOf(t.Input, t.CacheWrite5m, t.CacheWrite1h, t.CacheRead, t.Output, t.Model),
            t.Session, t.Request, t.Cwd)).ToList();
    }

    private sealed class SessionState
    {
        public required string Agent, Id, Cwd;
        public long Start, End;
        public readonly Dictionary<string, double> Keys = new(StringComparer.Ordinal);
        public readonly double[] Vector = new double[AgentVector.Length];
    }
}
