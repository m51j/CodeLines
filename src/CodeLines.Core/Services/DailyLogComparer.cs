using System.Globalization;
using System.Text;
using CodeLines.Core.Models;

namespace CodeLines.Core.Services;

public sealed record DailyLinesTotals(long Files, long Bytes, long Tokens, long PhysicalLines, long BlankLines, long CodeLines,
    long CommentLines, long ContentLines)
{
    public static DailyLinesTotals Sum(IEnumerable<DailyLinesEntry> entries)
    {
        var list = entries.ToList();
        return new(list.Sum(x => x.Files), list.Sum(x => x.Bytes), list.Sum(x => x.Tokens), list.Sum(x => x.PhysicalLines),
            list.Sum(x => x.BlankLines), list.Sum(x => x.CodeLines), list.Sum(x => x.CommentLines), list.Sum(x => x.ContentLines));
    }
}

/// <summary>A project's line counts as of a day: its scan on that day, or else its latest earlier scan.</summary>
public sealed record DailyEffectiveEntry(DailyLinesEntry Entry, string ScannedOn, bool IsCarried);

public sealed record DailyAiSummary(double Tokens, double Cost, bool CostPartial)
{
    public string CostText => DailyLogFormat.Currency(Cost) + (CostPartial ? "*" : "");
}

public sealed record DailyLogTimelineRow(string Date, int Projects, int CarriedProjects, DailyLinesTotals? Lines,
    DailyLinesTotals? Previous, DailyAiSummary? Ai, bool ScopeChanged)
{
    public long? Files => Lines?.Files;
    public long? PhysicalLines => Lines?.PhysicalLines;
    public long? CodeLines => Lines?.CodeLines;
    public long? Tokens => Lines?.Tokens;
    public long? DeltaLines => Lines is null || Previous is null ? null : Lines.PhysicalLines - Previous.PhysicalLines;
    public long? DeltaCode => Lines is null || Previous is null ? null : Lines.CodeLines - Previous.CodeLines;
    public long? DeltaTokens => Lines is null || Previous is null ? null : Lines.Tokens - Previous.Tokens;
    public string DeltaLinesText => DailyLogFormat.Signed(DeltaLines);
    public string DeltaCodeText => DailyLogFormat.Signed(DeltaCode);
    public string DeltaTokensText => DailyLogFormat.Signed(DeltaTokens);
    public double? AiTokens => Ai?.Tokens;
    public string AiCostText => Ai?.CostText ?? "—";

    public string Notes
    {
        get
        {
            var notes = new List<string>();
            if (Lines is null) notes.Add("Not scanned yet");
            else if (CarriedProjects == Projects) notes.Add("Not scanned this day; latest earlier scan shown");
            else if (CarriedProjects > 0) notes.Add($"{CarriedProjects} of {Projects} projects from earlier days");
            if (ScopeChanged) notes.Add("Counting scope or tokenizer changed");
            return string.Join(" • ", notes);
        }
    }
}

public sealed record DailyCompareRow(string Metric, double? A, double? B, bool IsCurrency = false)
{
    public double? Delta => A is { } a && B is { } b ? b - a : null;
    public double? DeltaPercent => A is { } a && B is { } b && a != 0 ? (b - a) / a : null;
    public string AText => IsCurrency ? DailyLogFormat.Currency(A) : DailyLogFormat.Number(A);
    public string BText => IsCurrency ? DailyLogFormat.Currency(B) : DailyLogFormat.Number(B);
    public string DeltaText => IsCurrency ? DailyLogFormat.SignedCurrency(Delta) : DailyLogFormat.Signed(Delta);
    public string DeltaPercentText => DailyLogFormat.Percent(DeltaPercent);
}

public sealed record DailyCompareLanguageRow(string Language, FileCategory Category, long LinesA, long LinesB, long CodeA, long CodeB)
{
    public long DeltaLines => LinesB - LinesA;
    public long DeltaCode => CodeB - CodeA;
    public string DeltaLinesText => DailyLogFormat.Signed(DeltaLines);
    public string DeltaCodeText => DailyLogFormat.Signed(DeltaCode);
}

public sealed record DailyComparison(IReadOnlyList<DailyCompareRow> Metrics, IReadOnlyList<DailyCompareLanguageRow> Languages, string Warning);

