using System.Globalization;
using CompUsage.Core;

internal static class SessionObservationChecks
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time");
    private static DateTimeOffset At(string time) => WorkDayClock.At(new(2026, 10, 16), TimeOnly.Parse(time, CultureInfo.InvariantCulture), Zone);
    private static LockEvent Event(long id, string time, int type) => new(id, At(time), type, "sid", "Test", "LOCAL", "1", "0x1");
    private static LockEvent[] Handoff() => [Event(1,"08:31:39",4801), Event(2,"09:01:49",4779),
        Event(3,"09:01:50",4778), Event(4,"09:01:51",4800), Event(5,"09:30:29",4800),
        Event(6,"09:44:48",4801), Event(7,"17:00",4800)];
    private static WorkReport Report(IEnumerable<LockEvent> events, IEnumerable<ActivityOpen> opens) => WorkReports.Build(events,
        new AppSettings(), new Dictionary<string,string>(), At("18:00").AddDays(1), Zone, opens: opens).Single();
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    public static void Run(Action<string, Action> check)
    {
        check("An empty duplicate notification keeps the valid activity sample in either input order", () =>
        {
            var native = Event(1,"12:00",4800);
            var valid = new ActivityLock(native.Timestamp.AddMilliseconds(-1),At("11:55"),At("11:59:59"),"1",true);
            var empty = new ActivityLock(native.Timestamp.AddMilliseconds(5),null,null,"1",true);
            foreach (var samples in new[] { new[] { valid, empty }, new[] { empty, valid }, new[] { valid, valid, empty } })
                Require(ActivityHistory.Match(samples,native) == valid, "A duplicate discarded the valid input");
            var day = WorkReports.Build([Event(2,"08:00",4801),native], new AppSettings { LockTimeSource="remote" },
                new Dictionary<string,string>(),At("18:00"),Zone,activity:[valid,empty]).Single();
            Require(day.Last!.WorkTimestamp == valid.LastInputAt && day.Sections.Single().To == valid.LastInputAt,
                "The preserved sample did not reach work sections and clipboard endpoints");
        });
        check("Activity coalescing preserves conflicts, rapid distinct locks and logon isolation", () =>
        {
            var native=Event(1,"12:00",4800);
            var valid=new ActivityLock(native.Timestamp,At("11:55"),At("11:59:59"),"1",true) { LogonId="0x1" };
            foreach(var different in new[] { valid with { LastInputAt=At("11:56"),LockedAt=native.Timestamp.AddMilliseconds(5) },
                valid with { Remote=false,LockedAt=native.Timestamp.AddMilliseconds(5) },
                valid with { LockedAt=native.Timestamp.AddMilliseconds(101) } })
                Require(ActivityHistory.Match([valid,different],native) is null,"A genuinely ambiguous sample was silently selected");
            Require(ActivityHistory.Match([valid with {LogonId="0x2"}],native) is null,"A reused numeric session crossed logons");
            Require(ActivityHistory.Match([valid,valid with {LogonId="0x2"}],native)==valid,"A foreign logon hid the valid sample");
            var a=new ActivityLock(native.Timestamp,null,null,"1");
            Require(ActivityHistory.CoalesceLocks([a,a with {LockedAt=a.LockedAt.AddMilliseconds(90)},a with {LockedAt=a.LockedAt.AddMilliseconds(180)}]).Count==2,
                "The duplicate window was extended by chaining");
        });
        check("A verified open desktop repairs a delayed RDP handoff lock without dropping real events", () =>
        {
            var observed=new ActivityOpen(At("09:01:52"),"1","0x1");
            var day=Report(Handoff(),[observed]);
            Require(!day.Incomplete && day.Sections.Count==2,"Verified reconnect still left the day incomplete");
            Require(day.Events.Any(e=>e.RecordId==4) && day.Events.Any(e=>e.EventId==LockEvent.ObservedUnlock && e.Timestamp==observed.ObservedAt),
                "Recovery discarded the native lock or backdated the observation");
            Require(day.Events.Single(e=>e.EventId==LockEvent.ObservedUnlock).Label=="Feloldott állapot észlelve","An observation was presented as a native unlock");
            Require(Report(Handoff().Reverse(),[observed,observed]).Pairs.Count==day.Pairs.Count,"Input order or duplicate observations changed the result");
        });
        check("An actual lock after reconnect stays excluded until Windows confirms an unlocked desktop", () =>
        {
            var day=Report(Handoff(),[new(At("09:20"),"1","0x1")]);
            Require(!day.Incomplete && day.Sections.Any(s=>s.To==At("09:01:51")) && day.Sections.Any(s=>s.From==At("09:20")),
                "A real lock was hidden by reconnect proximity");
        });
        check("Missing historical unlocks remain flagged without a verified open observation", () =>
        {
            Require(Report(Handoff(),[]).Incomplete,"Unknown history was guessed from a reconnect alone");
            foreach(var invalid in new[] {new ActivityOpen(At("09:01:52"),"2","0x1"),new(At("09:01:52"),"1","0x2"),
                new(At("09:01:52"),"1",""),new(At("09:01:49"),"1","0x1"),new(At("09:01:52").AddDays(3),"1","0x1")})
                Require(Report(Handoff(),[invalid]).Incomplete,"Invalid or pre-lock evidence repaired an unknown interval");
        });
        check("Real unlock timestamps and saved pair IDs win over nearby observer samples", () =>
        {
            var real=Event(8,"09:01:53",4801);
            var events=Handoff().Append(real).ToArray();
            var original=Report(events,[]);
            var observed=Report(events,[new(At("09:01:52"),"1","0x1"),new(At("09:01:54"),"1","0x1")]);
            Require(!observed.Incomplete && original.Pairs.Select(p=>p.Id).SequenceEqual(observed.Pairs.Select(p=>p.Id)),"Observer data changed native pair IDs");
            Require(observed.Events.All(e=>e.EventId!=LockEvent.ObservedUnlock),"An unnecessary synthetic opening was added");
        });
        check("Verified openings replace provisional wakes, but never bridge shutdowns or sign-outs", () =>
        {
            var sleep=new LockEvent(100,At("12:00"),LockEvent.Sleep,"","","","","");
            var wake=sleep with { Timestamp=At("12:40"),EventId=LockEvent.Wake };
            var day=Report([Event(1,"08:00",4801),sleep,wake,Event(2,"17:00",4800)],[new(At("12:55"),"1","0x1")]);
            Require(!day.Incomplete && day.Sections[1].From==At("12:55"),"The inferred wake overrode a verified opening");
            foreach(var stop in new[] { Event(3,"09:02",4647),sleep with {Timestamp=At("09:02"),EventId=LockEvent.SystemShutdown},
                sleep with {Timestamp=At("09:02"),EventId=LockEvent.UnexpectedShutdown} })
            {
                var recovered=ActivityHistory.RecoverOpenStates([Event(1,"08:00",4801),Event(2,"09:01",4800),stop],
                    [new(At("09:03"),"1","0x1")],At("18:00"));
                Require(recovered.All(e=>e.EventId!=LockEvent.ObservedUnlock),"An old observation revived an ended session");
            }
        });
        check("Observer openings survive status writes and old journals remain readable", () =>
        {
            var root=Path.Combine(Path.GetTempPath(),"CompUsage-observer-check-"+Guid.NewGuid().ToString("N"));
            try
            {
                var journal=new ActivityJournal(root);
                var open=new ActivityOpen(At("09:01:52"),"1","0x1");
                var state=new ActivitySession {SessionId="1",LogonId="0x1",Opens=[open,open with {ObservedAt=open.ObservedAt.AddDays(-62)}]};
                journal.Save(state,At("18:00"));journal.Heartbeat(state,At("18:00"));
                Require(journal.Load("1").Opens.SequenceEqual([open]),"Status replaced or failed to prune observed openings");
                var legacy=System.Text.Json.JsonSerializer.Deserialize<ActivitySession>("{\"SessionId\":\"2\"}")!;
                Require(legacy.Opens.Count==0,"Legacy journals require migration");
            }
            finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
        });
    }
}
