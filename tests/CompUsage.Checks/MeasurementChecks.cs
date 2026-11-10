using System.Text.Json;
using CompUsage.Core;

internal static class MeasurementChecks
{
    private static readonly DateTimeOffset Start=new(2026,11,10,0,0,0,TimeSpan.Zero);
    private static void Require(bool value,string message){if(!value)throw new Exception(message);}
    private static void WithJournal(Action<MeasurementJournal,MeasurementRecorder> test)
    {
        var directory=Directory.CreateTempSubdirectory("CompUsage-measurement-check-").FullName;
        try{var journal=new MeasurementJournal(directory);test(journal,new(journal,"2","0x1"));}
        finally{Directory.Delete(directory,true);}
    }
    public static void Run(Action<string,Action> check)
    {
        check("Continuous input polling counts raw tick changes without treating timestamp conversion jitter as activity",()=>WithJournal((journal,recorder)=>
        {
            for(var i=0;i<=60;i++)recorder.Observe(Start.AddSeconds(i),"sampling",true,(uint)(i<20?100:200),Start.AddSeconds(i%2==0?-1:-1.001));
            recorder.Stop(Start.AddSeconds(61));
            var spans=journal.Read(Start,Start.AddMinutes(2));
            Require(spans.Where(s=>s.State=="sampling").Sum(s=>s.Samples)==61,"Samples were lost or duplicated at a flush boundary");
            Require(spans.Sum(s=>s.InputChanges)==1,"The same Windows input tick was counted as repeated activity");
            Require(spans.All(s=>s.State!="gap"),"Healthy one-second polling produced a measurement gap");
        }));
        check("A frozen polling loop becomes an explicit gap and never a continuous sampling span",()=>WithJournal((journal,recorder)=>
        {
            recorder.Observe(Start,"sampling",true,100,Start);recorder.Observe(Start.AddSeconds(1),"sampling",true,100,Start);
            recorder.Observe(Start.AddMinutes(10),"sampling",true,200,Start.AddMinutes(9));recorder.Stop(Start.AddMinutes(10).AddSeconds(1));
            var spans=journal.Read(Start,Start.AddHours(1));
            var gap=spans.Single(s=>s.State=="gap");
            Require(gap.From==Start.AddSeconds(1)&&gap.To==Start.AddMinutes(10),"The exact unobserved interval was not preserved");
            Require(spans.Where(s=>s.State=="sampling").All(s=>s.To-s.From<TimeSpan.FromSeconds(5)),"Missing coverage was labelled as measured activity");
        }));
        check("Disabled, inactive, failed, unknown and stopped measurements retain their own states",()=>WithJournal((journal,recorder)=>
        {
            var states=new[]{"sampling","disabled","inactive","unavailable","input-error","error"};
            for(var i=0;i<states.Length;i++)recorder.Observe(Start.AddSeconds(i),states[i],inputTick:100,inputAt:Start);
            recorder.Stop(Start.AddSeconds(states.Length));
            var saved=journal.Read(Start,Start.AddMinutes(1));
            Require(states.All(state=>saved.Any(s=>s.State==state))&&saved.Any(s=>s.State=="stopped"),"Measurement failures or a deliberate stop disappeared");
            Require(saved.Where(s=>s.State!="sampling").All(s=>s.Samples==0&&s.InputChanges==0),"Non-measurement time contains input updates");
        }));
        check("Restarting recovers the durable checkpoint without inventing coverage or counting a span twice",()=>WithJournal((journal,recorder)=>
        {
            for(var i=0;i<=9;i++)recorder.Observe(Start.AddSeconds(i),"sampling",true,(uint)(100+i),Start.AddSeconds(i));
            journal.Checkpoint(recorder.Current);
            Require(journal.Read(Start,Start.AddMinutes(1)).Sum(s=>s.InputChanges)==9,"A checkpoint is not usable before the next full append");
            recorder.Flush();
            Require(journal.Read(Start,Start.AddMinutes(1)).Sum(s=>s.InputChanges)==9,"Checkpoint and append were counted twice");
            journal.RecoverCheckpoint("2");
            var next=new MeasurementRecorder(journal,"2","0x1");next.Observe(Start.AddMinutes(1),"sampling",true,500,Start.AddMinutes(1));next.Stop(Start.AddMinutes(1).AddSeconds(1));
            var timeline=MeasurementSummary.Timeline(journal.Read(Start,Start.AddMinutes(2)),Start,Start.AddMinutes(2));
            Require(timeline.Any(s=>s.State=="unknown"&&s.From==Start.AddSeconds(9)&&s.To==Start.AddMinutes(1)),"A restart silently bridged the time without a monitor");
            Require(timeline.Sum(s=>s.InputChanges)==9,"Recovery duplicated persisted input updates");
        }));
        check("A torn append record does not swallow later valid coverage, and old files are pruned",()=>WithJournal((journal,recorder)=>
        {
            recorder.Observe(Start,"sampling",true,100,Start);recorder.Flush();
            var file=Directory.GetFiles(journal.DirectoryPath,"*.jsonl").Single();File.AppendAllText(file,"{\"partial\":");
            recorder.Observe(Start.AddSeconds(1),"sampling",true,101,Start.AddSeconds(1));recorder.Flush();
            Require(journal.Read(Start,Start.AddMinutes(1)).Any(s=>s.To==Start.AddSeconds(1)),"A partial line corrupted the next record");
            journal.Append(new("2","0x1","old",Start.AddDays(-62),Start.AddDays(-62),"sampling",true,1,0,null,null));
            journal.Prune(Start);
            Require(Directory.GetFiles(journal.DirectoryPath,"*.jsonl").Length==1,"Evidence retention removed current data or retained expired data");
        }));
        check("Coverage data separates measured idle time, changed input and intervals with no evidence",()=>WithJournal((journal,recorder)=>
        {
            for(var i=0;i<=40;i++)recorder.Observe(Start.AddSeconds(i),"sampling",true,100,Start.AddMinutes(-5));
            recorder.Stop(Start.AddSeconds(41));
            var timeline=MeasurementSummary.Timeline(journal.Read(Start,Start.AddMinutes(1)),Start.AddMinutes(-1),Start.AddMinutes(2));
            Require(timeline.Any(s=>s.State=="sampling"&&s.InputChanges==0&&s.Samples>0),"Measured idle time cannot be distinguished from missing measurement");
            Require(timeline.First().State=="unknown"&&timeline.Last().State=="unknown","Unobserved edges were treated as measured");
            var legacy=JsonSerializer.Deserialize<ActivitySession>("{\"SessionId\":\"2\"}")!;
            Require(legacy.Opens.Count==0,"Old journals require a destructive migration");
        }));
        check("A backwards system clock is logged as uncertainty instead of a negative coverage interval",()=>WithJournal((journal,recorder)=>
        {
            recorder.Observe(Start.AddSeconds(10),"sampling",true,100,Start);
            recorder.Observe(Start,"sampling",true,101,Start);recorder.Stop(Start.AddSeconds(1));
            var spans=journal.Read(Start.AddSeconds(-1),Start.AddMinutes(1));
            Require(spans.Any(s=>s.State=="clock-change")&&spans.All(s=>s.To>=s.From),"A clock change became continuous or negative coverage");
        }));
        check("A multi-day polling gap is discoverable inside each affected UTC day",()=>WithJournal((journal,recorder)=>
        {
            recorder.Observe(Start,"sampling",true,100,Start);
            recorder.Observe(Start.AddDays(3).AddHours(1),"sampling",true,200,Start.AddDays(3));
            var middle=journal.Read(Start.AddDays(1),Start.AddDays(2));
            Require(middle.Any(s=>s.State=="gap"&&s.From<=Start.AddDays(1)&&s.To>=Start.AddDays(2)),"Reading one day missed a gap that ended on a later day");
        }));
        check("Structurally incomplete records are unknown evidence and cannot break report calculation",()=>WithJournal((journal,recorder)=>
        {
            recorder.Observe(Start,"sampling",true,100,Start);recorder.Flush();
            var file=Directory.GetFiles(journal.DirectoryPath,"*.jsonl").Single();
            var malformed=new{SessionId="2",State="sampling",From=Start,To=Start.AddMinutes(1),Samples=10,InputChanges=2};
            File.AppendAllText(file,JsonSerializer.Serialize(malformed)+"\n");
            Require(journal.Read(Start,Start.AddMinutes(2)).Count==1,"A record without run and logon identity was trusted");
        }));
    }
}
