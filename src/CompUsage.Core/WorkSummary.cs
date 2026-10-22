using System.Globalization;

namespace CompUsage.Core;

// Totals for a week or month. Only complete days with work count towards the goal; incomplete days are listed separately.
public sealed record SummaryPeriod(DateOnly From, DateOnly To, int Days, int IncompleteDays, TimeSpan Worked, TimeSpan Expected,
    IReadOnlyDictionary<string, TimeSpan> Reasons, TimeSpan Unanswered, bool Partial, bool Ongoing)
{
    public TimeSpan Balance => Worked - Expected;
}

public static class WorkSummary
{
    public static TimeSpan Worked(WorkReport day) => day.Sections.Aggregate(TimeSpan.Zero, (total, s) => total + s.Duration);

    // h:mm rounded to the nearest minute, with a sign for negative balances.
    public static string Clock(TimeSpan value)
    {
        var minutes = (long)Math.Round(Math.Abs(value.TotalMinutes), MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{(value < TimeSpan.Zero && minutes > 0 ? "-" : "")}{minutes / 60}:{minutes % 60:00}");
    }

    public static DateOnly WeekStart(DateOnly date, DayOfWeek first) => date.AddDays(-(((int)date.DayOfWeek - (int)first + 7) % 7));

    public static IReadOnlyList<SummaryPeriod> Weeks(IReadOnlyList<WorkReport> days, AppSettings settings, DateOnly firstAvailable, DateOnly today) =>
        days.Select(d => WeekStart(d.Date, settings.WeekStart)).Distinct().OrderDescending()
            .Select(start => Period(days, start, start.AddDays(6), settings, firstAvailable, today)).ToArray();

    public static IReadOnlyList<SummaryPeriod> Months(IReadOnlyList<WorkReport> days, AppSettings settings, DateOnly firstAvailable, DateOnly today) =>
        days.Select(d => new DateOnly(d.Date.Year, d.Date.Month, 1)).Distinct().OrderDescending()
            .Select(start => Period(days, start, start.AddMonths(1).AddDays(-1), settings, firstAvailable, today)).ToArray();

    private static SummaryPeriod Period(IReadOnlyList<WorkReport> days, DateOnly from, DateOnly to, AppSettings settings,
        DateOnly firstAvailable, DateOnly today)
    {
        var inRange = days.Where(d => d.Date >= from && d.Date <= to).ToArray();
        var complete = inRange.Where(d => !d.Incomplete && d.Sections.Count > 0).ToArray();
        var reasons = BreakReasons.Codes.ToDictionary(c => c, _ => TimeSpan.Zero);
        var unanswered = TimeSpan.Zero;
        foreach (var day in complete)
        {
            foreach (var pair in day.Pairs)
            {
                // The day's lunch and daily meeting count as such even when they were chosen without an answer.
                var reason = pair.Id == day.Lunch?.Id ? BreakReasons.Lunch : pair.Reason ?? (pair.Id == day.Meeting?.Id ? BreakReasons.Standup : null);
                if (reason is not null) reasons[reason] += pair.Duration;
                else if (pair.Duration.TotalMinutes > settings.SplitBreakMinutes) unanswered += pair.Duration;
            }
        }
        var worked = complete.Aggregate(TimeSpan.Zero, (total, d) => total + Worked(d));
        return new(from, to, complete.Length, inRange.Length - complete.Length, worked,
            TimeSpan.FromMinutes(settings.DailyGoalMinutes) * complete.Length, reasons, unanswered, from < firstAvailable, today >= from && today <= to);
    }
}
