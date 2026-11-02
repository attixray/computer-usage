using System.Globalization;
using CompUsage.Core;

internal static class MinimumWorkChecks
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time");
    private static DateTimeOffset At(string time) => WorkDayClock.At(new(2026, 10, 16), TimeOnly.Parse(time, CultureInfo.InvariantCulture), Zone);
    private static LockEvent Event(long id, string time, int type) => new(id, At(time), type, "sid", "Test", "LOCAL", "1", "0x1");
    private static WorkReport Report(IEnumerable<LockEvent> events, AppSettings? settings = null, DateTimeOffset? now = null) =>
        WorkReports.Build(events, settings ?? new(), new Dictionary<string, string>(), now ?? At("18:00").AddDays(1), Zone).Single();
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    public static void Run(Action<string, Action> check)
    {
        check("Minimum work defaults to six minutes, accepts zero and rejects invalid limits", () =>
        {
            Require(new AppSettings().MinimumWorkMinutes == 6, "Wrong minimum-work default");
            foreach (var value in new[] { 0, 6, 1440 }) (new AppSettings { MinimumWorkMinutes = value }).Validate();
            foreach (var value in new[] { -1, 1441 })
            {
                try { (new AppSettings { MinimumWorkMinutes = value }).Validate(); }
                catch (ArgumentException) { continue; }
                throw new Exception("Invalid minimum-work limit accepted");
            }
        });
        check("Minimum work uses exact seconds, preserves evidence and is reversible without joining separate sections", () =>
        {
            LockEvent[] events = [Event(1,"08:00",4801), Event(2,"08:05:59",4800),
                Event(3,"09:00",4801), Event(4,"09:06",4800), Event(5,"10:00",4801), Event(6,"10:06:01",4800)];
            var day = Report(events);
            Require(!day.Incomplete && day.Sections.Count == 2, "Short section was retained or exact six minutes were lost");
            Require(day.Sections[0].From == At("09:00") && day.Sections[1].From == At("10:00"), "Filtering bridged a real break");
            Require(WorkSummary.Worked(day) == TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(1), "Filtered work was counted");
            Require(day.Events.SequenceEqual(events) && day.Pairs.Count == 2 && day.First == events[0] && day.Last == events[^1], "Raw evidence changed");
            var all = Report(events, new() { MinimumWorkMinutes = 0 });
            Require(all.Sections.Count == 3 && all.Sections[1] == day.Sections[0], "Disabling filtering did not recover exact sections");
            Require(Presentation.CopyRow(day.Sections[1], Zone) == "2026.10.16\t10:00:00\t10:06:01", "Copy lost original seconds");
        });
        check("Minimum work is applied after merging short breaks and meetings, while lunch still splits", () =>
        {
            LockEvent[] shortBreak = [Event(1,"08:00",4801), Event(2,"08:02",4800), Event(3,"08:04",4801), Event(4,"08:06",4800)];
            Require(Report(shortBreak).Sections.Single().Duration == TimeSpan.FromMinutes(6), "Individual unlock intervals were filtered before merging");
            LockEvent[] meeting = [Event(1,"09:29",4801), Event(2,"09:30",4800), Event(3,"09:45",4801), Event(4,"09:46",4800)];
            var day = Report(meeting, new() { IgnoreDailyMeeting = true });
            Require(day.Meeting is not null && day.Sections.Single().Duration == TimeSpan.FromMinutes(17), "A meeting failed to keep the section together");
            var pair = Report(shortBreak).Pairs.Single();
            var lunch = WorkReports.Build(shortBreak, new(), new Dictionary<string, string> { ["2026-10-16"] = pair.Id },
                At("18:00").AddDays(1), Zone).Single();
            Require(lunch.Lunch is not null && lunch.Sections.Count == 0 && !lunch.Incomplete, "Lunch-separated short sections were combined or marked incomplete");
        });
        check("Minimum work follows the selected activity endpoint, including the new RDP default", () =>
        {
            LockEvent[] events = [Event(1,"08:00",4801), Event(2,"08:10",4800)];
            var sample = new ActivityLock(At("08:10"), At("08:05:59"), At("08:09:59"), "1", Remote: true);
            WorkReport Build(ActivityLock observation) => WorkReports.Build(events, new(), new Dictionary<string,string>(),
                At("18:00").AddDays(1), Zone, activity: [observation]).Single();
            Require(Build(sample).Sections.Count == 0, "The Windows timestamp bypassed the minimum on RDP");
            Require(Build(sample with { Remote = false }).Sections.Single().Duration == TimeSpan.FromMinutes(10), "Local lock time changed");
            Require(Build(sample with { LastInputAt = At("08:06") }).Sections.Single().Duration == TimeSpan.FromMinutes(6), "Exact threshold was excluded after activity adjustment");
        });
        check("Live sections stay provisional, short closed sections disappear and later merged work is restored", () =>
        {
            var opened = Event(1,"08:00",4801);
            var current = Report([opened], now: At("08:03"));
            Require(current.Ongoing && current.Sections.Single().Open, "Current work disappeared before it could reach the minimum");
            var locked = Event(2,"08:03",4800);
            Require(Report([opened, locked], now: At("08:04")).Sections.Count == 0, "Today's short closed section was retained");
            var resumed = Report([opened, locked, Event(3,"08:05",4801)], now: At("08:06"));
            Require(resumed.Sections.Single().From == opened.Timestamp && resumed.Sections[0].Open, "Filtering permanently discarded a section before a short break merged it");
        });
        check("Days with only filtered work do not become incomplete or add a daily goal", () =>
        {
            var day = Report([Event(1,"08:00",4801), Event(2,"08:03",4800)]);
            var settings = new AppSettings();
            var today = new DateOnly(2026,10,17);
            var week = WorkSummary.Weeks([day], settings, day.Date, today).Single();
            var month = WorkSummary.Months([day], settings, day.Date, today).Single();
            foreach (var period in new[] { week, month })
                Require(period.Days == 0 && period.IncompleteDays == 0 && period.Worked == TimeSpan.Zero && period.Expected == TimeSpan.Zero,
                    "An ignored short visit was counted as work, an incomplete day or a missed daily goal");
            var incomplete = day with { Date = day.Date.AddDays(-1), Issues = ["Missing event"] };
            Require(WorkSummary.Months([day, incomplete], settings, incomplete.Date, today).Single().IncompleteDays == 1,
                "Genuine incomplete days were hidden");
        });
    }
}