/// <summary>Reads the daily log back: a row per day, and a comparison of two days.</summary>
public static class DailyLogComparer
{
    /// <param name="projects">The projects to count; null = every project in the log.</param>
    public static IReadOnlyList<DailyEffectiveEntry> EffectiveLines(DailyLogFile log, string date, IReadOnlyCollection<Guid>? projects = null)
    {
        var latest = new Dictionary<Guid, DailyEffectiveEntry>();
        foreach (var day in log.Days.Where(d => string.CompareOrdinal(d.Date, date) <= 0).OrderBy(d => d.Date, StringComparer.Ordinal))
            foreach (var entry in day.Lines)
                if (projects is null || projects.Contains(entry.ProjectId))
                    latest[entry.ProjectId] = new(entry, day.Date, day.Date != date);
        return latest.Values.OrderBy(e => e.Entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One row per day that has a scan or AI usage, oldest first. Δ is against the previous row with line counts.</summary>
    /// <param name="projects">The projects to count; null = every project in the log.</param>
    /// <param name="allAgents">AI usage of the whole computer instead of only the sessions inside <paramref name="projects"/>.</param>
    public static IReadOnlyList<DailyLogTimelineRow> Timeline(DailyLogFile log, IReadOnlyCollection<Guid>? projects = null, bool allAgents = true)
    {
        var rows = new List<DailyLogTimelineRow>();
        var latest = new Dictionary<Guid, DailyLinesEntry>();
        DailyLinesTotals? previous = null;
        string? previousContract = null;
        foreach (var day in log.Days.OrderBy(d => d.Date, StringComparer.Ordinal))
        {
            var real = day.Lines.Where(e => projects is null || projects.Contains(e.ProjectId)).ToList();
            foreach (var entry in real) latest[entry.ProjectId] = entry;
            var ai = AiFor(day, projects, allAgents);
            if (real.Count == 0 && ai is not { Tokens: > 0 }) continue;

            var entries = latest.Values.ToList();
            var totals = entries.Count == 0 ? null : DailyLinesTotals.Sum(entries);
            var contract = entries.Count == 0 ? null : Contract(entries);
            var scopeChanged = contract is not null && (contract.Contains('+') || previousContract is not null && contract != previousContract);
            rows.Add(new(day.Date, entries.Count, entries.Count - real.Count, totals, previous, ai, scopeChanged));
            if (totals is null) continue;
            previous = totals;
            previousContract = contract;
        }
        return rows;
    }

    /// <param name="projects">The projects to count; null = every project in the log.</param>
    /// <param name="allAgents">AI usage of the whole computer instead of only the sessions inside <paramref name="projects"/>.</param>
    public static DailyComparison Compare(DailyLogFile log, string dateA, string dateB, IReadOnlyCollection<Guid>? projects = null, bool allAgents = true)
    {
        var a = EffectiveLines(log, dateA, projects);
        var b = EffectiveLines(log, dateB, projects);
        var ta = a.Count == 0 ? null : DailyLinesTotals.Sum(a.Select(x => x.Entry));
        var tb = b.Count == 0 ? null : DailyLinesTotals.Sum(b.Select(x => x.Entry));
        var aiA = AiFor(log.Days.FirstOrDefault(d => d.Date == dateA), projects, allAgents);
        var aiB = AiFor(log.Days.FirstOrDefault(d => d.Date == dateB), projects, allAgents);
        List<DailyCompareRow> metrics =
        [
            new("Files", ta?.Files, tb?.Files),
            new("Physical lines", ta?.PhysicalLines, tb?.PhysicalLines),
            new("Code lines", ta?.CodeLines, tb?.CodeLines),
            new("Comment lines", ta?.CommentLines, tb?.CommentLines),
            new("Blank lines", ta?.BlankLines, tb?.BlankLines),
            new("Content lines", ta?.ContentLines, tb?.ContentLines),
            new("Tokens", ta?.Tokens, tb?.Tokens),
            new("AI tokens (that day)", aiA?.Tokens, aiB?.Tokens),
            new("AI cost (that day)", aiA?.Cost, aiB?.Cost, IsCurrency: true)
        ];

        var langA = Languages(a);
        var langB = Languages(b);
        var languages = langA.Keys.Union(langB.Keys).Select(key =>
            {
                var la = langA.GetValueOrDefault(key);
                var lb = langB.GetValueOrDefault(key);
                return new DailyCompareLanguageRow(key.Language, key.Category, la.Lines, lb.Lines, la.Code, lb.Code);
            })
            .OrderByDescending(r => Math.Max(r.LinesA, r.LinesB)).ToList();

        var warnings = new List<string>();
        if (a.Concat(b).Select(x => ContractOf(x.Entry)).Distinct().Count() > 1)
            warnings.Add("The counting scope or tokenizer differs between these days, so line and token numbers are not directly comparable.");
        var carried = new[] { (Date: dateA, Entries: a), (Date: dateB, Entries: b) }.Where(x => x.Entries.Any(e => e.IsCarried)).Select(x => x.Date).ToList();
        if (carried.Count > 0)
            warnings.Add($"Some projects were not scanned on {string.Join(" / ", carried)}; their latest earlier scan is used.");
        return new(metrics, languages, string.Join(" ", warnings));
    }

    public static async Task ExportCsvAsync(IEnumerable<DailyLogTimelineRow> rows, string path, CancellationToken token = default)
    {
        var builder = new StringBuilder("Date,Projects,CarriedProjects,Files,PhysicalLines,DeltaPhysicalLines,CodeLines,DeltaCodeLines,CommentLines,BlankLines,ContentLines,Tokens,DeltaTokens,AiTokens,AiCost,AiCostPartial,Notes\r\n");
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            object?[] values = [row.Date, row.Projects, row.CarriedProjects, row.Files, row.PhysicalLines, row.DeltaLines, row.CodeLines, row.DeltaCode,
                row.Lines?.CommentLines, row.Lines?.BlankLines, row.Lines?.ContentLines, row.Tokens, row.DeltaTokens,
                row.Ai?.Tokens, row.Ai?.Cost, row.Ai?.CostPartial, row.Notes];
            builder.AppendLine(string.Join(',', values.Select(Csv)));
        }
        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(true), token);
    }

