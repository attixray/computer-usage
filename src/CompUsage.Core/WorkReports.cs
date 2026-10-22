using System.Globalization;

namespace CompUsage.Core;

public sealed record BreakPair(LockEvent Lock, LockEvent Unlock)
{
    public string Id => $"{Lock.RecordId}:{Lock.Timestamp.UtcTicks}:{Unlock.RecordId}:{Unlock.Timestamp.UtcTicks}";
    public TimeSpan Duration => Unlock.Timestamp - Lock.WorkTimestamp;
}
public sealed record WorkInterval(DateOnly Date, DateTimeOffset From, DateTimeOffset To, bool Open = false)
{
    private string comment = "";
    public string Id => string.Create(CultureInfo.InvariantCulture,$"{Date:yyyy-MM-dd}|{From.UtcTicks}");
    public string Comment
    {
        get => comment;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length > 32767) throw new ArgumentException("A megjegyzés legfeljebb 32767 karakter lehet.");
            try { System.Xml.XmlConvert.VerifyXmlChars(value); }
            catch (System.Xml.XmlException ex) { throw new ArgumentException("A megjegyzés nem megengedett karaktert tartalmaz.", ex); }
            comment = value;
        }
    }
    public TimeSpan Duration => To - From;
}
public sealed record LoggingGap(DateTimeOffset From, DateTimeOffset? To);
public sealed record WorkReport(DateOnly Date, IReadOnlyList<LockEvent> Events, IReadOnlyList<BreakPair> Pairs,
    LockEvent? First, LockEvent? Last, BreakPair? Lunch, bool ManualLunch, bool NoLunch,
    bool Ongoing, IReadOnlyList<string> Issues, IReadOnlyList<WorkInterval> Sections)
{
    public BreakPair? Meeting { get; init; }
    public bool ManualMeeting { get; init; }
    public bool NoMeeting { get; init; }
    public bool Incomplete => Issues.Count > 0;
    public string Status => Incomplete ? "Hiányos" : Ongoing ? "Folyamatban" : "Lezárt";
}

public static class WorkReports
{
    public static IReadOnlyList<WorkReport> Build(IEnumerable<LockEvent> events, AppSettings settings,
        IReadOnlyDictionary<string, string> lunches, DateTimeOffset now, TimeZoneInfo zone,
        IEnumerable<LoggingGap>? gaps = null, IEnumerable<ActivityLock>? activity = null,
        IReadOnlyDictionary<string, string>? meetings = null,
        IReadOnlyDictionary<string, string>? comments = null)
    {
        settings.Validate();
        var today = WorkDayClock.GetDate(now, settings.DayStart, zone);
        var firstDate = today.AddDays(1 - settings.HistoryDays);
        var meetingChoices = meetings ?? new Dictionary<string, string>();
        var notes = comments ?? new Dictionary<string, string>();
        var raw = SessionEvents.ResolveSessions(SessionEvents.MergeSleeps(events.Where(e => e.Timestamp <= now && e.IsWorkTransition)
            .DistinctBy(e => (e.EventId, e.RecordId, e.Timestamp))).OrderBy(e => e.Timestamp).ThenBy(e => e.RecordId));
        var transitions = SessionEvents.WorkTransitions(raw);
        // Every user record stays visible; machine-wide sleeps and shutdowns only when they affected the user's session.
        var originalDays = raw.Where(e => !e.IsSystemEvent).Concat(transitions.Where(e => e.IsSystemEvent))
            .OrderBy(e => e.Timestamp).ThenBy(e => e.RecordId)
            .GroupBy(e => WorkDayClock.GetDate(e.Timestamp, settings.DayStart, zone)).ToDictionary(g => g.Key, g => g.ToArray());
        return ActivityHistory.Apply(transitions, activity ?? [], settings, zone)
            .GroupBy(e => WorkDayClock.GetDate(e.Timestamp, settings.DayStart, zone))
            .Where(g => g.Key >= firstDate && g.Key <= today).OrderByDescending(g => g.Key)
            .Select(g => BuildDay(g.Key, g.ToArray(), originalDays[g.Key], settings, lunches, meetingChoices, now, zone, gaps ?? []))
            .Select(day => day with { Sections = day.Sections.Select(s => s with { Comment = notes.GetValueOrDefault(s.Id, "") }).ToArray() }).ToArray();
    }

