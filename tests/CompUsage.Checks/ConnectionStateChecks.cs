using System.Globalization;
using CompUsage.Core;

internal static class ConnectionStateChecks
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time");
    private static DateTimeOffset At(string time) => WorkDayClock.At(new(2026, 11, 5), TimeOnly.Parse(time, CultureInfo.InvariantCulture), Zone);
    private static LockEvent Event(long id, string time, int type) => new(id, At(time), type, "sid", "Test", "LOCAL", "2", "0x1");
    private static WorkReport Report(IEnumerable<LockEvent> events, IEnumerable<ActivityOpen>? opens = null) =>
        WorkReports.Build(events, new() { MinimumWorkMinutes = 0 }, new Dictionary<string, string>(), At("12:00").AddDays(1), Zone, opens: opens).Single();
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static void Run(Action<string, Action> check)
    {
        check("A reconnect never starts work, for immediate, delayed and midnight unlocks", () =>
        {
            foreach (var delay in new[] { 0d, 1d, 5d, 7.2212845, 60d, 1800d })
            {
                var connection = Event(3,"23:37:02",4778);
                var unlock = Event(4,"23:37:02",4801) with { Timestamp = connection.Timestamp.AddSeconds(delay) };
                var close = Event(5,"03:14:25",4800) with { Timestamp = At("03:14:25").AddDays(1) };
                LockEvent[] events = [Event(1,"08:00",4801),Event(2,"18:00",4800),connection,unlock,close];
                foreach (var input in new[] { events, events.Reverse().ToArray(), events.Concat(events).ToArray() })
                {
                    var day = Report(input);
                    Require(!day.Incomplete && day.Sections.Count == 2, $"Reconnect delay {delay} still produced an incomplete day");
                    Require(day.Sections[1].From == unlock.Timestamp && day.Sections[1].To == close.Timestamp, "Connection or midnight changed the native endpoints");
                    Require(day.Events.Contains(connection) && day.Events.Contains(unlock), "Raw connection or unlock evidence was hidden");
                    Require(day.Pairs.Single().Unlock == unlock, "The reconnect replaced the actual unlock in a break pair");
                }
            }
        });
        check("A connection without any confirmed opening remains visible and cannot be exported as work", () =>
        {
            var connection = Event(1,"08:00",4778);
            foreach (var events in new[] { new[] { connection }, new[] { connection, Event(2,"17:00",4779) },
                new[] { connection, Event(2,"17:00",4800) } })
            {
                var day = Report(events);
                Require(day.Incomplete && day.Sections.Count == 0 && day.First is null, "Unknown connection-only history became measured work");
                Require(day.Issues.Contains("A csatlakozás után nem található igazolt feloldás.") && day.Events.Contains(connection), "The unverified connection disappeared silently");
            }
        });
        check("A verified unlocked desktop starts a connected session when Windows omitted the unlock", () =>
        {
            var connection = Event(1,"08:00",4778);
            var close = Event(2,"17:00",4800);
            var open = new ActivityOpen(At("08:02:13"),"2","0x1");
            var day = Report([connection,close], [open,open]);
            Require(!day.Incomplete && day.Sections.Single().From == open.ObservedAt, "Initial connection still needs a fabricated unlock");
            Require(day.First?.EventId == LockEvent.ObservedUnlock && day.Events.Count(e => e.EventId == LockEvent.ObservedUnlock) == 1, "An observation was duplicated or labelled as a native unlock");
            foreach (var invalid in new[] { open with { SessionId = "1" },open with { LogonId = "0x2" },open with { LogonId = "" },
                open with { ObservedAt = At("07:59") },open with { ObservedAt = At("08:00").AddDays(2) } })
                Require(Report([connection,close], [invalid]).Incomplete, "Foreign, pre-connection or future evidence opened work");
        });
        check("An unverified connection day remains reviewable when the actual unlock belongs to the next workday", () =>
        {
            var connection = Event(1,"23:00",4778);
            var unlock = Event(2,"08:00",4801) with { Timestamp = At("08:00").AddDays(1) };
            var days = WorkReports.Build([connection,unlock], new(), new Dictionary<string,string>(), At("12:00").AddDays(1), Zone);
            Require(days.Count == 2 && days.Single(d => d.Date == new DateOnly(2026,11,5)) is { Incomplete: true, First: null, Sections.Count: 0 },
                "A later day's unlock silently made connection-only history complete");
        });
        check("Returning to an unlocked but disconnected desktop starts at its verified availability", () =>
        {
            var disconnected = Event(2,"10:00",4779);
            var connection = Event(3,"11:00",4778);
            LockEvent[] events = [Event(1,"08:00",4801),disconnected,connection,Event(4,"17:00",4800)];
            Require(Report(events).Incomplete, "Reconnect alone resumed a disconnected session");
            var open = new ActivityOpen(At("11:00:02"),"2","0x1");
            var day = Report(events, [open]);
            Require(!day.Incomplete && day.Sections.Count == 2 && day.Sections[0].To == disconnected.Timestamp
                && day.Sections[1].From == open.ObservedAt, "Disconnected time was counted as work");
        });
        check("A delayed native lock while disconnected does not end work twice or move its end", () =>
        {
            var disconnect = Event(2,"10:00",4779);
            LockEvent[] events = [Event(1,"08:00",4801),disconnect,Event(3,"10:10",4800),Event(4,"11:00",4778),
                Event(5,"11:05",4801),Event(6,"17:00",4800)];
            var day = Report(events);
            Require(!day.Incomplete && day.Sections[0].To == disconnect.Timestamp && day.Sections[1].From == At("11:05"),
                "Disconnected lock time became another end or connection became the start");
            Require(day.Events.Any(e => e.RecordId == 3), "The delayed native lock was deleted");
        });
        check("Reconnects during already confirmed work do not create new starts or alter saved lunch identity", () =>
        {
            LockEvent[] events = [Event(1,"08:00",4801),Event(2,"11:30",4800),Event(3,"12:00",4801),Event(4,"17:00",4800)];
            var before = Report(events);
            var additions = new[] { Event(5,"09:00",4778),Event(6,"09:30",4778),Event(7,"11:40",4778) };
            var choices = new Dictionary<string,string> { ["2026-11-05"] = before.Pairs.Single().Id };
            var day = WorkReports.Build(events.Concat(additions), new(), choices, At("12:00").AddDays(1), Zone).Single();
            Require(!day.Incomplete && day.Lunch?.Id == before.Lunch?.Id && day.ManualLunch, "Connection context changed an existing lunch choice");
            Require(day.Sections.SequenceEqual(before.Sections), "A reconnect split or extended measured work");
        });
        check("Native unlocks and observer confirmations do not create duplicate openings in either order", () =>
        {
            var connection = Event(1,"08:00",4778);
            var native = Event(2,"08:01",4801);
            var close = Event(3,"17:00",4800);
            foreach (var offset in new[] { -20d, -5d, -1d, 0d, 1d, 20d })
            {
                var open = new ActivityOpen(native.Timestamp.AddSeconds(offset),"2","0x1");
                var day = Report([connection,native,close], [open,open]);
                Require(!day.Incomplete && day.Sections.Count == 1, "An observer and native unlock opened work twice");
                var expected = offset < -5 ? open.ObservedAt : native.Timestamp;
                Require(day.Sections[0].From == expected && day.Events.Contains(native), "Earlier verified work or the native event was lost");
            }
        });
        check("Connection evidence never opens a different account, session or logon and is never carried through shutdown", () =>
        {
            var connection = Event(1,"08:00",4778);
            var real = Event(2,"08:01",4801);
            foreach (var foreign in new[] { real with { UserSid = "other" }, real with { LogonId = "0x2" }, real with { SessionId = "1" } })
                Require(Report([connection,foreign,foreign with {RecordId=3,Timestamp=At("17:00"),EventId=4800}]).Incomplete,
                    "A foreign unlock confirmed another session's reconnect");
            foreach (var type in new[] { LockEvent.SystemShutdown,LockEvent.UnexpectedShutdown,4647 })
            {
                var stop = Event(3,"08:02",type);
                var day = Report([Event(1,"07:00",4801),Event(2,"07:30",4800),connection,stop,Event(4,"17:00",4800)],
                    [new(At("08:03"),"2","0x1")]);
                Require(day.Incomplete && day.Events.All(e => e.EventId != LockEvent.ObservedUnlock), "An old session was revived after it ended");
            }
        });
    }
}