    private static DailyAiSummary? AiFor(DailyLogDay? day, IReadOnlyCollection<Guid>? projects, bool allAgents)
    {
        if (day?.Ai is not { } ai) return null;
        if (allAgents || projects is null) return new(ai.Total.Tokens, ai.Total.Cost, ai.Total.CostPartial);
        var matched = ai.Projects.Where(p => projects.Contains(p.ProjectId)).ToList();
        return new(matched.Sum(p => p.Tokens), matched.Sum(p => p.Cost), matched.Any(p => p.CostPartial));
    }

    private static Dictionary<(string Language, FileCategory Category), (long Lines, long Code)> Languages(IEnumerable<DailyEffectiveEntry> entries) =>
        entries.SelectMany(e => e.Entry.Languages).GroupBy(l => (l.Language, l.Category))
            .ToDictionary(g => g.Key, g => (g.Sum(l => l.PhysicalLines), g.Sum(l => l.CodeLines)));

    private static string ContractOf(DailyLinesEntry entry) => $"{entry.Scope}/{entry.Tokenizer.ToLowerInvariant()}";

    private static string Contract(IEnumerable<DailyLinesEntry> entries) =>
        string.Join('+', entries.Select(ContractOf).Distinct().Order(StringComparer.Ordinal));

    private static string Csv(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (value is string && text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return '"' + text.Replace("\"", "\"\"") + '"';
    }
}

public static class DailyLogFormat
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Number(double? value) => value is { } v ? v.ToString("N0", Culture) : "—";
    public static string Signed(double? value) => value is { } v ? v.ToString("+#,##0;-#,##0;0", Culture) : "—";
    public static string Currency(double? value) => value is { } v ? "$" + v.ToString("N2", Culture) : "—";
    public static string SignedCurrency(double? value) => value switch
    {
        null => "—",
        > 0 => "+$" + value.Value.ToString("N2", Culture),
        < 0 => "-$" + (-value.Value).ToString("N2", Culture),
        _ => "$0.00"
    };
    public static string Percent(double? value) => value is { } v ? v.ToString("+0.0%;-0.0%;0.0%", Culture) : "—";
}