    private static WorkReport BuildDay(DateOnly date, LockEvent[] list, LockEvent[] raw, AppSettings settings,
        IReadOnlyDictionary<string, string> lunches, IReadOnlyDictionary<string, string> meetings,
        DateTimeOffset now, TimeZoneInfo zone, IEnumerable<LoggingGap> gaps)
    {
        var ongoing = date == WorkDayClock.GetDate(now, settings.DayStart, zone);
        var issues = new List<string>();
        if (list[0].IsLock) issues.Add("Hiányzik a nap kezdő feloldása.");
        if (!ongoing && !list[^1].IsLock) issues.Add("Hiányzik a nap záró zárolása.");
        var sequential = SessionEvents.Sequential(list);
        if (!sequential)
            issues.Add("Több Windows-munkamenet: a munkaszakaszok kézi ellenőrzést igényelnek.");
        if (list.Zip(list.Skip(1)).Any(p => p.First.IsLock == p.Second.IsLock))
            issues.Add("Az eseménysorban hiányzó vagy ismétlődő esemény van.");
        var start = WorkDayClock.At(date, settings.DayStart, zone);
        var end = ongoing ? now : WorkDayClock.At(date.AddDays(1), settings.DayStart, zone);
        if (gaps.Any(g => g.From < end && (g.To ?? now) > start))
            issues.Add("A nap egy részében a naplózás ki volt kapcsolva.");
        // Consecutive sessions pair across a restart: the sign-out/sign-in gap is an ordinary break.
        string PairKey(LockEvent e) => sequential ? "" : e.SessionKey;
        var pending = new Dictionary<string, LockEvent>();
        var pairs = new List<BreakPair>();
        foreach (var e in list)
        {
            if (e.IsLock) pending[PairKey(e)] = e;
            else if (pending.Remove(PairKey(e), out var locked) && e.Timestamp > locked.Timestamp)
                pairs.Add(new BreakPair(locked, e));
        }
        var expected = WorkDayClock.At(settings.ExpectedLunch < settings.DayStart ? date.AddDays(1) : date,
            settings.ExpectedLunch, zone);
        var key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var manual = lunches.TryGetValue(key, out var chosen);
        var explicitLunch = manual ? pairs.Find(p => p.Id == chosen) : null;
        var manualMeeting = settings.IgnoreDailyMeeting && meetings.ContainsKey(key);
        var chosenMeeting = meetings.GetValueOrDefault(key);
        var meeting = !settings.IgnoreDailyMeeting ? null : manualMeeting
            ? pairs.Find(p => p.Id == chosenMeeting && p.Id != explicitLunch?.Id)
            : pairs.Where(p => p.Id != explicitLunch?.Id && p.Duration > TimeSpan.Zero
                    && p.Duration.TotalMinutes <= settings.MaximumMeetingMinutes && IsMeetingStart(p.Lock.WorkTimestamp, settings, zone))
                .OrderBy(p => MeetingDistance(p.Lock.WorkTimestamp, settings, zone)).ThenBy(p => p.Lock.WorkTimestamp).FirstOrDefault();
        if (manualMeeting && chosenMeeting != "none" && meeting is null)
            issues.Add("A korábban választott meetingpár már nem található vagy ebédként van kijelölve; válaszd ki újra.");
        var guess = pairs.Where(p => p.Id != meeting?.Id && p.Duration.TotalMinutes >= settings.MinimumLunchMinutes && p.Duration.TotalMinutes <= 120
                && Math.Abs((p.Lock.WorkTimestamp - expected).TotalMinutes) <= 120)
            .OrderBy(p => Math.Abs((p.Lock.WorkTimestamp - expected).Ticks)).ThenBy(p => p.Lock.WorkTimestamp).FirstOrDefault();
        var lunch = manual ? explicitLunch : guess;
        if (manual && chosen != "none" && lunch is null) issues.Add("A korábban választott ebédpár már nem található; válaszd ki újra.");
        var first = list.FirstOrDefault(e => !e.IsLock);
        var last = list.LastOrDefault(e => e.IsLock);
        var sections = new List<WorkInterval>();
        if (issues.Count == 0 && first is not null)
        {
            var to = list[^1].IsLock ? list[^1].WorkTimestamp : now;
            var from = first.Timestamp;
            foreach (var gap in pairs.Where(p => p.Lock.WorkTimestamp >= from && p.Unlock.Timestamp <= to
                         && (p.Id == lunch?.Id || p.Id != meeting?.Id && p.Duration.TotalMinutes > settings.SplitBreakMinutes)).OrderBy(p => p.Lock.WorkTimestamp))
            {
                if (gap.Lock.WorkTimestamp > from) sections.Add(new(date, from, gap.Lock.WorkTimestamp));
                from = gap.Unlock.Timestamp;
            }
            if (to > from) sections.Add(new(date, from, to, ongoing && !list[^1].IsLock));
        }
        return new(date, raw, pairs, first, last, lunch, manual, chosen == "none", ongoing, issues, sections)
        { Meeting = meeting, ManualMeeting = manualMeeting, NoMeeting = settings.IgnoreDailyMeeting && chosenMeeting == "none" };
    }

    private static bool IsMeetingStart(DateTimeOffset value, AppSettings settings, TimeZoneInfo zone)
    {
        var time = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime);
        return settings.MeetingStartFrom <= settings.MeetingStartUntil
            ? time >= settings.MeetingStartFrom && time <= settings.MeetingStartUntil
            : time >= settings.MeetingStartFrom || time <= settings.MeetingStartUntil;
    }

    private static long MeetingDistance(DateTimeOffset value, AppSettings settings, TimeZoneInfo zone)
    {
        // Use local clock times, including windows that cross midnight; ties prefer the earlier pair.
        var span = (settings.MeetingStartUntil.Ticks - settings.MeetingStartFrom.Ticks + TimeSpan.TicksPerDay) % TimeSpan.TicksPerDay;
        var offset = (TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime).Ticks
            - settings.MeetingStartFrom.Ticks + TimeSpan.TicksPerDay) % TimeSpan.TicksPerDay;
        return Math.Abs(offset - span / 2);
    }
}

public static class Presentation
{
    public static DateTimeOffset RoundMinute(DateTimeOffset value) => new(
        ((value.UtcTicks + TimeSpan.TicksPerSecond * 30) / TimeSpan.TicksPerMinute) * TimeSpan.TicksPerMinute, TimeSpan.Zero);
    public static string Date(DateOnly date) => date.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
    public static string Time(DateTimeOffset value, TimeZoneInfo zone, bool seconds = false) =>
        TimeZoneInfo.ConvertTime(value, zone).ToString(seconds ? "HH:mm:ss" : "H:mm", CultureInfo.InvariantCulture);
    public static string CopyRow(WorkInterval row, TimeZoneInfo zone) =>
        string.Join('\t', Date(row.Date), Time(row.From, zone, true), Time(row.To, zone, true));
}
