using System.Globalization;
using CodeLines.Core.AiUsage;
using CodeLines.Core.AiUsage.Agents;
using CodeLines.Core.Models;

namespace CodeLines.Core.Services;

/// <summary>Writes scans and AI usage into the daily log: one record per day, the last value of the day wins.</summary>
public static class DailyLogRecorder
{
    public static string DayKey(DateTimeOffset time) => time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Stores each scanned project under today's date, replacing an earlier scan of the same project today.</summary>
    public static DailyLogDay RecordLines(DailyLogFile log, IEnumerable<ProjectMetrics> scanned, CountingScope scope,
        string tokenizer, DateTimeOffset now)
    {
        var day = GetOrAddDay(log, DayKey(now));
        foreach (var project in scanned)
        {
            day.Lines.RemoveAll(x => x.ProjectId == project.ProjectId);
            day.Lines.Add(new DailyLinesEntry
            {
                ProjectId = project.ProjectId,
                Name = project.ProjectName,
                RootPath = project.RootPath,
                ScannedAt = now,
                Scope = scope,
                Tokenizer = tokenizer,
                Files = project.FileCount,
                Bytes = project.TotalBytes,
                Tokens = project.TotalTokens,
                PhysicalLines = project.PhysicalLines,
                BlankLines = project.BlankLines,
                CodeLines = project.CodeLines,
                CommentLines = project.CommentLines,
                ContentLines = project.ContentLines,
                Languages = project.Languages.Select(l => new DailyLanguageEntry(l.Language, l.Category, l.Files, l.Tokens,
                    l.PhysicalLines, l.CodeLines, l.CommentLines, l.ContentLines)).ToList()
            });
        }
        return day;
    }

    /// <summary>
    /// Stores the AI usage of every day in the payload. Today is always replaced; an earlier day only when the new
    /// numbers are not smaller, so agent logs that were deleted since do not erase what was recorded.
    /// </summary>
    /// <param name="roots">Project root folders, keyed by project id.</param>
    public static void RecordAi(DailyLogFile log, AgentUsagePayload payload, IReadOnlyDictionary<Guid, string> roots, DateTimeOffset now)
    {
        var clock = new UsageClock(payload.Meta.Tz == "utc");
        var today = clock.Parts(now.ToUnixTimeMilliseconds()).Day;
        var sessionsByDay = payload.Sessions.GroupBy(s => clock.Parts(s.Start).Day)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var (date, byHour) in payload.Hours)
        {
            var total = new double[AgentVector.Length];
            var byAgent = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (var byKey in byHour.Values)
                foreach (var (key, v) in byKey)
                {
                    var separator = key.IndexOf(AgentUsagePayload.Separator, StringComparison.Ordinal);
                    var agent = separator < 0 ? key : key[..separator];
                    if (!byAgent.TryGetValue(agent, out var a)) byAgent[agent] = a = new double[AgentVector.Length];
                    for (var i = 0; i < AgentVector.Length; i++) { total[i] += v[i]; a[i] += v[i]; }
                }

            var existing = log.Days.FirstOrDefault(d => d.Date == date)?.Ai;
            var amount = ToAmount(total);
            if (existing is not null && date != today && amount.Tokens < existing.Total.Tokens) continue;

            var projects = sessionsByDay.TryGetValue(date, out var sessions)
                ? AiProjectUsageMatcher.Match(sessions, roots).Where(p => p.Value.Sessions > 0)
                    .Select(p => new DailyAiProjectEntry(p.Key, p.Value.Sessions, p.Value.Tokens, p.Value.Cost, p.Value.CostPartial)).ToList()
                : [];
            GetOrAddDay(log, date).Ai = new DailyAiUsage
            {
                UpdatedAt = now,
                Tz = clock.Tz,
                Total = amount,
                ByAgent = byAgent.ToDictionary(p => p.Key, p => ToAmount(p.Value), StringComparer.Ordinal),
                Projects = projects
            };
        }
    }

    private static DailyAiAmount ToAmount(double[] v) => new(v[AgentVector.In], v[AgentVector.Cw], v[AgentVector.Cr], v[AgentVector.Out],
        v[AgentVector.Req], v[AgentVector.Cost], v[AgentVector.CostTokens] < AgentVector.RealTokens(v) * 0.999);

    private static DailyLogDay GetOrAddDay(DailyLogFile log, string date)
    {
        var index = log.Days.FindIndex(d => d.Date == date);
        if (index >= 0) return log.Days[index];
        var day = new DailyLogDay { Date = date };
        // Keep the days sorted, oldest first.
        var at = log.Days.FindIndex(d => string.CompareOrdinal(d.Date, date) > 0);
        log.Days.Insert(at < 0 ? log.Days.Count : at, day);
        return day;
    }
}
