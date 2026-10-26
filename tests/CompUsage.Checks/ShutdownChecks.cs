using System.Globalization;
using CompUsage.Core;

internal static class ShutdownChecks
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time");
    private static DateTimeOffset At(string time) => WorkDayClock.At(new(2026, 10, 26), TimeOnly.Parse(time, CultureInfo.InvariantCulture), Zone);
    private static LockEvent User(long id, string time, int type, string session = "2", string logon = "0x1") =>
        new(id, At(time), type, "sid", "Test", "LOCAL", session, logon);
    private static readonly LockEvent Crash = new(100, At("16:18:24.428"), LockEvent.UnexpectedShutdown, "", "", "", "", "")
        { RecordedAt = At("16:26:42.749") };
    private static readonly ActivityHeartbeat Alive = new("2", "0x1", At("10:00"), At("16:26:16.704"));
    private static LockEvent[] Day() => [User(1,"08:00",4801), Crash,
        User(2,"16:26:57.268",4624,"1","0x2"), User(3,"17:00",4800,"1","0x2")];
    private static WorkReport Report(IEnumerable<LockEvent> events, IEnumerable<ActivityHeartbeat> heartbeats) =>
        WorkReports.Build(events, new AppSettings { SplitBreakMinutes = 5 }, new Dictionary<string,string>(),
            At("18:00"), Zone, heartbeats: heartbeats).Single();
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    public static void Run(Action<string, Action> check)
    {
        check("A persisted pre-crash heartbeat refines last-alive time and prevents false break deductions", () =>
        {
            var original = Report(Day(), []);
            var fixedDay = Report(Day(), [Alive]);
            Require(original.Sections.Count == 2, "Fixture must expose a false long break");
            Require(!fixedDay.Incomplete && fixedDay.Sections.Count == 1, "The corrected short interruption split the workday");
            Require(fixedDay.Pairs.Single().Lock.Timestamp == Alive.HeartbeatAt, "The freshest heartbeat was not used");
            Require(fixedDay.Pairs.Single().Duration == At("16:26:57.268") - Alive.HeartbeatAt, "Incorrect corrected gap");
            Require(fixedDay.Pairs.Single().Lock.Label.Contains("becsült"), "The estimated nature of the shutdown was lost");
            Require(WorkSummary.Worked(fixedDay) == TimeSpan.FromHours(9), "Incorrect worked duration");
            Require(Crash.Timestamp == At("16:18:24.428"), "The raw cached record was mutated");
        });
        check("Shutdown refinement rejects stale, post-boot, future and foreign monitor observations", () =>
        {
            foreach (var invalid in new[] {
                Alive with { SessionId = "1" }, Alive with { LogonId = "0x2" },
                Alive with { HeartbeatAt = At("16:17") }, Alive with { HeartbeatAt = Crash.RecordedAt!.Value },
                Alive with { HeartbeatAt = At("16:27") }, Alive with { HeartbeatAt = At("19:00") },
                Alive with { StartedAt = At("16:26:30"), HeartbeatAt = At("16:26:35") },
                Alive with { StartedAt = default } })
                Require(Report(Day(), [invalid]).Pairs.Single().Lock.Timestamp == Crash.Timestamp, $"Accepted invalid heartbeat: {invalid}");
            Require(Report(Day(), [Alive with { HeartbeatAt = At("16:25") }, Alive]).Pairs.Single().Lock.Timestamp == Alive.HeartbeatAt,
                "Did not choose the newest valid observation");
        });
        check("Legacy heartbeat evidence is accepted only within an unambiguous session lifetime", () =>
        {
            var legacy = Alive with { LogonId = "" };
            Require(Report(Day(), [legacy]).Pairs.Single().Lock.Timestamp == Alive.HeartbeatAt, "Legacy observation was ignored");
            var changed = Day();
            changed[2] = User(2,"16:26:10",4624,"2","0x2");
            var refined = ActivityHistory.RefineShutdowns(changed, [legacy], At("18:00"));
            Require(refined.Single(e => e.EventId == LockEvent.UnexpectedShutdown).Timestamp == Crash.Timestamp,
                "An observation crossed a reused numeric session ID");
            changed[2] = User(2,"16:26:10",4624,"1","0x2");
            refined = ActivityHistory.RefineShutdowns(changed, [Alive], At("18:00"));
            Require(refined.Single(e => e.EventId == LockEvent.UnexpectedShutdown).Timestamp == Crash.Timestamp,
                "An observation crossed another session's start");
        });
        check("Refinement occurs before transition validation and does not extend already locked work", () =>
        {
            var day = Report(Day().Concat([User(4,"16:20",4800),User(5,"16:21",4801)]), [Alive]);
            Require(!day.Incomplete && day.Sections.Count == 1, "A stale crash estimate disrupted genuine later events");
            day = Report(Day().Append(User(4,"16:15",4800)), [Alive]);
            Require(!day.Incomplete && day.Sections.Count == 2 && day.Sections[0].To == At("16:15"),
                "Machine liveness extended a locked work interval");
            Require(day.Events.All(e => e.EventId != LockEvent.UnexpectedShutdown), "An irrelevant shutdown remained visible");
        });
        check("Fresh shutdown metadata upgrades old caches independently of merge order", () =>
        {
            var legacy = Crash with { RecordedAt = null };
            Require(Report(Day().Select(e => e == Crash ? legacy : e), [Alive]).Pairs.Single().Lock.Timestamp == Crash.Timestamp,
                "An unbounded old shutdown estimate used heartbeat evidence");
            foreach (var duplicates in new[] { new[] { legacy, Crash }, new[] { Crash, legacy } })
            {
                var merged = SessionEvents.MergeRecords(duplicates);
                Require(merged.Count == 1 && merged[0].RecordedAt == Crash.RecordedAt, "Fresh metadata was discarded");
                var day = Report(Day().Where(e => e != Crash).Concat(duplicates), [Alive]);
                Require(!day.Incomplete && day.Pairs.Single().Lock.Timestamp == Alive.HeartbeatAt, "Duplicate cached records broke refinement");
            }
        });
        check("Multiple crashes keep separate pre-boot bounds and never synthesize a shutdown", () =>
        {
            var later = Crash with { RecordId = 101, Timestamp = At("17:10"), RecordedAt = At("17:30") };
            var events = Day().Concat([later]).ToArray();
            var laterRun = new ActivityHeartbeat("1","0x2",At("16:28"),At("17:25"));
            var refined = ActivityHistory.RefineShutdowns(events, [Alive,laterRun], At("18:00"));
            Require(refined.Single(e => e.RecordId == 100).Timestamp == Alive.HeartbeatAt, "A later run moved the first crash");
            Require(refined.Single(e => e.RecordId == 101).Timestamp == laterRun.HeartbeatAt, "The second crash was not refined");
            Require(ActivityHistory.RefineShutdowns([User(1,"08:00",4801)], [Alive], At("18:00")).Count == 1,
                "A heartbeat by itself synthesized a shutdown");
        });
        check("Archived monitor runs survive numeric-session reuse and prune evidence older than history", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "CompUsage-ShutdownChecks-" + Guid.NewGuid().ToString("N"));
            try
            {
                var journal = new ActivityJournal(directory);
                var old = new ActivitySession { SessionId="2", LogonId="0x1", StartedAt=Alive.StartedAt, HeartbeatAt=Alive.HeartbeatAt };
                journal.ArchiveRun(old, At("16:28"));
                journal.ArchiveRun(old, At("16:28"));
                var current = new ActivitySession { SessionId="2", LogonId="0x2", StartedAt=At("16:28") };
                journal.Heartbeat(current, At("16:29"));
                var evidence = new ActivityJournal(directory).Heartbeats();
                Require(evidence.Count(h => h.LogonId == "0x1") == 1 && evidence.Contains(Alive), "Old heartbeat was overwritten or duplicated");
                Require(journal.Load("2").LogonId == "0x2", "Current logon ID was not persisted");
                journal.SaveEvents("2", [Crash with { RecordedAt=null }], At("16:29"));
                journal.SaveEvents("2", [Crash], At("16:30"));
                Require(journal.Events().Single().RecordedAt == Crash.RecordedAt, "The journal did not enrich cached metadata");
                journal.ArchiveRun(current, At("16:29").AddDays(62));
                Require(journal.Heartbeats().All(h => h.LogonId != "0x1"), "Expired run history was retained");
            }
            finally
            {
                // Only delete this test's uniquely named generated fixture directory.
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        });
    }
}
