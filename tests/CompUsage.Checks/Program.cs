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
Check("Day boundary and minute rollover", () => { Equal(new DateOnly(2026,10,15), WorkDayClock.GetDate(At(16,"03:59:59"),settings.DayStart,zone)); Equal(new DateOnly(2026,10,16), WorkDayClock.GetDate(At(16,"04:00"),settings.DayStart,zone)); Equal("08:02",Presentation.Time(At(16,"08:01:30"),zone)); Equal("08:01",Presentation.Time(At(16,"08:01:29"),zone)); Equal("00:00",Presentation.Time(At(16,"23:59:30"),zone)); });
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
var allEvents = Day(14,"08:11:06","17:03:55").Concat(Day(15)).Concat(Day(16)).Concat(Day(19).SkipLast(1)).ToArray();
var reports = WorkReports.Build(allEvents,settings,lunches,now,zone);
Dictionary<string,byte[]> Entries(string path) { using var z=ZipFile.OpenRead(path);return z.Entries.ToDictionary(e=>e.FullName,e=>{using var s=e.Open();using var m=new MemoryStream();s.CopyTo(m);return m.ToArray();}); }
try
{
    Check("Workbook preview appends only newer closed days and offers a rounded-start partial",()=>{var path=Fixture("preview");var p=service.Preview(path,reports);Equal(new DateOnly(2026,10,14),p.LastDate!.Value);Equal(4,p.Rows.Count);Equal(1,p.Partials.Count);Equal("08:11:00",Presentation.Time(p.Partials[0].Interval.From,zone,true));Equal("11:32:05",Presentation.Time(p.Partials[0].Interval.To,zone,true));});
    Check("Workbook commit keeps other sheets and descriptions byte-for-byte, creates a backup, and preserves seconds",()=>{var path=Fixture("write");var original=File.ReadAllBytes(path);var before=Entries(path);var p=service.Preview(path,reports);var backup=service.Commit(p,[2]);True(original.SequenceEqual(File.ReadAllBytes(backup)));var after=Entries(path);True(before["xl/worksheets/sheet2.xml"].SequenceEqual(after["xl/worksheets/sheet2.xml"]));using var result=new XLWorkbook(path);var s=result.Worksheet("Details");Equal("Existing description",s.Cell(2,6).GetString());True(s.Cell(2,4).HasFormula);Equal("MOD(C2-B2,1)",s.Cell(2,4).FormulaA1);True(Math.Abs((At(15,"08:02:14").TimeOfDay-s.Cell(3,2).GetTimeSpan()).TotalMilliseconds)<1);Equal("yyyy.mm.dd",s.Cell(3,1).Style.NumberFormat.Format);Equal("hh:mm",s.Cell(3,2).Style.NumberFormat.Format);Equal(0,service.Preview(path,reports).Rows.Count);Equal(0,service.Preview(path,reports).Partials.Count);});
    Check("Stale preview cannot overwrite an intervening workbook edit",()=>{var path=Fixture("stale");var p=service.Preview(path,reports);using(var b=new XLWorkbook(path)){b.Worksheet("Details").Cell(2,6).Value="New external edit";b.Save();}var changed=File.ReadAllBytes(path);Throws<IOException>(()=>service.Commit(p,[]));True(changed.SequenceEqual(File.ReadAllBytes(path)));});
    Check("An Excel file lock prevents any write",()=>{var path=Fixture("locked");var p=service.Preview(path,reports);using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);Throws<IOException>(()=>service.Commit(p,[]));});
    Check("Unselected partial remains unchanged",()=>{var path=Fixture("partial");service.Commit(service.Preview(path,reports),[]);using var b=new XLWorkbook(path);True(b.Worksheet("Details").Cell(2,3).IsEmpty());});
    Check("Incorrect Details schema fails before modification",()=>{var path=Fixture("schema");using(var b=new XLWorkbook(path)){b.Worksheet("Details").Cell(1,1).Value="Wrong";b.Save();}Throws<InvalidDataException>(()=>service.Preview(path,reports));});
    Check("1904 date workbooks retain their epoch",()=>{var path=Fixture("epoch");using(var z=ZipFile.Open(path,ZipArchiveMode.Update)){XNamespace n="http://schemas.openxmlformats.org/spreadsheetml/2006/main";var e=z.GetEntry("xl/workbook.xml")!;XDocument book;using(var s=e.Open())book=XDocument.Load(s);var prop=book.Root!.Element(n+"workbookPr");if(prop is null){prop=new XElement(n+"workbookPr");book.Root.AddFirst(prop);}prop.SetAttributeValue("date1904",1);e.Delete();using(var s=z.CreateEntry("xl/workbook.xml").Open())book.Save(s);e=z.GetEntry("xl/worksheets/sheet1.xml")!;XDocument sheet;using(var s=e.Open())sheet=XDocument.Load(s);sheet.Descendants(n+"c").Single(c=>(string?)c.Attribute("r")=="A2").Element(n+"v")!.Value=(new DateTime(2026,10,14).ToOADate()-1462).ToString(CultureInfo.InvariantCulture);e.Delete();using(var s=z.CreateEntry("xl/worksheets/sheet1.xml").Open())sheet.Save(s);}var p=service.Preview(path,reports);True(p.Date1904);Equal(new DateOnly(2026,10,14),p.LastDate!.Value);service.Commit(p,[]);Equal(new DateOnly(2026,10,16),service.Preview(path,reports).LastDate!.Value);});
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
