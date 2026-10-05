namespace KidGuard.Core;

/// <summary>Вычисления по недельному расписанию во времени часового пояса службы.</summary>
public static class ScheduleCalculator
{
    /// <summary>
    /// Конец непрерывного разрешённого окна, в которое попадает <paramref name="local"/>,
    /// или null, если сейчас время не разрешено. Смежные интервалы (в том числе через полночь) склеиваются.
    /// </summary>
    public static DateTime? AllowedUntil(IReadOnlyDictionary<string, List<TimeRange>> schedule, DateTime local)
    {
        var day = local.Date;
        var minute = (int)(local.TimeOfDay.Ticks / TimeSpan.TicksPerMinute);
        if (Find(schedule, day, r => r.Contains(minute)) is not { } current) return null;

        var end = current.EndMinute;
        // Ограничение итераций: не больше трёх интервалов в день, т.е. примерно три недели вперёд.
        for (var guard = 0; guard < 64; guard++)
        {
            if (end >= TimeRange.MinutesPerDay)
            {
                day = day.AddDays(1);
                end = 0;
            }
            var startsAt = end;
            if (Find(schedule, day, r => r.StartMinute == startsAt) is not { } next) break;
            end = next.EndMinute;
        }
        return day.AddMinutes(end);
    }

    public static IReadOnlyList<TimeRange> ForDay(IReadOnlyDictionary<string, List<TimeRange>> schedule, DayOfWeek day) =>
        schedule.TryGetValue(Days.Key(day), out var list) ? list : [];

    static TimeRange? Find(IReadOnlyDictionary<string, List<TimeRange>> schedule, DateTime day, Func<TimeRange, bool> predicate)
    {
        foreach (var range in ForDay(schedule, day.DayOfWeek))
        {
            if (predicate(range)) return range;
        }
        return null;
    }
}

/// <summary>Перевод между UTC и часовым поясом службы.</summary>
public static class Tz
{
    public static DateTime ToLocal(DateTimeOffset utc, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(utc, zone).DateTime;

    /// <summary>
    /// Местное время → UTC. Несуществующее время (переход на летнее) сдвигается вперёд,
    /// для неоднозначного берётся стандартное смещение.
    /// </summary>
    public static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var guard = 0; guard < 8 && zone.IsInvalidTime(local); guard++)
        {
            local = local.AddMinutes(30);
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
