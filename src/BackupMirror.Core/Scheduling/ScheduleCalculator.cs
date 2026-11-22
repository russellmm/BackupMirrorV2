using BackupMirror.Core.Models;

namespace BackupMirror.Core.Scheduling;

/// <summary>Pure local-time schedule maths (times of day on selected weekdays).</summary>
public static class ScheduleCalculator
{
    /// <summary>Most recent occurrence at or before <paramref name="localNow"/> within the last 8 days.</summary>
    public static DateTime? PreviousOccurrence(JobSchedule schedule, DateTime localNow)
    {
        if (schedule.Times.Count == 0) return null;
        for (var dayOffset = 0; dayOffset <= 7; dayOffset++)
        {
            var date = DateOnly.FromDateTime(localNow).AddDays(-dayOffset);
            if (!AppliesOn(schedule, date.DayOfWeek)) continue;
            var best = schedule.Times
                .Select(t => date.ToDateTime(t))
                .Where(dt => dt <= localNow)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            if (best != DateTime.MinValue) return best;
        }

        return null;
    }

    /// <summary>Next occurrence strictly after <paramref name="localNow"/>.</summary>
    public static DateTime? NextOccurrence(JobSchedule schedule, DateTime localNow)
    {
        if (schedule.Times.Count == 0) return null;
        for (var dayOffset = 0; dayOffset <= 7; dayOffset++)
        {
            var date = DateOnly.FromDateTime(localNow).AddDays(dayOffset);
            if (!AppliesOn(schedule, date.DayOfWeek)) continue;
            var best = schedule.Times
                .Select(t => date.ToDateTime(t))
                .Where(dt => dt > localNow)
                .DefaultIfEmpty(DateTime.MaxValue)
                .Min();
            if (best != DateTime.MaxValue) return best;
        }

        return null;
    }

    /// <summary>
    /// Whether a scheduled job is due: an occurrence passed that has not been run yet.
    /// Missed occurrences (service was off) are caught up once, if no older than <paramref name="maxCatchUp"/>.
    /// </summary>
    public static bool IsDue(JobSchedule schedule, DateTime localNow, DateTimeOffset? lastFiredUtc, TimeSpan maxCatchUp)
    {
        var prev = PreviousOccurrence(schedule, localNow);
        if (prev is null) return false;
        if (localNow - prev.Value > maxCatchUp) return false;
        var prevUtc = new DateTimeOffset(prev.Value, TimeZoneInfo.Local.GetUtcOffset(prev.Value)).ToUniversalTime();
        return lastFiredUtc is null || lastFiredUtc.Value < prevUtc;
    }

    private static bool AppliesOn(JobSchedule schedule, DayOfWeek day)
        => schedule.Days.Count == 0 || schedule.Days.Contains(day);
}
