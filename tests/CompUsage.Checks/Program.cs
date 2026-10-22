using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ClosedXML.Excel;
using CompUsage.Core;

try
{
var count = 0;
var zone = TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time");
var huCulture = (CultureInfo)CultureInfo.GetCultureInfo("hu-HU").Clone();
huCulture.DateTimeFormat.DateSeparator = ".";
var now = WorkDayClock.At(new(2026, 10, 19), new(14, 0), zone);
var settings = new AppSettings();
var lunches = new Dictionary<string, string>();
long id = 1;
DateTimeOffset At(int day, string time) => WorkDayClock.At(new(2026, 10, day), TimeOnly.Parse(time, CultureInfo.InvariantCulture), zone);
LockEvent Event(int day, string time, bool locked, string session = "1") => new(id++, At(day, time), locked ? 4800 : 4801, "S-1-5-21-1", "Test", "LOCAL", session, "0x1");
List<LockEvent> Day(int day, string from = "08:02:14", string until = "16:48:09") =>
    [Event(day, from, false), Event(day, "10:16:03", true), Event(day, "10:20:09", false),
     Event(day, "11:32:05", true), Event(day, "11:51:34", false), Event(day, until, true)];
WorkReport Report(IEnumerable<LockEvent> events, AppSettings? config = null) => WorkReports.Build(events, config ?? settings, lunches, now, zone).Single();
void Check(string name, Action test) { test(); count++; Console.WriteLine("PASS " + name); }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

Check("Defaults and validation", () => { Equal(new TimeOnly(4,0), settings.DayStart); Equal(60, settings.HistoryDays); Equal(15, settings.MinimumLunchMinutes); Equal(10, settings.SplitBreakMinutes); settings.Validate(); Throws<ArgumentException>(() => (settings with { HistoryDays = 61 }).Validate()); Throws<ArgumentException>(() => (settings with { Language = "xx" }).Validate()); });
Check("Day boundary and Excel-style minute display", () => { Equal(new DateOnly(2026,10,15), WorkDayClock.GetDate(At(16,"03:59:59"),settings.DayStart,zone)); Equal(new DateOnly(2026,10,16), WorkDayClock.GetDate(At(16,"04:00"),settings.DayStart,zone)); Equal("8:01",Presentation.Time(At(16,"08:01:30"),zone)); Equal("8:01",Presentation.Time(At(16,"08:01:29"),zone)); Equal("23:59",Presentation.Time(At(16,"23:59:30"),zone)); });
Check("Automatic lunch and exact clipboard", () => { var d=Report(Day(16)); Equal("11:32:05",Presentation.Time(d.Lunch!.Lock.Timestamp,zone,true)); Equal(2,d.Sections.Count); Equal("2026.10.16\t08:02:14\t11:32:05",Presentation.CopyRow(d.Sections[0],zone)); True(!d.Incomplete&&!d.Ongoing); });
Check("Manual short lunch is excluded and retained across settings", () => { var events=Day(16);var original=Report(events);lunches["2026-10-16"]=original.Pairs[0].Id;var d=Report(events,settings with { ExpectedLunch=new(13,0) });Equal(original.Pairs[0].Id,d.Lunch!.Id);Equal(3,d.Sections.Count);lunches.Clear(); });
Check("Explicit no-lunch choice does not remove long-break splitting", () => { lunches["2026-10-16"]="none";var d=Report(Day(16));True(d.NoLunch&&d.Lunch is null);Equal(2,d.Sections.Count);lunches.Clear(); });
Check("Exactly 10 minutes count as work, 10 minutes and one second split", () => { var list=new[]{Event(16,"08:00",false),Event(16,"10:00",true),Event(16,"10:10",false),Event(16,"16:00",true)};Equal(1,Report(list).Sections.Count);list[2]=list[2] with {Timestamp=list[2].Timestamp.AddSeconds(1)};Equal(2,Report(list).Sections.Count); });
Check("Missing endpoints, duplicate transitions and multiple sessions are not inferred", () => { var list=Day(16);True(Report(list.Skip(1)).Incomplete);True(Report(list.SkipLast(1)).Incomplete);True(Report(list.Concat([Event(16,"10:02",true)])).Incomplete);True(Report(list.Select((e,i)=>i==0?e with {SessionId="2"}:e)).Incomplete); });
Check("Today has an open interval, past days require a closing lock", () => { var d=Report(Day(19).SkipLast(1));True(d.Ongoing&&!d.Incomplete);True(d.Sections[^1].Open);Equal(now,d.Sections[^1].To); });
Check("Logging gaps flag affected days", () => { var d=WorkReports.Build(Day(16),settings,lunches,now,zone,[new(At(16,"12:00"),At(16,"13:00"))]).Single();True(d.Incomplete);Equal(0,d.Sections.Count); });
Check("Sixty-day range includes exactly the configured work dates", () => { var today=WorkDayClock.GetDate(now,settings.DayStart,zone);var list=Enumerable.Range(0,65).SelectMany(i=>{var date=today.AddDays(-i);return new[]{new LockEvent(id++,WorkDayClock.At(date,new(8,0),zone),4801,"sid","name","","1","1"),new LockEvent(id++,WorkDayClock.At(date,new(12,0),zone),4800,"sid","name","","1","1")};});Equal(60,WorkReports.Build(list,settings,lunches,now,zone).Count); });
Check("DST gaps and repeated hours preserve real elapsed duration", () => { var spring=WorkDayClock.At(new(2026,3,29),new(2,30),zone);Equal(3,TimeZoneInfo.ConvertTime(spring,zone).Hour);var early=new DateTimeOffset(2026,10,25,2,30,0,TimeSpan.FromHours(2));var late=new DateTimeOffset(2026,10,25,2,30,0,TimeSpan.FromHours(1));Equal(TimeSpan.FromHours(1),late-early);Equal(early,WorkDayClock.At(new(2026,10,25),new(2,30),zone)); });
Check("XML parser filters IDs and retains SID, session and UTC precision", () => { var xml="<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>4801</EventID><EventRecordID>42</EventRecordID><TimeCreated SystemTime=\"2026-10-19T07:45:56.969Z\"/></System><EventData><Data Name=\"TargetUserSid\">S-1-5-21-1</Data><Data Name=\"SessionId\">2</Data><Data Name=\"TargetLogonId\">0x3</Data></EventData></Event>";var e=EventXmlParser.Parse(xml)!;Equal(42L,e.RecordId);Equal(969,e.Timestamp.Millisecond);Equal("S-1-5-21-1|2|0x3",e.SessionKey);True(EventXmlParser.Parse(xml.Replace("4801","4624")) is null); });

Check("Reconnect XML retains the account and logon ID without inventing a SID or numeric session", () =>
{
    const string xml = "<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>4778</EventID><EventRecordID>43</EventRecordID><TimeCreated SystemTime=\"2026-10-19T05:05:21.677Z\"/></System><EventData><Data Name=\"AccountName\">Test</Data><Data Name=\"AccountDomain\">LOCAL</Data><Data Name=\"LogonID\">0x1</Data><Data Name=\"SessionName\">RDP-Tcp#7</Data></EventData></Event>";
    var entry = EventXmlParser.Parse(xml)!;
    Equal("Test", entry.UserName); Equal("LOCAL", entry.Domain); Equal("0x1", entry.LogonId);
    Equal("", entry.UserSid); Equal("", entry.SessionId); True(!entry.IsLock && entry.IsSessionTransition);
    True(EventXmlParser.Parse(xml.Replace("4778", "4779"))!.IsLock);
});
Check("Session events require the full current account, while lock events require the exact SID", () =>
{
    var entry = Event(16, "08:00", false) with { EventId = 4778, UserSid = "" };
    var selected = SessionEvents.ForUser([entry, entry with {Domain="OTHER"}, entry with {UserName="Other"},
        entry with {UserSid="different-sid"}, entry with {EventId=4801}], "S-1-5-21-1", @"local\test").ToArray();
    Equal(1, selected.Length); Equal("S-1-5-21-1", selected[0].UserSid);
    Equal(1, SessionEvents.ForUser([Event(16,"08:00",false)], "S-1-5-21-1", @"LOCAL\Test").Count());
});
Check("RDP morning, disconnect and console reconnect fill the missing unlock without double counting", () =>
{
    var unlock = Event(19, "07:05:23", false);
    var locked = Event(19, "07:29:51", true);
    LockEvent[] entries = [unlock with {RecordId=id++,EventId=4778,Timestamp=At(19,"07:05:21"),SessionId=""},
        unlock, locked with {RecordId=id++,EventId=4779,Timestamp=At(19,"07:29:50"),SessionId=""}, locked,
        unlock with {RecordId=id++,EventId=4778,Timestamp=At(19,"08:13:51"),SessionId=""},
        Event(19,"09:29:43",true), Event(19,"09:41:18",false)];
    var day = Report(entries.Concat(entries));
    True(!day.Incomplete && day.Ongoing); Equal(7, day.Events.Count); Equal(unlock.RecordId, day.First!.RecordId);
    Equal(2, day.Pairs.Count); Equal(4778, day.Pairs[0].Unlock.EventId);
    Equal(locked.Timestamp, day.Pairs[0].Lock.Timestamp); Equal(At(19,"08:13:51"), day.Pairs[0].Unlock.Timestamp);
    Equal(3, day.Sections.Count); Equal(unlock.Timestamp, day.Sections[0].From);
    Equal(At(19,"08:13:51"), day.Sections[1].From); True(day.Sections[^1].Open);
});
Check("Duplicate connection records preserve an existing manual lunch pair and original second precision", () =>
{
    var entries = Day(16); var before = Report(entries); lunches["2026-10-16"] = before.Pairs[1].Id;
    var sessionCopies = entries.Select(e => e with {RecordId=id++,EventId=e.IsLock?4779:4778,
        Timestamp=e.Timestamp.AddMilliseconds(-600),SessionId=""});
    var day = Report(entries.Concat(sessionCopies));
    True(!day.Incomplete); Equal(before.Pairs[1].Id, day.Lunch!.Id); Equal(before.Sections.Count, day.Sections.Count);
    Equal(Presentation.CopyRow(before.Sections[0],zone), Presentation.CopyRow(day.Sections[0],zone));
    lunches.Clear();
});
Check("Deduplication cannot skip a real intervening opposite transition or cross sessions", () =>
{
    var connected = Event(16,"08:00",false) with {EventId=4778};
    LockEvent[] entries = [connected, Event(16,"08:00:01",true), Event(16,"08:00:02",false),Event(16,"17:00",true)];
    Equal(4, SessionEvents.WorkTransitions(entries).Count); True(!Report(entries).Incomplete);
    var other = Event(16,"08:00:01",false) with {SessionId="2"};
    Equal(2, SessionEvents.WorkTransitions([connected,other]).Count);
    Equal(2, SessionEvents.WorkTransitions([connected,other with {SessionId="1",LogonId="0x2"}]).Count);
    Equal(2, SessionEvents.WorkTransitions([connected,other with {SessionId="1",UserSid="other"}]).Count);
});
Check("Only unambiguous session IDs are recovered and unknown sessions stay marked for review", () =>
{
    var connected = Event(16,"08:00",false) with {EventId=4778,SessionId=""};
    var native = Event(16,"17:00",true);
    Equal("1", SessionEvents.ResolveSessions([connected,native])[0].SessionId);
    Equal("", SessionEvents.ResolveSessions([connected,native,native with {SessionId="2"}])[0].SessionId);
    True(Report([connected with {LogonId="0x2"},native]).Incomplete);
    Equal("1", SessionEvents.ResolveSessions([connected with {LogonId="0X1"},native])[0].SessionId);
});
Check("A session-only day supports the existing break threshold without fabricated lock events", () =>
{
    LockEvent[] entries = [Event(16,"08:00",false) with {EventId=4778,SessionId=""},
        Event(16,"10:00",true) with {EventId=4779,SessionId=""},
        Event(16,"10:10",false) with {EventId=4778,SessionId=""},Event(16,"17:00",true) with {EventId=4779,SessionId=""}];
    var day = Report(entries); True(!day.Incomplete); Equal(1,day.Sections.Count); Equal(4778,day.First!.EventId);
    Equal(4779,day.Last!.EventId); entries[2] = entries[2] with {Timestamp=entries[2].Timestamp.AddSeconds(1)};
    Equal(2,Report(entries).Sections.Count);
});
LockEvent SignIn(int day, string time, string logon) => new(id++, At(day, time), 4624, "S-1-5-21-1", "Test", "LOCAL", "", logon);
LockEvent SignOut(int day, string time, string logon) => new(id++, At(day, time), 4647, "S-1-5-21-1", "Test", "LOCAL", "", logon);
Check("Sign-in XML keeps only interactive logons, and sign-out ends work", () =>
{
    const string xml = "<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>4624</EventID><EventRecordID>44</EventRecordID><TimeCreated SystemTime=\"2026-10-21T12:46:50.166Z\"/></System><EventData><Data Name=\"TargetUserSid\">S-1-5-21-1</Data><Data Name=\"TargetUserName\">Test</Data><Data Name=\"TargetDomainName\">LOCAL</Data><Data Name=\"TargetLogonId\">0xe1ca5</Data><Data Name=\"LogonType\">11</Data></EventData></Event>";
    var entry = EventXmlParser.Parse(xml)!;
    Equal("0xe1ca5", entry.LogonId); Equal("", entry.SessionId); True(!entry.IsLock && entry.IsSignInOrOut && entry.IsWorkTransition);
    True(EventXmlParser.Parse(xml.Replace(">11<", ">2<")) is not null); True(EventXmlParser.Parse(xml.Replace(">11<", ">10<")) is not null);
    True(EventXmlParser.Parse(xml.Replace(">11<", ">3<")) is null); True(EventXmlParser.Parse(xml.Replace(">11<", ">7<")) is null);
    var signOut = EventXmlParser.Parse(xml.Replace("4624", "4647").Replace("<Data Name=\"LogonType\">11</Data>", ""))!;
    True(signOut.IsLock && signOut.IsSignInOrOut); Equal("0xe1ca5", signOut.LogonId);
});
Check("Credential checks at unlock are not sign-ins; the session's own and the current logon are", () =>
{
    var session = SignIn(22, "08:21:27", "0x1a02fd"); var elevated = SignIn(22, "08:21:27", "0x1a017a");
    var unlockCheck = SignIn(22, "08:45:15", "0x1297358"); var locked = Event(22, "08:32:27", true) with { LogonId = "0x1A02FD" };
    var kept = SessionEvents.DropTransientSignIns([session, elevated, locked, unlockCheck], null);
    Equal(2, kept.Count); True(kept.Contains(session) && kept.Contains(locked));
    kept = SessionEvents.DropTransientSignIns([session, elevated, unlockCheck], "0x1A02FD");
    Equal(1, kept.Count); Equal(session, kept[0]);
});
Check("A mid-day restart continues the day from the new sign-in and a shutdown closes it", () =>
{
    LockEvent[] entries = [Event(16,"08:24:09",false) with {EventId=4778,SessionId="",LogonId="0x2a1acf"},
        Event(16,"11:27:34",true,"2") with {LogonId="0x2a1acf"}, Event(16,"11:44:22",false,"2") with {LogonId="0x2a1acf"},
        SignOut(16,"14:46:16","0x2a1acf"), SignIn(16,"14:46:50","0xe1ca5"), SignOut(16,"17:33:07","0xe1ca5")];
    var day = Report(entries);
    True(!day.Incomplete); Equal(4778, day.First!.EventId); Equal(4647, day.Last!.EventId); Equal(At(16,"17:33:07"), day.Last.Timestamp);
    Equal(2, day.Pairs.Count); Equal(4624, day.Pairs[1].Unlock.EventId); Equal(4647, day.Pairs[1].Lock.EventId);
    Equal(2, day.Sections.Count); Equal(At(16,"11:44:22"), day.Sections[1].From); Equal(At(16,"17:33:07"), day.Sections[1].To);
    entries[4] = entries[4] with { Timestamp = At(16,"14:56:17") };
    Equal(3, Report(entries).Sections.Count);
});
Check("Real lock/unlock records win over sign-in/sign-out, and a sign-out after a lock is ignored", () =>
{
    var day = Report([SignIn(16,"08:00:00","0x1"), Event(16,"08:00:02",false), Event(16,"12:00",true), Event(16,"12:30",false),
        Event(16,"17:00",true), SignOut(16,"17:20","0x1")]);
    True(!day.Incomplete); Equal(4801, day.First!.EventId); Equal(4800, day.Last!.EventId); Equal(6, day.Events.Count);
    Equal(At(16,"08:00:02"), day.Sections[0].From); Equal(At(16,"17:00"), day.Sections[^1].To);
});
Check("Disconnecting a locked console session before an RDP reconnect does not repeat the lock", () =>
{
    var day = Report([Event(16,"11:14:23",false), Event(16,"18:37:37",true), Event(16,"19:33:52",true) with {EventId=4779,SessionId=""},
        Event(16,"19:33:53",false) with {EventId=4778,SessionId=""}, Event(16,"19:33:54",false),
        Event(16,"21:25:28",true) with {EventId=4779,SessionId=""}, Event(16,"21:25:29",true)]);
    True(!day.Incomplete); Equal(2, day.Sections.Count); Equal(At(16,"18:37:37"), day.Sections[0].To);
    Equal(At(16,"19:33:54"), day.Sections[1].From); Equal(At(16,"21:25:29"), day.Sections[1].To);
});
LockEvent[] SleepPeriod(int day, string from, string until) { var record = id++;
    return [new(record, At(day, from), LockEvent.Sleep, "", "", "", "", ""), new(record, At(day, until), LockEvent.Wake, "", "", "", "", "")]; }
LockEvent SystemEvent(int day, string time, int eventId) => new(id++, At(day, time), eventId, "", "", "", "", "");
Check("System-log XML yields exact sleep endpoints, the UTC last-alive time and shutdowns", () =>
{
    const string wake = "<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><Provider Name=\"Microsoft-Windows-Power-Troubleshooter\"/><EventID>1</EventID><EventRecordID>70</EventRecordID><TimeCreated SystemTime=\"2026-10-21T06:24:05Z\"/></System><EventData><Data Name=\"SleepTime\">2026-10-21T00:13:46.3083346Z</Data><Data Name=\"WakeTime\">2026-10-21T06:24:04.6372246Z</Data></EventData></Event>";
    var sleep = EventXmlParser.ParseSystem(wake);
    Equal(2, sleep.Count); Equal(LockEvent.Sleep, sleep[0].EventId); True(sleep[0].IsLock && sleep[0].IsSystemEvent && !sleep[1].IsLock);
    Equal(new DateTimeOffset(2026,10,21,0,13,46,TimeSpan.Zero).AddTicks(3083346), sleep[0].Timestamp); Equal(70L, sleep[1].RecordId);
    Equal(0, EventXmlParser.ParseSystem(wake.Replace("Power-Troubleshooter", "Other")).Count);
    Equal(0, EventXmlParser.ParseSystem(wake.Replace("2026-10-21T06:24:04", "2026-10-20T06:24:04")).Count);
    const string crash = "<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><Provider Name=\"EventLog\"/><EventID Qualifiers=\"32768\">6008</EventID><EventRecordID>71</EventRecordID><TimeCreated SystemTime=\"2026-10-09T21:24:58Z\"/></System><EventData><Data></Data><Binary>EA070A0005000900160039000E00B101EA070A0005000900140039000E00B101600900003C000000</Binary></EventData></Event>";
    var lost = EventXmlParser.ParseSystem(crash).Single();
    Equal(LockEvent.UnexpectedShutdown, lost.EventId); Equal(new DateTimeOffset(2026,10,9,20,57,14,433,TimeSpan.Zero), lost.Timestamp);
    Equal(0, EventXmlParser.ParseSystem(crash.Replace("EA070A0005000900160039000E00B101EA07", "EA07")).Count);
    var stop = EventXmlParser.ParseSystem(wake.Replace("Power-Troubleshooter", "Kernel-General").Replace("<EventID>1<", "<EventID>13<")).Single();
    Equal(LockEvent.SystemShutdown, stop.EventId); Equal(new DateTimeOffset(2026,10,21,6,24,5,TimeSpan.Zero), stop.Timestamp);
    True(EventXmlParser.Parse(wake) is null);
});
Check("Sleep while signed in is a break; sleep after a lock and an orderly shutdown after sign-out are ignored", () =>
{
    var day = Report([Event(16,"08:00",false), ..SleepPeriod(16,"12:00","12:40"), Event(16,"17:00",true)]);
    True(!day.Incomplete); Equal(1, day.Pairs.Count); Equal(LockEvent.Sleep, day.Lunch!.Lock.EventId); Equal(LockEvent.Wake, day.Lunch.Unlock.EventId);
    Equal(2, day.Sections.Count); Equal(At(16,"12:00"), day.Sections[0].To); Equal(4, day.Events.Count);
    day = Report([Event(16,"08:00",false), Event(16,"12:00",true), ..SleepPeriod(16,"12:01","12:40"), Event(16,"12:41",false),
        Event(16,"17:00",true), SystemEvent(16,"17:00:10",LockEvent.SystemShutdown)]);
    True(!day.Incomplete); Equal(4, day.Events.Count); Equal(1, day.Pairs.Count); Equal(4800, day.Last!.EventId);
    day = Report([Event(16,"08:00",false), SystemEvent(16,"17:33:17",LockEvent.SystemShutdown)]);
    True(!day.Incomplete); Equal(LockEvent.SystemShutdown, day.Last!.EventId); Equal(At(16,"17:33:17"), day.Sections[0].To);
});
Check("Sleep and hibernation merge, and a wake to the lock screen restarts work at the unlock", () =>
{
    var first = SleepPeriod(16,"12:00","12:00:40"); var second = SleepPeriod(16,"12:00","12:40");
    var merged = SessionEvents.MergeSleeps([..first, ..second]);
    Equal(2, merged.Count); Equal(At(16,"12:00"), merged[0].Timestamp); Equal(At(16,"12:40"), merged[1].Timestamp);
    foreach (var resumeLock in new[] { true, false })
    {
        LockEvent[] entries = [Event(16,"08:00",false), ..first, ..second, Event(16,"12:55",false), Event(16,"17:00",true)];
        var day = Report(resumeLock ? entries.Append(Event(16,"12:40:02",true)) : entries);
        True(!day.Incomplete); Equal(1, day.Pairs.Count); Equal(LockEvent.Sleep, day.Pairs[0].Lock.EventId);
        Equal(4801, day.Pairs[0].Unlock.EventId); Equal(At(16,"12:55"), day.Sections[1].From);
    }
});
Check("An unexpected shutdown ends work at the last-alive time and the next sign-in continues the day", () =>
{
    var day = Report([Event(16,"08:00",false), SystemEvent(16,"15:31:19",LockEvent.UnexpectedShutdown),
        SignIn(16,"15:58:10","0x9"), SignOut(16,"17:00","0x9")]);
    True(!day.Incomplete); Equal(2, day.Sections.Count); Equal(At(16,"15:31:19"), day.Sections[0].To);
    Equal(At(16,"15:58:10"), day.Sections[1].From); Equal(LockEvent.UnexpectedShutdown, day.Pairs[0].Lock.EventId);
    True(Report([Event(16,"08:00",false), Event(16,"12:00",true), SystemEvent(16,"15:31:19",LockEvent.UnexpectedShutdown)]).Events.All(e => !e.IsSystemEvent));
});
Check("Today's sign-in after boot starts work, while interleaved sessions still need review", () =>
{
    var today = Report([SignIn(19,"08:21:27","0x1"), Event(19,"08:32:27",true), Event(19,"08:45:15",false)]);
    True(today.Ongoing && !today.Incomplete); Equal(4624, today.First!.EventId); Equal(At(19,"08:21:27"), today.Sections[0].From);
    True(Report([SignIn(16,"08:00","0x2"), Event(16,"08:30",false), Event(16,"12:00",true), SignOut(16,"17:00","0x2")]).Incomplete);
    True(Report([Event(16,"08:00",false), Event(16,"10:00",true), SignIn(16,"10:05","0x2"), SignOut(16,"11:00","0x2"),
        Event(16,"11:05",false), Event(16,"17:00",true)]).Incomplete);
});

Check("Activity tracking defaults off and validates its optional time source", () =>
{
    True(!new ActivityConfiguration().Enabled);
    Equal("lock", settings.LockTimeSource);
    Throws<ArgumentException>(() => (settings with { LockTimeSource = "invalid" }).Validate());
});

List<LockEvent> MeetingDay() => [Event(16,"08:00",false),Event(16,"09:29:43",true),Event(16,"09:41:18",false),
    Event(16,"11:32:05",true),Event(16,"11:51:34",false),Event(16,"17:00",true)];
var meetingSettings = settings with {IgnoreDailyMeeting=true};
Check("Section comment identity survives an open interval closing and does not drift to another start", () =>
{
    var entries=Day(19).SkipLast(1).ToArray();var before=Report(entries);var section=before.Sections[^1];
    var notes=new Dictionary<string,string>{{section.Id,"Design and review — árvíztűrő"}};
    var later=WorkReports.Build(entries,settings,lunches,now.AddMinutes(5),zone,comments:notes).Single();
    Equal(section.Id,later.Sections[^1].Id);Equal(notes[section.Id],later.Sections[^1].Comment);
    var closed=WorkReports.Build(entries.Append(Event(19,"14:05",true)),settings,lunches,now.AddMinutes(10),zone,comments:notes).Single();
    Equal(section.Id,closed.Sections[^1].Id);Equal(notes[section.Id],closed.Sections[^1].Comment);
    Equal("",closed.Sections[0].Comment);
    var changed=WorkReports.Build(entries,settings with {ExpectedLunch=new(9,0)},new Dictionary<string,string>{{"2026-10-19","none"}},now,zone,comments:notes).Single();
    True(changed.Sections.All(s=>s.Id!=section.Id||s.Comment==notes[section.Id]));
});
Check("Section row copy has only three time columns, leaving Excel duration formulas and comments untouched", () =>
{
    var row=new WorkInterval(new(2026,10,16),At(16,"08:00:01"),At(16,"09:30:03")){Comment="Árvíz\t\"review\"\nnext"};
    var excel=new ExcelDisplay(new("yyyy.mm.dd","h:mm","h:mm"),huCulture,zone);
    Equal("2026.10.16\t08:00:01\t09:30:03",excel.CopyRow(row));
    Equal(3,excel.CopyRow(row).Split('\t').Length);Equal("Árvíz\t\"review\"\nnext",row.Comment);
    Throws<ArgumentException>(()=>_ = row with {Comment=new string('x',32768)});
    Throws<ArgumentException>(()=>_ = row with {Comment="bad\0text"});
});
Check("Displayed section duration rounds to minutes, including hour carry, without changing exact copy values", () =>
{
    foreach(var item in new[]{(Seconds:1468.1720969,Expected:"0:24"),(Seconds:1470.0,Expected:"0:25"),
        (Seconds:3590.0,Expected:"1:00"),(Seconds:12787.5646658,Expected:"3:33"),(Seconds:92.3664264,Expected:"0:02"),
        (Seconds:90030.0,Expected:"25:01")})
        Equal(item.Expected,ExcelDisplay.RoundedDuration(TimeSpan.FromSeconds(item.Seconds)));
    var duration=TimeSpan.FromSeconds(1468.1720969);
    Equal("0:24:28",ExcelDisplay.ExactDuration(duration));
});
WorkReport MeetingReport(IEnumerable<LockEvent> entries, AppSettings? config=null, string? choice=null) =>
    WorkReports.Build(entries,config??meetingSettings,lunches,now,zone,
        meetings:choice is null?null:new Dictionary<string,string>{{"2026-10-16",choice}}).Single();
Check("Daily meeting defaults are off, with a 09:25-09:40 start window and 20-minute maximum", () =>
{
    True(!settings.IgnoreDailyMeeting);Equal(new TimeOnly(9,25),settings.MeetingStartFrom);
    Equal(new TimeOnly(9,40),settings.MeetingStartUntil);Equal(20,settings.MaximumMeetingMinutes);
    Throws<ArgumentException>(()=>(settings with {MaximumMeetingMinutes=0}).Validate());
    Throws<ArgumentException>(()=>(settings with {MaximumMeetingMinutes=121}).Validate());
});
Check("An enabled daily meeting stays in work sections and exact copied rows, while lunch remains excluded", () =>
{
    var entries=MeetingDay();var baseline=Report(entries);var day=MeetingReport(entries);
    Equal(3,baseline.Sections.Count);Equal(2,day.Sections.Count);Equal(entries[1].RecordId,day.Meeting!.Lock.RecordId);
    Equal("2026.10.16\t08:00:00\t11:32:05",Presentation.CopyRow(day.Sections[0],zone));
    Equal(baseline.Lunch!.Id,day.Lunch!.Id);Equal(entries[2].Timestamp,day.Meeting.Unlock.Timestamp);
    Equal(baseline.Sections.Sum(s=>s.Duration.Ticks)+day.Meeting.Duration.Ticks,day.Sections.Sum(s=>s.Duration.Ticks));
});
Check("Meeting start bounds and duration maximum are inclusive and use original seconds", () =>
{
    foreach(var start in new[]{"09:25:00","09:40:00"})
    {
        var entries=MeetingDay();entries[1]=entries[1] with {Timestamp=At(16,start)};
        entries[2]=entries[2] with {Timestamp=entries[1].Timestamp.AddMinutes(20)};
        True(MeetingReport(entries).Meeting is not null);
        entries[2]=entries[2] with {Timestamp=entries[2].Timestamp.AddSeconds(1)};
        True(MeetingReport(entries).Meeting is null);
    }
    foreach(var start in new[]{"09:24:59","09:40:01"})
    {
        var entries=MeetingDay();entries[1]=entries[1] with {Timestamp=At(16,start)};
        entries[2]=entries[2] with {Timestamp=entries[1].Timestamp.AddMinutes(12)};
        True(MeetingReport(entries).Meeting is null);
    }
});
Check("Only one daily meeting is guessed, closest to the start-window midpoint with a deterministic tie", () =>
{
    LockEvent[] entries=[Event(16,"08:00",false),Event(16,"09:25",true),Event(16,"09:29",false),
        Event(16,"09:30",true),Event(16,"09:34",false),Event(16,"09:35",true),Event(16,"09:39",false),Event(16,"17:00",true)];
    Equal(entries[3].RecordId,MeetingReport(entries).Meeting!.Lock.RecordId);
});
Check("Manual meeting choices override guess limits, no-meeting restores splitting, and disabling suspends saved choices", () =>
{
    var entries=MeetingDay();var chosen=MeetingReport(entries).Meeting!.Id;
    var day=MeetingReport(entries,meetingSettings with {MaximumMeetingMinutes=1,MeetingStartFrom=new(10,0),MeetingStartUntil=new(10,15)},chosen);
    True(day.ManualMeeting);Equal(chosen,day.Meeting!.Id);Equal(2,day.Sections.Count);
    day=MeetingReport(entries,choice:"none");True(day.NoMeeting&&day.ManualMeeting&&day.Meeting is null);Equal(3,day.Sections.Count);
    day=MeetingReport(entries,settings,chosen);True(!day.ManualMeeting&&day.Meeting is null);Equal(3,day.Sections.Count);
    day=MeetingReport(entries,settings,"stale");True(!day.Incomplete);
    day=MeetingReport(entries,choice:"stale");True(day.Incomplete);Equal(0,day.Sections.Count);
});
Check("The meeting guess precedes an automatic lunch guess, but an explicit lunch always wins", () =>
{
    LockEvent[] entries=[Event(16,"08:00",false),Event(16,"09:40",true),Event(16,"10:00",false),Event(16,"17:00",true)];
    var day=MeetingReport(entries);True(day.Meeting is not null&&day.Lunch is null);Equal(1,day.Sections.Count);
    var chosen=day.Meeting!.Id;lunches["2026-10-16"]=chosen;
    day=MeetingReport(entries);True(day.Meeting is null);Equal(chosen,day.Lunch!.Id);Equal(2,day.Sections.Count);
    day=MeetingReport(entries,choice:chosen);True(day.Meeting is null&&day.Incomplete);Equal(chosen,day.Lunch!.Id);
    lunches.Clear();
});
Check("Meeting matching follows the selected activity endpoint and duration without changing raw locks", () =>
{
    var entries=MeetingDay();entries[2]=entries[2] with {Timestamp=At(16,"09:45")};
    ActivityLock[] records=[new(entries[1].Timestamp,At(16,"09:25"),entries[1].Timestamp.AddSeconds(-1),"1")];
    var config=meetingSettings with {LockTimeSource="activity"};
    var day=WorkReports.Build(entries,config,lunches,now,zone,activity:records).Single();
    Equal(At(16,"09:25"),day.Meeting!.Lock.WorkTimestamp);Equal(entries[1].Timestamp,day.Meeting.Lock.Timestamp);
    entries[2]=entries[2] with {Timestamp=At(16,"09:45:01")};
    True(WorkReports.Build(entries,config,lunches,now,zone,activity:records).Single().Meeting is null);
});
Check("Session disconnect pairs can be daily meetings, while incomplete days remain excluded", () =>
{
    var entries=MeetingDay();entries[1]=entries[1] with {EventId=4779,SessionId=""};entries[2]=entries[2] with {EventId=4778,SessionId=""};
    var day=MeetingReport(entries);Equal(4779,day.Meeting!.Lock.EventId);Equal(2,day.Sections.Count);
    day=MeetingReport(entries.Skip(1));True(day.Incomplete);Equal(0,day.Sections.Count);
});
Check("A meeting start window can cross midnight within the configured workday", () =>
{
    LockEvent[] entries=[Event(16,"20:00",false),Event(16,"23:59",true),Event(17,"00:12",false),Event(17,"03:00",true)];
    var day=MeetingReport(entries,meetingSettings with {MeetingStartFrom=new(23,55),MeetingStartUntil=new(0,10)});
    True(!day.Incomplete);Equal(entries[1].RecordId,day.Meeting!.Lock.RecordId);Equal(1,day.Sections.Count);
});
Check("Input ticks handle 32-bit rollover and reject impossible future input", () =>
{
    Equal(now.AddMilliseconds(-1500), ActivityHistory.InputTime(now, (ulong)uint.MaxValue + 1001, uint.MaxValue - 499)!.Value);
    True(ActivityHistory.InputTime(now, 100, 110) is null);
});
Check("Lock capture rejects stale, future and missing samples", () =>
{
    var locked = At(16,"18:00");
    True(ActivityHistory.Capture(locked, locked.AddMinutes(-10), locked.AddSeconds(-4), "1").LastInputAt is null);
    True(ActivityHistory.Capture(locked, locked.AddSeconds(1), locked, "1").LastInputAt is null);
    True(ActivityHistory.Capture(locked, null, null, "1").LastInputAt is null);
    var valid = ActivityHistory.Capture(locked, locked.AddSeconds(-602), locked.AddSeconds(-1), "1");
    Equal("0:10:02", ActivityHistory.IdleText(valid));
});
Check("Activity correlation requires the same session and an unambiguous nearby lock", () =>
{
    var locked = Event(16,"18:00",true);
    var record = new ActivityLock(locked.Timestamp, locked.Timestamp.AddMinutes(-10), locked.Timestamp.AddSeconds(-1), "1");
    Equal(record, ActivityHistory.Match([record], locked));
    True(ActivityHistory.Match([record with {SessionId="2"}],locked) is null);
    True(ActivityHistory.Match([record with {LockedAt=locked.Timestamp.AddSeconds(6)}],locked) is null);
    True(ActivityHistory.Match([record,record with {LockedAt=locked.Timestamp.AddSeconds(1)}],locked) is null);
    True(ActivityHistory.Match([record],locked with {EventId=4801}) is null);
});
Check("Activity mode updates lunch, split rules, exact copying and the final section without changing raw events", () =>
{
    var list = new[] {Event(16,"08:00",false),Event(16,"11:40",true),Event(16,"11:50",false),Event(16,"18:00",true)};
    ActivityLock[] records = [new(list[1].Timestamp, At(16,"11:20:30"),list[1].Timestamp.AddSeconds(-1),"1"),
        new(list[3].Timestamp,At(16,"17:45:10"),list[3].Timestamp.AddSeconds(-1),"1")];
    var original = Report(list);
    Equal(1, original.Sections.Count);
    var updated = WorkReports.Build(list,settings with {LockTimeSource="activity"},lunches,now,zone,activity:records).Single();
    Equal(2,updated.Sections.Count);
    Equal(original.Pairs[0].Id,updated.Lunch!.Id);
    Equal("2026.10.16\t08:00:00\t11:20:30",Presentation.CopyRow(updated.Sections[0],zone));
    Equal(At(16,"17:45:10"),updated.Sections[^1].To);
    Equal(At(16,"18:00"),updated.Last!.Timestamp);
    True(list.All(e=>e.ActivityTimestamp is null));
    var manual = new Dictionary<string,string>{{"2026-10-16",original.Pairs[0].Id}};
    True(WorkReports.Build(list,settings with {LockTimeSource="activity"},manual,now,zone,activity:records).Single().ManualLunch);
});
Check("Activity mode falls back to locks for unavailable samples and never moves a lock before its unlock or into another workday", () =>
{
    var list = new[] {Event(16,"08:00",false),Event(16,"18:00",true)};
    var record = new ActivityLock(list[1].Timestamp,At(16,"17:45"),list[1].Timestamp.AddSeconds(-1),"1");
    foreach (var observations in new ActivityLock[][] {[],[record with {LastInputAt=At(16,"07:59")}],[record with {SampledAt=list[1].Timestamp.AddSeconds(-4)}]})
        Equal(At(16,"18:00"),WorkReports.Build(list,settings with {LockTimeSource="activity"},lunches,now,zone,activity:observations).Single().Sections[^1].To);
    var overnight = new[] {Event(15,"23:00",false),Event(16,"04:05",true)};
    var boundary = new ActivityLock(overnight[1].Timestamp,At(16,"03:59"),overnight[1].Timestamp.AddSeconds(-1),"1");
    True(ActivityHistory.Apply(overnight,[boundary],settings with {LockTimeSource="activity"},zone).Last().ActivityTimestamp is null);
    Equal(At(16,"18:00"),WorkReports.Build(list,settings,lunches,now,zone,activity:[record]).Single().Sections[^1].To);
});

Check("Break prompts follow the split threshold and never ask at the start of a day", () =>
{
    True(settings.BreakPrompt); Equal(40, settings.BreakPromptSeconds); Equal(25, settings.CoffeeBreakMaxMinutes);
    True(!BreakReasons.ShouldPrompt(At(16,"14:00"), At(16,"14:10"), settings, zone));
    True(BreakReasons.ShouldPrompt(At(16,"14:00"), At(16,"14:10:01"), settings, zone));
    True(!BreakReasons.ShouldPrompt(At(15,"18:00"), At(16,"08:00"), settings, zone));
    True(!BreakReasons.ShouldPrompt(At(16,"14:00"), At(16,"15:00"), settings with { BreakPrompt = false }, zone));
    Throws<ArgumentException>(() => (settings with { BreakPromptSeconds = 4 }).Validate());
    Throws<ArgumentException>(() => (settings with { CoffeeBreakMaxMinutes = 0 }).Validate());
});
Check("Break suggestions: stand-up by meeting window, lunch by clock, then coffee or meeting by length", () =>
{
    Equal(BreakReasons.Standup, BreakReasons.Suggest(At(16,"09:30"), At(16,"09:45"), settings, zone, false));
    Equal(BreakReasons.Meeting, BreakReasons.Suggest(At(16,"13:40"), At(16,"14:10"), settings, zone, false));
    Equal(BreakReasons.Lunch, BreakReasons.Suggest(At(16,"11:35"), At(16,"12:05"), settings, zone, false));
    Equal(BreakReasons.Coffee, BreakReasons.Suggest(At(16,"11:35"), At(16,"11:55"), settings, zone, true));
    Equal(BreakReasons.Coffee, BreakReasons.Suggest(At(16,"14:00"), At(16,"14:25"), settings, zone, false));
    Equal(BreakReasons.Meeting, BreakReasons.Suggest(At(16,"15:00"), At(16,"15:25:01"), settings, zone, false));
    True(BreakReasons.SavedOnTimeout(BreakReasons.Standup) && BreakReasons.SavedOnTimeout(BreakReasons.Lunch));
    True(!BreakReasons.SavedOnTimeout(BreakReasons.Coffee) && !BreakReasons.SavedOnTimeout(BreakReasons.Meeting) && !BreakReasons.SavedOnTimeout(BreakReasons.Other));
});
Check("Break reason overrides round-trip and reject invalid input", () =>
{
    Equal("other:Orvos", BreakReasons.Encode("other", "  Orvos ")); Equal(("other", "Orvos"), BreakReasons.Parse("other:Orvos"));
    Equal("other", BreakReasons.Encode("other", "")); Equal("coffee", BreakReasons.Encode("coffee", "ignored"));
    Equal(((string?)null, ""), BreakReasons.Parse("none")); Equal(((string?)null, ""), BreakReasons.Parse("unknown"));
    Throws<ArgumentException>(() => BreakReasons.Encode("nap", null));
    Throws<ArgumentException>(() => BreakReasons.Encode("other", new string('x', 201)));
});
Check("Answered stand-ups and meetings count as work; coffee and other breaks split; answered lunch wins over the clock", () =>
{
    List<LockEvent> Events() => [Event(16,"08:00",false), Event(16,"09:30",true), Event(16,"09:45",false), Event(16,"11:32",true), Event(16,"11:51",false),
        Event(16,"14:00",true), Event(16,"14:40",false), Event(16,"17:00",true)];
    var list = Events();
    WorkReport Build(IEnumerable<BreakNote> notes, Dictionary<string,string>? reasons = null, AppSettings? config = null) =>
        WorkReports.Build(list, config ?? settings, lunches, now, zone, breaks: notes, reasons: reasons).Single();
    Equal(4, Build([]).Sections.Count);
    var standup = new BreakNote(At(16,"09:30:02"), At(16,"09:45:20"), BreakReasons.Standup);
    var meeting = new BreakNote(At(16,"14:00:01"), At(16,"14:41:30"), BreakReasons.Meeting);
    var day = Build([standup, meeting]);
    Equal(BreakReasons.Standup, day.Pairs[0].Reason); Equal(BreakReasons.Meeting, day.Pairs[2].Reason); Equal(2, day.Sections.Count);
    // A note more than two minutes away from the unlock belongs to another break.
    True(Build([meeting with { UnlockedAt = At(16,"14:42:30") }]).Pairs[2].Reason is null);
    var manual = Build([standup, meeting], new() { [day.Pairs[2].Id] = "other:Orvos" });
    Equal(BreakReasons.Other, manual.Pairs[2].Reason); Equal("Orvos", manual.Pairs[2].ReasonText); True(manual.Pairs[2].ReasonManual); Equal(3, manual.Sections.Count);
    True(Build([meeting], new() { [day.Pairs[2].Id] = "none" }).Pairs[2].Reason is null);
    var lunch = Build([new BreakNote(At(16,"14:00"), At(16,"14:40"), BreakReasons.Lunch, Automatic: true)]);
    Equal(lunch.Pairs[2].Id, lunch.Lunch!.Id); True(lunch.Pairs[2].ReasonAutomatic); Equal(4, lunch.Sections.Count);
    var coffee = Build([new BreakNote(At(16,"11:32"), At(16,"11:51"), BreakReasons.Coffee)]);
    True(coffee.Lunch?.Id != coffee.Pairs[1].Id);
    lunches["2026-10-16"] = day.Pairs[1].Id;
    Equal(day.Pairs[1].Id, Build([new BreakNote(At(16,"14:00"), At(16,"14:40"), BreakReasons.Lunch)]).Lunch!.Id);
    lunches.Clear();
    var meetingDay = Build([standup], config: settings with { IgnoreDailyMeeting = true, MeetingStartFrom = new(9,0), MeetingStartUntil = new(9,10) });
    Equal(meetingDay.Pairs[0].Id, meetingDay.Meeting!.Id);
});

Check("Other work counts as work with its own text; unanswered prompt records never set a reason", () =>
{
    True(BreakReasons.CountsAsWork(BreakReasons.Work) && BreakReasons.HasText(BreakReasons.Work) && !BreakReasons.CountsAsWork(BreakReasons.Other));
    Equal("work:Megbeszélés", BreakReasons.Encode("work", " Megbeszélés ")); Equal(("work", "Megbeszélés"), BreakReasons.Parse("work:Megbeszélés"));
    Equal(BreakReasons.Other, BreakReasons.Codes[^1]);
    List<LockEvent> Events() => [Event(16,"08:00",false), Event(16,"14:00",true), Event(16,"14:40",false), Event(16,"17:00",true)];
    var list = Events();
    var work = WorkReports.Build(list, settings, lunches, now, zone, breaks: [new BreakNote(At(16,"14:00"), At(16,"14:40"), BreakReasons.Work, "Megbeszélés")]).Single();
    Equal(1, work.Sections.Count); Equal("Megbeszélés", work.Pairs[0].ReasonText);
    var open = WorkReports.Build(list, settings, lunches, now, zone, breaks: [new BreakNote(At(16,"14:00"), At(16,"14:40"), "", Suggestion: BreakReasons.Meeting)]).Single();
    True(open.Pairs[0].Reason is null); Equal(2, open.Sections.Count);
});
Check("A manual no-reason choice keeps a pair out of the lunch guess", () =>
{
    var events = Day(16).ToList();
    var plain = WorkReports.Build(events, settings, lunches, now, zone).Single(d => d.Date == new DateOnly(2026,10,16));
    var guessed = plain.Lunch!;
    var excluded = WorkReports.Build(events, settings, lunches, now, zone, reasons: new Dictionary<string, string> { [guessed.Id] = "none" })
        .Single(d => d.Date == new DateOnly(2026,10,16));
    True(excluded.Lunch?.Id != guessed.Id); True(excluded.Pairs.Single(p => p.Id == guessed.Id).ReasonManual);
});
Check("Daily goal, week start and suggestion lists validate", () =>
{
    Equal(480, settings.DailyGoalMinutes); True(settings.GoalNotification); Equal(DayOfWeek.Monday, settings.WeekStart);
    Equal("Megbeszélés", settings.WorkSuggestions.Single()); Equal(0, settings.BreakSuggestions.Length);
    (settings with { DailyGoalMinutes = 0, WeekStart = DayOfWeek.Sunday }).Validate();
    Throws<ArgumentException>(() => (settings with { DailyGoalMinutes = 1441 }).Validate());
    Throws<ArgumentException>(() => (settings with { WeekStart = (DayOfWeek)9 }).Validate());
    Throws<ArgumentException>(() => (settings with { BreakSuggestions = [" Orvos"] }).Validate());
    Throws<ArgumentException>(() => (settings with { WorkSuggestions = Enumerable.Repeat("x", 21).ToArray() }).Validate());
    Throws<ArgumentException>(() => (settings with { WorkSuggestions = [""] }).Validate());
});
Check("Weekly and monthly summaries measure complete days against the goal and break time down by reason", () =>
{
    Equal("7:59", WorkSummary.Clock(TimeSpan.FromSeconds(7 * 3600 + 59 * 60 + 29))); Equal("-0:30", WorkSummary.Clock(TimeSpan.FromMinutes(-30))); Equal("0:00", WorkSummary.Clock(TimeSpan.FromSeconds(-20)));
    Equal(new DateOnly(2026,10,12), WorkSummary.WeekStart(new(2026,10,16), DayOfWeek.Monday));
    Equal(new DateOnly(2026,10,11), WorkSummary.WeekStart(new(2026,10,16), DayOfWeek.Sunday));
    Equal(new DateOnly(2026,10,18), WorkSummary.WeekStart(new(2026,10,18), DayOfWeek.Sunday));
    // Fri 16 (complete), Sun 18 (complete), Mon 19 (incomplete: missing first unlock).
    var events = Day(16).Concat([Event(18,"09:00",false), Event(18,"10:00",true), Event(18,"10:30",false), Event(18,"12:00",true)])
        .Concat([Event(19,"09:00",true), Event(19,"09:30",false)]).ToList();
    var notes = new[] { new BreakNote(At(18,"10:00"), At(18,"10:30"), BreakReasons.Coffee) };
    var days = WorkReports.Build(events, settings, lunches, now, zone, breaks: notes);
    var weeks = WorkSummary.Weeks(days, settings, new DateOnly(2026,10,14), new DateOnly(2026,10,19));
    Equal(2, weeks.Count); Equal(new DateOnly(2026,10,19), weeks[0].From); True(weeks[0].Ongoing); Equal(0, weeks[0].Days); Equal(1, weeks[0].IncompleteDays);
    var week = weeks[1];
    Equal(new DateOnly(2026,10,12), week.From); True(week.Partial); Equal(2, week.Days);
    var friday = days.Single(d => d.Date == new DateOnly(2026,10,16));
    Equal(WorkSummary.Worked(friday) + TimeSpan.FromMinutes(150), week.Worked);
    Equal(TimeSpan.FromMinutes(960), week.Expected); Equal(week.Worked - week.Expected, week.Balance);
    Equal(friday.Lunch!.Duration, week.Reasons[BreakReasons.Lunch]); Equal(TimeSpan.FromMinutes(30), week.Reasons[BreakReasons.Coffee]);
    var sundayWeeks = WorkSummary.Weeks(days, settings with { WeekStart = DayOfWeek.Sunday }, new DateOnly(2026,10,14), new DateOnly(2026,10,19));
    Equal(new DateOnly(2026,10,18), sundayWeeks[0].From); Equal(1, sundayWeeks[0].Days); Equal(1, sundayWeeks[0].IncompleteDays);
    var months = WorkSummary.Months(days, settings with { DailyGoalMinutes = 0 }, new DateOnly(2026,10,14), new DateOnly(2026,10,19));
    Equal(1, months.Count); Equal(new DateOnly(2026,10,31), months[0].To); Equal(TimeSpan.Zero, months[0].Expected); Equal(2, months[0].Days);
});

var fixtureRoot = Path.Combine(Path.GetTempPath(), "CompUsage-Checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
string Fixture(string name)
{
    var path = Path.Combine(fixtureRoot,name+".xlsx");
    using var book=new XLWorkbook();var sheet=book.AddWorksheet("Details");
    var headers=new[]{"Date","From (HH:MM)","To (HH:MM)","Duration (HH:MM)","Duration (H)","Details"};
    for(var c=1;c<=headers.Length;c++)sheet.Cell(1,c).Value=headers[c-1];
    sheet.Cell(2,1).Value=new DateTime(2026,10,14);sheet.Cell(2,2).Value=TimeSpan.FromHours(8)+TimeSpan.FromMinutes(11);
    sheet.Cell(2,4).FormulaA1="MOD(C2-B2,1)";sheet.Cell(2,5).FormulaA1="D2*24";sheet.Cell(2,6).Value="Existing description";
    sheet.Column(1).Style.DateFormat.Format="yyyy.mm.dd";sheet.Columns(2,4).Style.DateFormat.Format="hh:mm";
    sheet.Cell(2,1).Style.Fill.BackgroundColor=XLColor.LightBlue;
    var daily=book.AddWorksheet("Daily");daily.Cell(1,1).Value="Do not change";daily.Cell(2,1).FormulaA1="SUM(Details!E2:E100)";
    book.SaveAs(path);return path;
}
var service = new WorkbookService(zone);
Check("Excel display follows regional separators without rounding the underlying time",()=>{var display=new ExcelDisplay(new WorkbookFormats("yyyy/mm/dd;@","h:mm","h:mm"),huCulture,zone);Equal("2026.10.16",display.Date(new(2026,10,16)));Equal("8:01",display.Time(At(16,"08:01:37")));Equal("11:51",display.Time(At(16,"11:51:34"),true));Equal("23:59",display.Time(At(16,"23:59:59")));Equal("0:19",display.Duration(TimeSpan.FromSeconds(1169)));Equal("2026.10.16\t08:01:37\t11:51:34",display.CopyRow(new(new(2026,10,16),At(16,"08:01:37"),At(16,"11:51:34"))));});
Check("Excel display preserves literal separators and separate From/To formats",()=>{var us=new ExcelDisplay(new WorkbookFormats("yyyy/mm/dd","h:mm","hh:mm:ss"),CultureInfo.GetCultureInfo("en-US"),zone);Equal("2026/10/16",us.Date(new(2026,10,16)));Equal("8:01",us.Time(At(16,"08:01:37")));Equal("08:01:37",us.Time(At(16,"08:01:37"),true));var literal=new ExcelDisplay(new WorkbookFormats("yyyy\"/\"mm\"/\"dd","h:mm AM/PM"),huCulture,zone);Equal("2026/10/16",literal.Date(new(2026,10,16)));True(literal.Time(At(16,"08:01:37")).StartsWith("8:01",StringComparison.Ordinal));});
var allEvents = Day(14,"08:11:06","17:03:55").Concat(Day(15)).Concat(Day(16)).Concat(Day(19).SkipLast(1)).ToArray();
var reports = WorkReports.Build(allEvents,settings,lunches,now,zone);
Dictionary<string,byte[]> Entries(string path) { using var z=ZipFile.OpenRead(path);return z.Entries.ToDictionary(e=>e.FullName,e=>{using var s=e.Open();using var m=new MemoryStream();s.CopyTo(m);return m.ToArray();}); }
try
{
    Check("Activity journals are opt-in, retain independent sessions and prune old lock observations",()=>
    {
        var journal = new ActivityJournal(Path.Combine(fixtureRoot,"activity"));
        True(!journal.Configuration().Enabled);
        journal.Configure(true); True(journal.Configuration().Enabled);
        var first = new ActivitySession {SessionId="1",StartedAt=now,HeartbeatAt=now,Locks=[new(now,null,null,"1"),new(now.AddDays(-62),null,null,"1")]};
        journal.Save(first,now);
        journal.Heartbeat(first,now.AddSeconds(5));
        Equal(now.AddSeconds(5),journal.Load("1").HeartbeatAt); Equal(1,journal.Load("1").Locks.Count);
        journal.Save(new ActivitySession {SessionId="2",StartedAt=now,HeartbeatAt=now},now);
        Equal(2,journal.ReadAll().Count); Equal(1,journal.Load("1").Locks.Count);
        journal.Configure(false); True(!journal.Configuration().Enabled); Equal(1,journal.Load("1").Locks.Count);
        Throws<ArgumentException>(()=>journal.Load("../state"));
    });
    Check("Break notes and the open break survive status updates and are pruned with the lock history",()=>
    {
        var journal = new ActivityJournal(Path.Combine(fixtureRoot,"breaks"));
        var session = new ActivitySession {SessionId="3",StartedAt=now,HeartbeatAt=now,LockedAt=now.AddMinutes(-3),
            Breaks=[new(now.AddMinutes(-40),now.AddMinutes(-20),BreakReasons.Coffee),new(now.AddDays(-62),now.AddDays(-62),BreakReasons.Lunch)]};
        journal.Save(session,now);
        journal.Heartbeat(session,now.AddSeconds(5));
        var loaded = journal.Load("3");
        Equal(1,loaded.Breaks.Count); Equal(BreakReasons.Coffee,loaded.Breaks[0].Reason); Equal(now.AddMinutes(-3),loaded.LockedAt);
        loaded.LockedAt = null; journal.Heartbeat(loaded,now.AddSeconds(10));
        True(journal.Load("3").LockedAt is null); Equal(1,journal.Load("3").Breaks.Count);
        loaded.SessionEnded = true; loaded.StoppedAt = now.AddSeconds(20); journal.Heartbeat(loaded,now.AddSeconds(20));
        True(journal.Load("3").SessionEnded); Equal(now.AddSeconds(20),journal.Load("3").StoppedAt);
        loaded.GoalNotifiedOn = "2026-10-19"; loaded.Breaks.Add(new(now.AddMinutes(-15),now.AddMinutes(-1),"",Suggestion:BreakReasons.Meeting));
        journal.Save(loaded,now.AddSeconds(25)); journal.Heartbeat(loaded,now.AddSeconds(25));
        Equal("2026-10-19",journal.Load("3").GoalNotifiedOn); Equal(BreakReasons.Meeting,journal.Load("3").Breaks[^1].Suggestion);
    });
    Check("Excel writes only Date/From/To: durations and Details stay to the workbook, also when completing a row",()=>
    {
        var path=Fixture("columns-abc");
        var day=Report(Day(14,"08:11:24","17:03:55"));
        var notes=new Dictionary<string,string>{{day.Sections[0].Id,"Not written"},{day.Sections[1].Id,"=2+2"}};
        var days=WorkReports.Build(Day(14,"08:11:24","17:03:55").Concat(Day(15)),settings,lunches,now,zone,comments:notes);
        var plan=service.Preview(path,days);Equal(1,plan.Partials.Count);True(plan.Rows.Count>0);
        service.Commit(plan,[2]);
        using var book=new XLWorkbook(path);var sheet=book.Worksheet("Details");
        True(Math.Abs((At(14,"11:32:05").TimeOfDay-sheet.Cell(2,3).GetTimeSpan()).TotalMilliseconds)<1);
        Equal("MOD(C2-B2,1)",sheet.Cell(2,4).FormulaA1);Equal("D2*24",sheet.Cell(2,5).FormulaA1);Equal("Existing description",sheet.Cell(2,6).GetString());
        var last=sheet.LastRowUsed()!.RowNumber();True(last>=3);
        for(var r=3;r<=last;r++){True(!sheet.Cell(r,1).IsEmpty()&&!sheet.Cell(r,2).IsEmpty());True(sheet.Cell(r,4).IsEmpty()&&sheet.Cell(r,5).IsEmpty()&&sheet.Cell(r,6).IsEmpty());}
    });
    Check("Rows with prefilled duration formulas receive the new Date/From/To and keep their formulas",()=>
    {
        var path=Fixture("prefilled");
        using(var b=new XLWorkbook(path)){var sh=b.Worksheet("Details");for(var r=3;r<=12;r++){sh.Cell(r,4).FormulaA1=$"IF(C{r}=\"\",\"\",MOD(C{r}-B{r},1))";sh.Cell(r,5).FormulaA1=$"IF(D{r}=\"\",\"\",D{r}*24)";}b.Save();}
        var plan=service.Preview(path,reports);Equal(2,plan.LastRow);True(plan.Rows.Count>0);
        service.Commit(plan,[]);
        using var book=new XLWorkbook(path);var sheet=book.Worksheet("Details");
        for(var r=3;r<3+plan.Rows.Count;r++){True(!sheet.Cell(r,1).IsEmpty()&&!sheet.Cell(r,2).IsEmpty());Equal($"IF(C{r}=\"\",\"\",MOD(C{r}-B{r},1))",sheet.Cell(r,4).FormulaA1);Equal($"IF(D{r}=\"\",\"\",D{r}*24)",sheet.Cell(r,5).FormulaA1);}
        True(sheet.Cell(3+plan.Rows.Count,1).IsEmpty());
    });
    Check("Excel append keeps the daily meeting inside a continuous row and still excludes lunch",()=>
    {
        var path=Fixture("meeting-write");
        var days=WorkReports.Build(MeetingDay(),meetingSettings,lunches,now,zone);
        var plan=service.Preview(path,days);Equal(2,plan.Rows.Count);
        Equal(At(16,"08:00"),plan.Rows[0].From);Equal(At(16,"11:32:05"),plan.Rows[0].To);
        service.Commit(plan,[]);
        using var book=new XLWorkbook(path);var sheet=book.Worksheet("Details");
        True(Math.Abs((At(16,"08:00").TimeOfDay-sheet.Cell(3,2).GetTimeSpan()).TotalMilliseconds)<1);
        True(Math.Abs((At(16,"11:32:05").TimeOfDay-sheet.Cell(3,3).GetTimeSpan()).TotalMilliseconds)<1);
        True(Math.Abs((At(16,"11:51:34").TimeOfDay-sheet.Cell(4,2).GetTimeSpan()).TotalMilliseconds)<1);
    });
    Check("Excel append writes the selected activity endpoint with original seconds",()=>
    {
        var path=Fixture("activity-write");
        var list=new[]{Event(15,"08:00",false),Event(15,"18:00",true)};
        var observation=new ActivityLock(list[1].Timestamp,At(15,"17:00:37"),list[1].Timestamp.AddSeconds(-1),"1");
        var days=WorkReports.Build(list,settings with {LockTimeSource="activity"},lunches,now,zone,activity:[observation]);
        service.Commit(service.Preview(path,days),[]);
        using var result=new XLWorkbook(path);
        True(Math.Abs((At(15,"17:00:37").TimeOfDay-result.Worksheet("Details").Cell(3,3).GetTimeSpan()).TotalMilliseconds)<1);
    });
    Check("Workbook formats and built-in h:mm are preserved instead of replaced",()=>{var path=Fixture("formats");using(var b=new XLWorkbook(path)){var s=b.Worksheet("Details");s.Column(1).Style.NumberFormat.Format="yyyy/mm/dd;@";s.Columns(2,4).Style.NumberFormat.NumberFormatId=20;s.Cell(3,3).Style.NumberFormat.NumberFormatId=21;b.Save();}var p=service.Preview(path,reports);Equal("yyyy/mm/dd;@",p.Formats.Date);Equal("h:mm",p.Formats.Start);Equal("h:mm:ss",p.Formats.End);var display=new ExcelDisplay(p.Formats,huCulture,zone);Equal("8:01",display.Time(At(16,"08:01:37")));Equal("8:01:37",display.Time(At(16,"08:01:37"),true));service.Commit(p,[]);using var result=new XLWorkbook(path);var s2=result.Worksheet("Details");Equal("yyyy/mm/dd;@",s2.Cell(3,1).Style.NumberFormat.Format);Equal(20,s2.Cell(3,2).Style.NumberFormat.NumberFormatId);Equal(21,s2.Cell(3,3).Style.NumberFormat.NumberFormatId);});
    Check("A partial row matches an Excel-displayed minute while keeping the existing start",()=>{var path=Fixture("truncated-start");var days=WorkReports.Build(Day(14,"08:11:37","17:03:55").Concat(Day(15)),settings,lunches,now,zone);var p=service.Preview(path,days);Equal(1,p.Partials.Count);Equal("08:11:00",Presentation.Time(p.Partials[0].Interval.From,zone,true));});
    Check("Workbook preview appends only newer closed days and offers a rounded-start partial",()=>{var path=Fixture("preview");var p=service.Preview(path,reports);Equal(new DateOnly(2026,10,14),p.LastDate!.Value);Equal(6,p.Rows.Count);True(p.Rows[^1].Open&&!p.Rows[^2].Open);Equal(1,p.Partials.Count);Equal("08:11:00",Presentation.Time(p.Partials[0].Interval.From,zone,true));Equal("11:32:05",Presentation.Time(p.Partials[0].Interval.To,zone,true));Equal(1,p.Partials[0].Following.Count);Equal("11:51:34",Presentation.Time(p.Partials[0].Following[0].From,zone,true));});
    Check("Workbook commit keeps other sheets and descriptions byte-for-byte, creates a backup, and preserves seconds",()=>{var path=Fixture("write");var original=File.ReadAllBytes(path);var before=Entries(path);var p=service.Preview(path,reports);var backup=service.Commit(p,[2]);True(original.SequenceEqual(File.ReadAllBytes(backup)));var after=Entries(path);True(before["xl/worksheets/sheet2.xml"].SequenceEqual(after["xl/worksheets/sheet2.xml"]));using var result=new XLWorkbook(path);var s=result.Worksheet("Details");Equal("Existing description",s.Cell(2,6).GetString());True(s.Cell(2,4).HasFormula);Equal("MOD(C2-B2,1)",s.Cell(2,4).FormulaA1);True(Math.Abs((At(14,"11:51:34").TimeOfDay-s.Cell(3,2).GetTimeSpan()).TotalMilliseconds)<1);Equal(new DateTime(2026,10,14),s.Cell(3,1).GetDateTime());True(Math.Abs((At(15,"08:02:14").TimeOfDay-s.Cell(4,2).GetTimeSpan()).TotalMilliseconds)<1);Equal("yyyy.mm.dd",s.Cell(3,1).Style.NumberFormat.Format);Equal("hh:mm",s.Cell(3,2).Style.NumberFormat.Format);Equal(0,service.Preview(path,reports).Rows.Count);Equal(0,service.Preview(path,reports).Partials.Count);});
    Check("Stale preview cannot overwrite an intervening workbook edit",()=>{var path=Fixture("stale");var p=service.Preview(path,reports);using(var b=new XLWorkbook(path)){b.Worksheet("Details").Cell(2,6).Value="New external edit";b.Save();}var changed=File.ReadAllBytes(path);Throws<IOException>(()=>service.Commit(p,[]));True(changed.SequenceEqual(File.ReadAllBytes(path)));});
    Check("An Excel file lock prevents any write",()=>{var path=Fixture("locked");var p=service.Preview(path,reports);using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);Throws<IOException>(()=>service.Commit(p,[]));});
    Check("Shares that deny ReplaceFile still get an exact backup and a one-step rename, never a stale overwrite",()=>{
        var dir=Directory.CreateTempSubdirectory("cu-rename").FullName;
        try{
            string F(string n)=>Path.Combine(dir,n);string Hash(string p)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));
            File.WriteAllText(F("book.xlsx"),"original");File.WriteAllText(F("new.tmp"),"updated");var fingerprint=Hash(F("book.xlsx"));
            WorkbookService.ReplaceByRename(F("new.tmp"),F("book.xlsx"),F("backup.xlsx"),fingerprint);
            Equal("updated",File.ReadAllText(F("book.xlsx")));Equal("original",File.ReadAllText(F("backup.xlsx")));True(!File.Exists(F("new.tmp")));
            File.WriteAllText(F("new.tmp"),"again");
            Throws<IOException>(()=>WorkbookService.ReplaceByRename(F("new.tmp"),F("book.xlsx"),F("backup2.xlsx"),fingerprint));
            Equal("updated",File.ReadAllText(F("book.xlsx")));True(!File.Exists(F("backup2.xlsx")));
            File.Move(F("book.xlsx"),F("backup3.xlsx"));
            WorkbookService.ReplaceByRename(F("new.tmp"),F("book.xlsx"),F("backup3.xlsx"),fingerprint);Equal("again",File.ReadAllText(F("book.xlsx")));
            // A held original keeps the workbook unchanged and leaves no stray backup behind.
            File.WriteAllText(F("new.tmp"),"held");var heldFingerprint=Hash(F("book.xlsx"));
            using(new FileStream(F("book.xlsx"),FileMode.Open,FileAccess.Read,FileShare.Read))
                Throws<Exception>(()=>WorkbookService.ReplaceByRename(F("new.tmp"),F("book.xlsx"),F("backup4.xlsx"),heldFingerprint));
            Equal("again",File.ReadAllText(F("book.xlsx")));True(!File.Exists(F("backup4.xlsx")));
            File.WriteAllText(F("new.tmp"),"x");Throws<FileNotFoundException>(()=>WorkbookService.ReplaceByRename(F("new.tmp"),F("missing.xlsx"),F("none.xlsx"),fingerprint));
        }finally{Directory.Delete(dir,true);}
    });
    Check("A partial start within three minutes continues today's ongoing day up to the latest data",()=>{
        string Partial(string name,int minute){var path=Fixture(name);using(var b=new XLWorkbook(path)){var s=b.Worksheet("Details");s.Cell(2,1).Value=new DateTime(2026,10,19);s.Cell(2,2).Value=TimeSpan.FromHours(8)+TimeSpan.FromMinutes(minute);b.Save();}return path;}
        var path=Partial("ongoing-partial",0);var p=service.Preview(path,reports);
        Equal(1,p.Partials.Count);Equal("08:00:00",Presentation.Time(p.Partials[0].Interval.From,zone,true));Equal("11:32:05",Presentation.Time(p.Partials[0].Interval.To,zone,true));
        Equal(1,p.Partials[0].Following.Count);True(p.Partials[0].Following[0].Open);Equal(now,p.Partials[0].Following[0].To);
        service.Commit(p,[2]);
        using(var result=new XLWorkbook(path)){var s=result.Worksheet("Details");
            True(Math.Abs((At(19,"11:32:05").TimeOfDay-s.Cell(2,3).GetTimeSpan()).TotalMilliseconds)<1);
            True(Math.Abs((At(19,"11:51:34").TimeOfDay-s.Cell(3,2).GetTimeSpan()).TotalMilliseconds)<1);
            True(s.Cell(3,3).IsEmpty()&&s.Cell(3,4).IsEmpty()&&s.Cell(3,5).IsEmpty());True(s.Cell(4,1).IsEmpty());}
        var again=service.Preview(path,reports);Equal(0,again.Partials.Count);Equal(0,again.Rows.Count);
        Equal(0,service.Preview(Partial("far-partial",-2),reports).Partials.Count);
        Equal(1,service.Preview(Partial("late-partial",5),reports).Partials.Count);
    });    Check("Today is appended with a blank open end, then completed and continued on a later append",()=>{
        var path=Fixture("today-open");using(var b=new XLWorkbook(path)){b.Worksheet("Details").Cell(2,3).Value=TimeSpan.FromHours(17);b.Save();}
        var today=WorkReports.Build(Day(19).SkipLast(1),settings,lunches,now,zone);
        var p=service.Preview(path,today);Equal(2,p.Rows.Count);service.Commit(p,[]);
        using(var b=new XLWorkbook(path)){var s=b.Worksheet("Details");True(!s.Cell(3,3).IsEmpty());
            True(Math.Abs((At(19,"11:51:34").TimeOfDay-s.Cell(4,2).GetTimeSpan()).TotalMilliseconds)<1);True(s.Cell(4,3).IsEmpty()&&s.Cell(4,4).IsEmpty());}
        var later=now.AddHours(3);var events=Day(19).SkipLast(1).Concat([Event(19,"15:00",true),Event(19,"15:30",false)]);
        var days=WorkReports.Build(events,settings,lunches,later,zone);p=service.Preview(path,days);
        Equal(0,p.Rows.Count);Equal(1,p.Partials.Count);Equal(4,p.Partials[0].Row);True(p.Partials[0].CompletesRow);
        Equal("15:00:00",Presentation.Time(p.Partials[0].Interval.To,zone,true));Equal(1,p.Partials[0].Following.Count);True(p.Partials[0].Following[0].Open);
        service.Commit(p,[4]);
        using(var b=new XLWorkbook(path)){var s=b.Worksheet("Details");True(Math.Abs((At(19,"15:00").TimeOfDay-s.Cell(4,3).GetTimeSpan()).TotalMilliseconds)<1);
            True(Math.Abs((At(19,"15:30").TimeOfDay-s.Cell(5,2).GetTimeSpan()).TotalMilliseconds)<1);True(s.Cell(5,3).IsEmpty());}
    });
    Check("A complete last row written during a break continues with the day's later sections",()=>{
        var path=Fixture("break-continue");using(var b=new XLWorkbook(path)){var s=b.Worksheet("Details");s.Cell(2,1).Value=new DateTime(2026,10,19);s.Cell(2,2).Value=TimeSpan.Parse("08:02:14");s.Cell(2,3).Value=TimeSpan.Parse("11:32:05");b.Save();}
        var p=service.Preview(path,reports);Equal(0,p.Rows.Count);Equal(1,p.Partials.Count);var c=p.Partials[0];
        True(!c.CompletesRow);Equal(2,c.Row);Equal("11:51:34",Presentation.Time(c.Interval.From,zone,true));True(c.Interval.Open);Equal(0,c.Following.Count);
        service.Commit(p,[2]);
        using(var b=new XLWorkbook(path)){var s=b.Worksheet("Details");True(Math.Abs((At(19,"11:32:05").TimeOfDay-s.Cell(2,3).GetTimeSpan()).TotalMilliseconds)<1);
            True(Math.Abs((At(19,"11:51:34").TimeOfDay-s.Cell(3,2).GetTimeSpan()).TotalMilliseconds)<1);True(s.Cell(3,3).IsEmpty());}
    });    Check("Unselected partial remains unchanged",()=>{var path=Fixture("partial");service.Commit(service.Preview(path,reports),[]);using var b=new XLWorkbook(path);True(b.Worksheet("Details").Cell(2,3).IsEmpty());});
    Check("Incorrect Details schema fails before modification",()=>{var path=Fixture("schema");using(var b=new XLWorkbook(path)){b.Worksheet("Details").Cell(1,1).Value="Wrong";b.Save();}Throws<InvalidDataException>(()=>service.Preview(path,reports));});
    Check("1904 date workbooks retain their epoch",()=>{var path=Fixture("epoch");using(var z=ZipFile.Open(path,ZipArchiveMode.Update)){XNamespace n="http://schemas.openxmlformats.org/spreadsheetml/2006/main";var e=z.GetEntry("xl/workbook.xml")!;XDocument book;using(var s=e.Open())book=XDocument.Load(s);var prop=book.Root!.Element(n+"workbookPr");if(prop is null){prop=new XElement(n+"workbookPr");book.Root.AddFirst(prop);}prop.SetAttributeValue("date1904",1);e.Delete();using(var s=z.CreateEntry("xl/workbook.xml").Open())book.Save(s);e=z.GetEntry("xl/worksheets/sheet1.xml")!;XDocument sheet;using(var s=e.Open())sheet=XDocument.Load(s);sheet.Descendants(n+"c").Single(c=>(string?)c.Attribute("r")=="A2").Element(n+"v")!.Value=(new DateTime(2026,10,14).ToOADate()-1462).ToString(CultureInfo.InvariantCulture);e.Delete();using(var s=z.CreateEntry("xl/worksheets/sheet1.xml").Open())sheet.Save(s);}var p=service.Preview(path,reports);True(p.Date1904);Equal(new DateOnly(2026,10,14),p.LastDate!.Value);service.Commit(p,[]);Equal(new DateOnly(2026,10,19),service.Preview(path,reports).LastDate!.Value);});
    Check("Empty workbook data can be appended without a prior date",()=>{var path=Fixture("empty");using(var b=new XLWorkbook(path)){b.Worksheet("Details").Row(2).Clear();b.Save();}var p=service.Preview(path,reports);True(p.LastDate is null);service.Commit(p,[]);using var result=new XLWorkbook(path);Equal("yyyy.mm.dd",result.Worksheet("Details").Cell(2,1).Style.NumberFormat.Format);});
}
finally
{
    // The generated fixture directory is unique to this test run; never use a user workbook here.
    Directory.Delete(fixtureRoot,true);
}
Console.WriteLine($"{count} checks passed.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}
