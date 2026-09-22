using System.Globalization;

namespace CodeLines.Core.AiUsage;

/// <summary>Day/hour bucketing in local time or UTC, the way the ccstats aggregators do it.</summary>
public sealed class UsageClock(bool utc, TimeZoneInfo? local = null)
{
    private readonly TimeZoneInfo _zone = utc ? TimeZoneInfo.Utc : local ?? TimeZoneInfo.Local;

    public bool Utc => utc;
    public string Tz => utc ? "utc" : "local";
    public TimeZoneInfo Zone => _zone;

    public string Label => utc ? "UTC" : $"local (UTC{FormatOffset(_zone.GetUtcOffset(DateTime.UtcNow))})";

    public (string Day, int Hour) Parts(long ts)
    {
        var time = ToZone(ts);
        return (time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), time.Hour);
    }

    public DateTimeOffset ToZone(long ts) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ts), _zone);

    /// <summary>ccstats' windowFor(): --since/--until/--days relative to the newest record.</summary>
    public (long? Since, long? Until) Window(AiUsageOptions options, long lastTs)
    {
        long? since = options.Since is { } s ? new DateTimeOffset(s.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds() : null;
        long? until = options.Until is { } u ? new DateTimeOffset(u.ToDateTime(new TimeOnly(23, 59, 59, 999)), TimeSpan.Zero).ToUnixTimeMilliseconds() : null;
        if (options.Days > 0)
        {
            // setHours(0,0,0,0) and setDate() in JavaScript always use the machine's local time.
            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(lastTs > 0 ? lastTs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), LocalZone);
            var midnight = local.Date.AddDays(-(options.Days - 1));
            var start = new DateTimeOffset(midnight, LocalZone.GetUtcOffset(midnight)).ToUnixTimeMilliseconds();
            since = Math.Max(since ?? long.MinValue, start);
        }
        return (since, until);
    }

    private TimeZoneInfo LocalZone => local ?? TimeZoneInfo.Local;

    private static string FormatOffset(TimeSpan offset)
    {
        var minutes = (int)offset.TotalMinutes;
        var a = Math.Abs(minutes);
        return $"{(minutes < 0 ? '-' : '+')}{a / 60:00}:{a % 60:00}";
    }
}
