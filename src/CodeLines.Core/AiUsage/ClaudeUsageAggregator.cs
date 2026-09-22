namespace CodeLines.Core.AiUsage;

/// <summary>Token vector indexes of the Claude payload: IN, CW5M, CW1H, CR, OUT, REQ, THINK.</summary>
public static class ClaudeVector
{
    public const int In = 0, Cw5m = 1, Cw1h = 2, Cr = 3, Out = 4, Req = 5, Think = 6, Length = 7;

    public static long RealTokens(long[] v) => v[In] + v[Cw5m] + v[Cw1h] + v[Cr] + v[Out];

    internal static void Add(long[] target, ClaudeRecord r)
    {
        target[In] += r.Input;
        target[Cw5m] += r.CacheWrite5m;
        target[Cw1h] += r.CacheWrite1h;
        target[Cr] += r.CacheRead;
        target[Out] += r.Output;
        target[Req] += 1;
        target[Think] += r.Thinking;
    }
}

public sealed record ClaudeSessionRow(string Id, long Start, long End, long Requests, long Input, long CacheWrite5m,
    long CacheWrite1h, long CacheRead, long Output, string Cwd, string Branch, string TopModel);

public sealed class ClaudeUsageMeta
{
    public long GeneratedAt { get; init; }
    public string Tz { get; init; } = "local";
    public string TzLabel { get; init; } = "";
    public bool Dedupe { get; init; }
    public long RawRecords { get; init; }
    public long CountedRecords { get; init; }
    public long DuplicateRecords { get; init; }
    public long PartialRecords { get; init; }
    public double Inflation { get; init; }
    public long? FirstTs { get; init; }
    public long? LastTs { get; init; }
    public long Sessions { get; init; }
    public List<string> UnknownModels { get; init; } = [];
}

/// <summary>The payload report.html consumes (ccstats' aggregate() output).</summary>
public sealed class ClaudeUsagePayload
{
    public required ClaudeUsageMeta Meta { get; init; }
    public required Dictionary<string, (ModelRates Rates, string Label)> Rates { get; init; }
    public required List<string> Models { get; init; }
    /// <summary>day → hour → model → vector.</summary>
    public required Dictionary<string, Dictionary<int, Dictionary<string, long[]>>> Hours { get; init; }
    public required List<ClaudeSessionRow> Sessions { get; init; }
    public required Dictionary<string, long[]> AppBasis { get; init; }
}

