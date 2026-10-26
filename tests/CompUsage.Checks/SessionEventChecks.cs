using System.Globalization;
using CompUsage.Core;

internal static class SessionEventChecks
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time");
    private static readonly AppSettings Settings = new();
    private static DateTimeOffset At(string time) => WorkDayClock.At(new(2026, 10, 16), TimeOnly.Parse(time, CultureInfo.InvariantCulture), Zone);
    private static LockEvent User(long id, string time, int eventId) => new(id, At(time), eventId, "sid", "Test", "LOCAL", "1", "0x1");
    private static LockEvent[] Sleep(long id = 100, string from = "12:00", string to = "12:40") =>
        [new(id, At(from), LockEvent.Sleep, "", "", "", "", ""), new(id, At(to), LockEvent.Wake, "", "", "", "", "")];
    private static WorkReport Report(IEnumerable<LockEvent> events) => WorkReports.Build(events, Settings,
        new Dictionary<string, string>(), At("23:00").AddDays(1), Zone).Single();
    private static void Require(bool condition, string context)
    {
        if (!condition) throw new Exception(context);
    }

    public static void Run(Action<string, Action> check)
    {
        check("Suspend and native closes preserve exact endpoints and saved lunch choices in either order", () =>
        {
            foreach (var closeId in new[] { 4800, 4779, 4647 })
            foreach (var offset in new[] { -5L * TimeSpan.TicksPerSecond, -5_500_000L, 0L, 5_500_000L, 5L * TimeSpan.TicksPerSecond })
            foreach (var record in new[] { 2L, 100L, 102L })
            {
                var locked = User(record, "12:00", closeId) with { Timestamp = At("12:00").AddTicks(offset) };
                var unlocked = User(4, "12:55", 4801);
                LockEvent[] native = [User(1, "08:00", 4801), locked, unlocked, User(5, "17:00", 4800)];
                var choice = Report(native).Pairs.Single().Id;
                LockEvent[] entries = [..native, ..Sleep()];
                var day = WorkReports.Build(entries, Settings, new Dictionary<string, string> { ["2026-10-16"] = choice },
                    At("23:00").AddDays(1), Zone).Single();
                Require(!day.Incomplete && day.Sections.Count == 2, $"close={closeId}, suspend offset={offset}: incomplete day");
                Require(day.Lunch?.Id == choice && day.ManualLunch, "The native break-pair ID changed");
                Require(day.Sections[0].To == locked.Timestamp && day.Sections[1].From == unlocked.Timestamp, "Native endpoint precision was lost");
                Require(Presentation.CopyRow(day.Sections[0], Zone) == Presentation.CopyRow(Report(native).Sections[0], Zone), "Clipboard endpoint changed");
            }
        });
        check("Resume notifications do not repeat a native close before or after suspend", () =>
        {
            foreach (var closeId in new[] { 4800, 4779, 4647 })
            foreach (var initialOffset in new[] { -TimeSpan.TicksPerMinute, -5_500_000L, 0L, 5_500_000L })
            foreach (var resumeId in new[] { 4800, 4779, 4647 })
            foreach (var resumeOffset in new[] { -5L * TimeSpan.TicksPerSecond, -5_000_000L, 0L, 2L * TimeSpan.TicksPerSecond, TimeSpan.TicksPerMinute })
            {
                var locked = User(2, "12:00", closeId) with { Timestamp = At("12:00").AddTicks(initialOffset) };
                var resume = User(3, "12:40", resumeId) with { Timestamp = At("12:40").AddTicks(resumeOffset) };
                LockEvent[] entries = [User(1, "08:00", 4801), locked, ..Sleep(), resume, User(4, "12:55", 4801), User(5, "17:00", 4800)];
                var day = Report(entries);
                Require(!day.Incomplete && day.Sections.Count == 2,
                    $"close={closeId}@{initialOffset}, resume={resumeId}@{resumeOffset}: false incomplete day");
                Require(day.Pairs.Single().Lock == locked && day.Sections[1].From == At("12:55"), "Resume changed the break endpoints");
                Require(day.Events.Contains(resume), "A native event disappeared from the visible log");
            }
        });
        check("A resume lock before or simultaneous with wake works without a native suspend close", () =>
        {
            foreach (var record in new[] { 3L, 100L, 103L })
            foreach (var ticks in new[] { -5L * TimeSpan.TicksPerSecond, -1L, 0L, 1L, TimeSpan.TicksPerMinute })
            {
                var resume = User(record, "12:40", 4800) with { Timestamp = At("12:40").AddTicks(ticks) };
                LockEvent[] events = [User(1, "08:00", 4801), ..Sleep(), resume, User(104, "12:55", 4801), User(105, "17:00", 4800)];
                foreach (var ordered in new[] { events, events.Reverse().ToArray() })
                {
                    var day = Report(ordered);
                    Require(!day.Incomplete && day.Sections.Count == 2, $"Resume order changed the day: id={record}, ticks={ticks}");
                    Require(day.Sections[0].To == At("12:00") && day.Sections[1].From == At("12:55"), "Wake counted lock-screen time as work");
                }
            }
        });
        check("Suspend coalescing honors the five-second limit and preserves genuine repeated locks", () =>
        {
            foreach (var time in new[] { "12:00:05.0000001", "12:01", "12:39:54.9999999", "12:41:00.0000001" })
            {
                LockEvent[] entries = [User(1, "08:00", 4801), User(2, "11:59:59", 4800), ..Sleep(),
                    User(3, time, 4800), User(4, "12:55", 4801), User(5, "17:00", 4800)];
                Require(Report(entries).Incomplete, $"Unexplained repeated lock at {time} was hidden");
            }
            var afterSuspend = Report([User(1, "08:00", 4801), ..Sleep(), User(2, "12:00:05.0000001", 4800),
                User(4, "12:55", 4801), User(5, "17:00", 4800)]);
            Require(afterSuspend.Incomplete, "A late suspend lock was coalesced outside the five-second window");
        });
        check("A real unlock ends resume suppression, including immediately before wake", () =>
        {
            foreach (var unlockTime in new[] { "12:39:59", "12:40", "12:40:01" })
            foreach (var initialClose in new[] { false, true })
            {
                List<LockEvent> entries = [User(1, "08:00", 4801), ..Sleep(), User(3, unlockTime, 4801),
                    User(4, "12:40:02", 4800), User(5, "12:55", 4801), User(6, "17:00", 4800)];
                if (initialClose) entries.Add(User(2, "12:00:00.550", 4800));
                var day = Report(entries);
                Require(!day.Incomplete && day.Sections.Count == 3, $"Real post-unlock work lost: {unlockTime}, initial close={initialClose}");
                Require(day.Sections[1].From == At(unlockTime) && day.Sections[1].To == At("12:40:02"), "Real lock was treated as a resume duplicate");
            }
        });
        check("Suspend-disconnect-lock chains keep the native lock and subsequent resume duplicates stay suppressed", () =>
        {
            var locked = User(4, "12:00:00.550", 4800);
            var day = Report([User(1, "08:00", 4801), ..Sleep(), User(2, "12:00:00.100", 4779),
                User(3, "12:00:00.200", 4647), locked, User(5, "12:39:59.500", 4779),
                User(6, "12:40:02", 4800), User(7, "12:40:03", 4800), User(8, "12:55", 4801), User(9, "17:00", 4800)]);
            Require(!day.Incomplete && day.Pairs.Single().Lock == locked, "The native close chain lost its original endpoint");
        });
        check("Sleep context never coalesces events from another session, logon or account", () =>
        {
            foreach (var time in new[] { "12:00:00.550", "12:39:59.500", "12:40:02" })
            foreach (var foreign in new[] { User(3, time, 4800) with { SessionId = "2" },
                User(3, time, 4800) with { LogonId = "0x2" }, User(3, time, 4800) with { UserSid = "other" } })
            {
                LockEvent[] events = [User(1, "08:00", 4801), ..Sleep(), foreign, User(4, "12:55", 4801), User(5, "17:00", 4800)];
                Require(Report(events).Incomplete, "Cross-session ambiguity was hidden");
                Require(SessionEvents.WorkTransitions(events).Contains(foreign), "Another session's lock was removed");
            }
        });
        check("Wake alone opens an unlocked session, but never reopens a natively closed session", () =>
        {
            var plain = Report([User(1, "08:00", 4801), ..Sleep(), User(5, "17:00", 4800)]);
            Require(!plain.Incomplete && plain.Sections[1].From == At("12:40"), "A wake without lock-screen evidence did not resume work");
            var lateLock = Report([User(1, "08:00", 4801), ..Sleep(), User(3, "12:41:00.0000001", 4800),
                User(4, "12:55", 4801), User(5, "17:00", 4800)]);
            Require(!lateLock.Incomplete && lateLock.Sections.Count == 3, "A real lock beyond the resume window was suppressed");
            foreach (var time in new[] { "11:59:59", "12:00:00.550" })
            {
                var closed = Report([User(1, "08:00", 4801), User(2, time, 4800), ..Sleep()]);
                Require(!closed.Incomplete && closed.Sections.Count == 1 && closed.Last?.Timestamp == At(time), "Wake reopened a locked session");
            }
        });
        check("Separate sleep cycles and merged hibernation retain their own resume context", () =>
        {
            var day = Report([User(1, "08:00", 4801), ..Sleep(100, "10:00", "10:20"), User(2, "10:00:00.550", 4800),
                User(3, "10:20:02", 4800), User(4, "10:30", 4801), ..Sleep(101, "12:00", "12:10"), ..Sleep(102),
                User(5, "12:00:00.550", 4779), User(6, "12:39:59.500", 4800), User(7, "12:55", 4801), User(8, "17:00", 4800)]);
            Require(!day.Incomplete && day.Sections.Count == 3, "Power-cycle context leaked between periods");
            Require(day.Sections[1].From == At("10:30") && day.Sections[1].To == At("12:00:00.550"), "Merged sleep changed native endpoints");
        });
        check("Shutdown invalidates pending resume context before a new logon", () =>
        {
            var stop = new LockEvent(101, At("12:30"), LockEvent.SystemShutdown, "", "", "", "", "");
            var connected = User(4, "12:40", 4624) with { SessionId = "2", LogonId = "0x2" };
            var locked = connected with { RecordId = 5, Timestamp = At("17:00"), EventId = 4800 };
            var day = Report([User(1, "08:00", 4801), ..Sleep(), User(2, "12:00:00.550", 4800), stop, connected, locked]);
            Require(!day.Incomplete && day.Sections.Count == 2 && day.Sections[1].From == connected.Timestamp, "Shutdown left a stale resume context");
        });
        check("Monitor-recovered locks and last-input endpoints survive suspend normalization", () =>
        {
            var close = At("12:00:00.550"); var input = At("11:59:50");
            var day = WorkReports.Build([User(1, "08:00", 4801), ..Sleep(), User(3, "12:40:02", 4800),
                User(4, "12:55", 4801), User(5, "17:00", 4800)], Settings with { LockTimeSource = "activity" },
                new Dictionary<string, string>(), At("23:00").AddDays(1), Zone,
                activity: [new(close, input, At("12:00"), "1")], breaks: [new(close, At("12:55"), BreakReasons.Lunch)]).Single();
            Require(!day.Incomplete && day.Sections.Count == 2, "A recovered suspend lock made the day incomplete");
            Require(day.Sections[0].To == input && day.Pairs.Single().Lock.Timestamp == close, "The monitor's exact endpoint was lost");
        });
    }
}
