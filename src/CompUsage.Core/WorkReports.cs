using System.Globalization;

namespace CompUsage.Core;

public sealed record BreakPair(LockEvent Lock, LockEvent Unlock)
{
    public string Id => $"{Lock.RecordId}:{Lock.Timestamp.UtcTicks}:{Unlock.RecordId}:{Unlock.Timestamp.UtcTicks}";
    public TimeSpan Duration => Unlock.Timestamp - Lock.Timestamp;
}
public sealed record WorkInterval(DateOnly Date, DateTimeOffset From, DateTimeOffset To, bool Open = false)
{
    public TimeSpan Duration => To - From;
}
public sealed record LoggingGap(DateTimeOffset From, DateTimeOffset? To);
public sealed record WorkReport(DateOnly Date, IReadOnlyList<LockEvent> Events, IReadOnlyList<BreakPair> Pairs,
    LockEvent? First, LockEvent? Last, BreakPair? Lunch, bool ManualLunch, bool NoLunch,
    bool Ongoing, IReadOnlyList<string> Issues, IReadOnlyList<WorkInterval> Sections)
{
    public bool Incomplete => Issues.Count > 0;
    public string Status => Incomplete ? "Hiányos" : Ongoing ? "Folyamatban" : "Lezárt";
}

public static class WorkReports
{
    public static IReadOnlyList<WorkReport> Build(IEnumerable<LockEvent> events, AppSettings settings,
        IReadOnlyDictionary<string, string> lunches, DateTimeOffset now, TimeZoneInfo zone,
        IEnumerable<LoggingGap>? gaps = null)
    {
        settings.Validate();
        var today = WorkDayClock.GetDate(now, settings.DayStart, zone);
        var firstDate = today.AddDays(1 - settings.HistoryDays);
        return events.Where(e => e.Timestamp <= now && e.EventId is 4800 or 4801)
            .DistinctBy(e => (e.RecordId, e.Timestamp)).OrderBy(e => e.Timestamp).ThenBy(e => e.RecordId)
            .GroupBy(e => WorkDayClock.GetDate(e.Timestamp, settings.DayStart, zone))
            .Where(g => g.Key >= firstDate && g.Key <= today).OrderByDescending(g => g.Key)
            .Select(g => BuildDay(g.Key, g.ToArray(), settings, lunches, now, zone, gaps ?? [])).ToArray();
    }

    private static WorkReport BuildDay(DateOnly date, LockEvent[] list, AppSettings settings,
        IReadOnlyDictionary<string, string> lunches, DateTimeOffset now, TimeZoneInfo zone, IEnumerable<LoggingGap> gaps)
    {
        var ongoing = date == WorkDayClock.GetDate(now, settings.DayStart, zone);
        var issues = new List<string>();
        if (list[0].IsLock) issues.Add("Hiányzik a nap kezdő feloldása.");
        if (!ongoing && !list[^1].IsLock) issues.Add("Hiányzik a nap záró zárolása.");
        if (list.Select(e => e.SessionKey).Distinct().Count() > 1)
            issues.Add("Több Windows-munkamenet: a munkaszakaszok kézi ellenőrzést igényelnek.");
        if (list.Zip(list.Skip(1)).Any(p => p.First.IsLock == p.Second.IsLock))
            issues.Add("Az eseménysorban hiányzó vagy ismétlődő esemény van.");
        var start = WorkDayClock.At(date, settings.DayStart, zone);
        var end = ongoing ? now : WorkDayClock.At(date.AddDays(1), settings.DayStart, zone);
        if (gaps.Any(g => g.From < end && (g.To ?? now) > start))
            issues.Add("A nap egy részében a naplózás ki volt kapcsolva.");
        var pending = new Dictionary<string, LockEvent>();
        var pairs = new List<BreakPair>();
        foreach (var e in list)
        {
            if (e.IsLock) pending[e.SessionKey] = e;
            else if (pending.Remove(e.SessionKey, out var locked) && e.Timestamp > locked.Timestamp)
                pairs.Add(new BreakPair(locked, e));
        }
        var expected = WorkDayClock.At(settings.ExpectedLunch < settings.DayStart ? date.AddDays(1) : date,
            settings.ExpectedLunch, zone);
        var guess = pairs.Where(p => p.Duration.TotalMinutes >= settings.MinimumLunchMinutes && p.Duration.TotalMinutes <= 120
                && Math.Abs((p.Lock.Timestamp - expected).TotalMinutes) <= 120)
            .OrderBy(p => Math.Abs((p.Lock.Timestamp - expected).Ticks)).ThenBy(p => p.Lock.Timestamp).FirstOrDefault();
        var manual = lunches.TryGetValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var chosen);
        var lunch = manual ? pairs.Find(p => p.Id == chosen) : guess;
        if (manual && chosen != "none" && lunch is null) issues.Add("A korábban választott ebédpár már nem található; válaszd ki újra.");
        var first = list.FirstOrDefault(e => !e.IsLock);
        var last = list.LastOrDefault(e => e.IsLock);
        var sections = new List<WorkInterval>();
        if (issues.Count == 0 && first is not null)
        {
            var to = list[^1].IsLock ? list[^1].Timestamp : now;
            var from = first.Timestamp;
            foreach (var gap in pairs.Where(p => p.Lock.Timestamp >= from && p.Unlock.Timestamp <= to
                         && (p.Duration.TotalMinutes > settings.SplitBreakMinutes || p.Id == lunch?.Id)).OrderBy(p => p.Lock.Timestamp))
            {
                if (gap.Lock.Timestamp > from) sections.Add(new(date, from, gap.Lock.Timestamp));
                from = gap.Unlock.Timestamp;
            }
            if (to > from) sections.Add(new(date, from, to, ongoing && !list[^1].IsLock));
        }
        return new(date, list, pairs, first, last, lunch, manual, chosen == "none", ongoing, issues, sections);
    }
}

public static class Presentation
{
    public static DateTimeOffset RoundMinute(DateTimeOffset value) => new(
        ((value.UtcTicks + TimeSpan.TicksPerSecond * 30) / TimeSpan.TicksPerMinute) * TimeSpan.TicksPerMinute, TimeSpan.Zero);
    public static string Date(DateOnly date) => date.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
    public static string Time(DateTimeOffset value, TimeZoneInfo zone, bool seconds = false) =>
        TimeZoneInfo.ConvertTime(seconds ? value : RoundMinute(value), zone).ToString(seconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);
    public static string CopyRow(WorkInterval row, TimeZoneInfo zone) =>
        string.Join('\t', Date(row.Date), Time(row.From, zone, true), Time(row.To, zone, true));
}
