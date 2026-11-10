using CompUsage.Core;

internal static class DayBoundaryChecks
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time");
    private static DateTimeOffset At(int day, int hour, int minute = 0, int second = 0) => WorkDayClock.At(new(2026,11,day),new(hour,minute,second),Zone);
    private static LockEvent E(long id, DateTimeOffset at, int type) => new(id,at,type,"sid","Test","LOCAL","2","0x1");
    private static IReadOnlyList<WorkReport> Build(IEnumerable<LockEvent> events, AppSettings? settings = null,
        IEnumerable<IdleBreak>? idle = null, IEnumerable<MeasurementSpan>? measurements = null, DateTimeOffset? now = null) =>
        WorkReports.Build(events,settings ?? new(),new Dictionary<string,string>(),now ?? At(10,12),Zone,idles:idle,measurements:measurements);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(Action<string,Action> check)
    {
        check("An overnight RDP section is split at 04:00 without missing endpoints or invented raw Windows events", () =>
        {
            var opened=E(1,At(8,0,18,49),4801);var closed=E(2,At(8,6,15,13),4800);
            var days=Build([opened,closed],idle:[new(At(8,4,5,8),At(8,6,15,13),"2")]);
            var before=days.Single(d=>d.Date==new DateOnly(2026,11,7));var after=days.Single(d=>d.Date==new DateOnly(2026,11,8));
            Require(!before.Incomplete&&!after.Incomplete,"A workday boundary still looks like missing Windows events");
            Require(before.Sections.Single().To==At(8,4)&&after.Sections.Single().From==At(8,4)&&after.Sections[0].To==At(8,4,5,8),"Boundary clipping lost the last-input endpoint");
            Require(after.Sections[0].Duration<TimeSpan.FromMinutes(6)&&after.Sections[0].UnsplitDuration>TimeSpan.FromHours(3),"The minimum filtered an artificial short calendar fragment");
            Require(before.Last?.EventId==LockEvent.DayBoundaryEnd&&after.First?.EventId==LockEvent.DayBoundaryStart,"Calendar continuity is not explicitly labelled");
            Require(days.SelectMany(d=>d.Events).Count()==2&&days.SelectMany(d=>d.Events).All(e=>!e.IsDayBoundary),"Synthetic boundaries leaked into the raw event log");
            Require(WorkSummary.Worked(before)+WorkSummary.Worked(after)==At(8,4,5,8)-opened.Timestamp,"Work was lost or counted twice at the boundary");
        });
        check("A short break across 04:00 is merged before the minimum and calendar clipping", () =>
        {
            var days=Build([E(1,At(8,3,57),4801),E(2,At(8,3,59),4800),E(3,At(8,4,1),4801),E(4,At(8,4,3),4800)]);
            Require(days.All(d=>!d.Incomplete)&&days.Sum(d=>d.Sections.Count)==2,"A short boundary break discarded valid work");
            Require(days.SelectMany(d=>d.Sections).All(s=>s.Duration==TimeSpan.FromMinutes(3)&&s.UnsplitDuration==TimeSpan.FromMinutes(6)),"Minimum was applied to fragments rather than the complete section");
        });
        check("Long breaks across a boundary stay excluded and keep their original pair identity", () =>
        {
            var close=E(2,At(8,3,50),4800);var open=E(3,At(8,4,20),4801);
            var days=Build([E(1,At(8,3,30),4801),close,open,E(4,At(8,5),4800)]);
            Require(days.All(d=>!d.Incomplete)&&days.Sum(d=>WorkSummary.Worked(d).TotalMinutes)==60,"The overnight break became work");
            Require(days.Single(d=>d.Date.Day==7).Pairs.Single().Id==new BreakPair(close,open).Id,"Splitting days changed the real break-pair ID");
        });
        check("A closing event exactly at the day start closes the prior section without a false missing start", () =>
        {
            var days=Build([E(1,At(7,22),4801),E(2,At(8,4),4800)]);
            Require(days.All(d=>!d.Incomplete)&&days.Sum(d=>d.Sections.Count)==1,"Exact boundary end created an extra interval or warning");
            Require(days.Single(d=>d.Date.Day==7).Sections.Single().To==At(8,4),"Exact seconds were changed to place an end in the prior day");
        });
        check("A section spanning several workdays retains all elapsed time and empty raw-log days", () =>
        {
            var start=At(6,23);var end=At(9,5);
            var days=Build([E(1,start,4801),E(2,end,4800)]);
            Require(days.Count==4&&days.All(d=>!d.Incomplete),"An intermediate event-free day was lost");
            Require(days.Sum(d=>WorkSummary.Worked(d).Ticks)==(end-start).Ticks,"Multiple boundary clipping duplicated or lost time");
            Require(days.Where(d=>d.Date.Day is 7 or 8).All(d=>d.Events.Count==0),"Intermediate days contain fabricated Windows events");
        });
        check("A live overnight section needs fresh same-session sampling evidence, while unknown history stays incomplete", () =>
        {
            var opened=E(1,At(7,23),4801);var now=At(8,7);
            var evidence=new MeasurementSpan("2","0x1","run",now.AddSeconds(-30),now,"sampling",true,30,5,null,now);
            var days=Build([opened],measurements:[evidence],now:now);
            Require(days.Count==2&&days.All(d=>!d.Incomplete),"Fresh measured work was not carried into today");
            Require(days.Single(d=>d.Ongoing).Sections.Single().Open&&!days.Single(d=>!d.Ongoing).Sections.Single().Open,"Only today's final fragment should stay open");
            foreach(var invalid in new[]{evidence with {State="inactive"},evidence with {To=now.AddSeconds(-11)},evidence with {SessionId="1"},evidence with {LogonId="0x2"}})
                Require(Build([opened],measurements:[invalid],now:now).Single(d=>d.Date.Day==7).Incomplete,"Stale or foreign evidence repaired an unknown end");
            Require(Build([opened],now:now).Single().Incomplete,"Old missing endpoints were silently completed");
        });
        check("Unexplained transitions and foreign sessions are not hidden by calendar boundary markers", () =>
        {
            foreach(var events in new[]{new[]{E(1,At(8,3),4801),E(2,At(8,3,30),4801),E(3,At(8,5),4800)},
                new[]{E(1,At(8,3),4801),E(2,At(8,5),4800) with {SessionId="1"}}})
                Require(Build(events).All(d=>d.Incomplete&&d.Sections.Count==0),"Boundary carry concealed an ambiguous work interval");
        });
        check("Workday clipping uses real instants through the autumn DST change", () =>
        {
            var start=WorkDayClock.At(new(2026,10,24),new(23,0),Zone);var end=WorkDayClock.At(new(2026,10,25),new(5,0),Zone);
            var days=WorkReports.Build([E(1,start,4801),E(2,end,4800)],new(),new Dictionary<string,string>(),end.AddDays(1),Zone);
            Require(days.All(d=>!d.Incomplete)&&days.Sum(d=>WorkSummary.Worked(d).TotalHours)==7,"DST clipping lost the repeated hour");
        });
    }
}