/// <summary>Ports ccstats' src/aggregate.mjs: request-id deduplication and the hourly fact table.</summary>
public static class ClaudeUsageAggregator
{
    public static ClaudeUsagePayload Aggregate(IReadOnlyList<ClaudeRecord> records, UsageClock clock, bool dedupe = true,
        long? since = null, long? until = null, long? generatedAt = null)
    {
        var from = since ?? long.MinValue;
        var to = until ?? long.MaxValue;
        // One API response is written once per content block, and streaming writes early snapshots whose
        // output is still growing. They share a requestId; the copy with the most output is the billed one.
        var seen = new Dictionary<string, ClaudeRecord>(StringComparer.Ordinal);
        var kept = new List<ClaudeRecord>();
        var appBasis = new Dictionary<string, long[]>(StringComparer.Ordinal);
        long raw = 0, duplicates = 0, partials = 0;

        foreach (var r in records)
        {
            if (r.Ts < from || r.Ts > to) continue;
            // ccstats counts every record here, even outside the window, which overstates the inflation
            // whenever a window is set. Counting in-window records keeps "raw ÷ counted" honest.
            raw++;
            if (!appBasis.TryGetValue(r.Model, out var bucket)) appBasis[r.Model] = bucket = new long[ClaudeVector.Length];
            ClaudeVector.Add(bucket, r);

            if (dedupe && r.Request.Length > 0)
            {
                if (seen.TryGetValue(r.Request, out var previous))
                {
                    duplicates++;
                    if (!SameTokens(previous, r)) partials++;
                    if (r.Output > previous.Output) seen[r.Request] = r;
                    continue;
                }
                seen[r.Request] = r;
            }
            kept.Add(r);
        }
        // kept holds placeholders in arrival order; swap in each request's winner.
        for (var i = 0; i < kept.Count; i++)
            if (dedupe && kept[i].Request.Length > 0) kept[i] = seen[kept[i].Request];

        var hours = new Dictionary<string, Dictionary<int, Dictionary<string, long[]>>>(StringComparer.Ordinal);
        var sessions = new Dictionary<string, SessionState>(StringComparer.Ordinal);
        var models = new HashSet<string>(StringComparer.Ordinal);
        long firstTs = long.MaxValue, lastTs = long.MinValue;

        foreach (var r in kept)
        {
            var (day, hour) = clock.Parts(r.Ts);
            models.Add(r.Model);
            firstTs = Math.Min(firstTs, r.Ts);
            lastTs = Math.Max(lastTs, r.Ts);
            if (!hours.TryGetValue(day, out var byHour)) hours[day] = byHour = [];
            if (!byHour.TryGetValue(hour, out var byModel)) byHour[hour] = byModel = new(StringComparer.Ordinal);
            if (!byModel.TryGetValue(r.Model, out var vector)) byModel[r.Model] = vector = new long[ClaudeVector.Length];
            ClaudeVector.Add(vector, r);

            var sid = r.Session.Length > 0 ? r.Session : "unknown";
            if (!sessions.TryGetValue(sid, out var s))
                sessions[sid] = s = new SessionState { Id = sid, Start = r.Ts, End = r.Ts, Cwd = r.Cwd, Branch = r.Branch };
            if (r.Ts < s.Start) s.Start = r.Ts;
            if (r.Ts > s.End)
            {
                s.End = r.Ts;
                if (r.Cwd.Length > 0) s.Cwd = r.Cwd;
                if (r.Branch.Length > 0) s.Branch = r.Branch;
            }
            ClaudeVector.Add(s.Vector, r);
            s.Models[r.Model] = s.Models.GetValueOrDefault(r.Model) + r.Output + r.CacheRead;
        }

        var sortedModels = models.Sorted();
        var rates = new Dictionary<string, (ModelRates, string)>(StringComparer.Ordinal);
        var unknown = new List<string>();
        foreach (var m in sortedModels)
        {
            rates[m] = (ClaudePricing.RatesFor(m), ClaudePricing.DisplayModel(m));
            if (!ClaudePricing.IsKnownModel(m)) unknown.Add(m);
        }

        var sessionRows = sessions.Values.Select(s => new ClaudeSessionRow(s.Id, s.Start, s.End, s.Vector[ClaudeVector.Req],
                s.Vector[ClaudeVector.In], s.Vector[ClaudeVector.Cw5m], s.Vector[ClaudeVector.Cw1h], s.Vector[ClaudeVector.Cr],
                s.Vector[ClaudeVector.Out], s.Cwd, s.Branch, TopKey(s.Models)))
            .OrderBy(s => s.Start).ToList();

        return new ClaudeUsagePayload
        {
            Meta = new ClaudeUsageMeta
            {
                GeneratedAt = generatedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Tz = clock.Tz,
                TzLabel = clock.Label,
                Dedupe = dedupe,
                RawRecords = raw,
                CountedRecords = kept.Count,
                DuplicateRecords = duplicates,
                PartialRecords = partials,
                Inflation = kept.Count > 0 ? (double)raw / kept.Count : 0,
                FirstTs = kept.Count > 0 ? firstTs : null,
                LastTs = kept.Count > 0 ? lastTs : null,
                Sessions = sessionRows.Count,
                UnknownModels = unknown
            },
            Rates = rates,
            Models = sortedModels,
            Hours = hours,
            Sessions = sessionRows,
            AppBasis = appBasis
        };
    }

    /// <summary>The heaviest key; the first one seen wins ties, as with a JavaScript Map.</summary>
    internal static string TopKey<T>(Dictionary<string, T> weights) where T : System.Numerics.INumber<T>
    {
        var top = "";
        var first = true;
        var best = T.Zero;
        foreach (var (key, weight) in weights)
            if (first || weight > best) (best, top, first) = (weight, key, false);
        return top;
    }

    private static bool SameTokens(ClaudeRecord a, ClaudeRecord b) => a.Input == b.Input && a.CacheWrite5m == b.CacheWrite5m
        && a.CacheWrite1h == b.CacheWrite1h && a.CacheRead == b.CacheRead && a.Output == b.Output;

    private sealed class SessionState
    {
        public required string Id;
        public long Start, End;
        public required string Cwd, Branch;
        public readonly Dictionary<string, long> Models = new(StringComparer.Ordinal);
        public readonly long[] Vector = new long[ClaudeVector.Length];
    }
}
