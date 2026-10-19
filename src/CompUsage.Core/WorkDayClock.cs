namespace CompUsage.Core;

public static class WorkDayClock
{
    public static DateOnly GetDate(DateTimeOffset instant, TimeOnly dayStart, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var date = DateOnly.FromDateTime(local.DateTime);
        return TimeOnly.FromDateTime(local.DateTime) < dayStart ? date.AddDays(-1) : date;
    }

    public static DateTimeOffset At(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        // A spring-forward gap has no real instant. Use the first valid time after it.
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}
