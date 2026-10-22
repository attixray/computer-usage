using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Resources;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using CompUsage.App.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args is ["--activate-instance", var instanceName])
            {
                using var instance = new SingleInstance(instanceName);
                Require(!instance.IsPrimary);
                instance.RequestActivation();
                return 0;
            }
            var checks = 0;
            void Check(string name, Action test) { test(); checks++; Console.WriteLine("PASS " + name); }
            const string sid = "S-1-5-21-123-456-789-1001";
            Check("Legacy settings keep daily meetings disabled and manual meeting choices survive a JSON round trip", () =>
            {
                var legacy=System.Text.Json.JsonSerializer.Deserialize<LocalState>("{\"Settings\":{\"ExpectedLunch\":\"11:30:00\"}}")!;
                Require(!legacy.Settings.IgnoreDailyMeeting && legacy.Meetings.Count==0);
                Require(legacy.Settings.MeetingStartFrom==new TimeOnly(9,25) && legacy.Settings.MeetingStartUntil==new TimeOnly(9,40));
                Require(legacy.Settings.MaximumMeetingMinutes==20);
                legacy.Settings=legacy.Settings with {IgnoreDailyMeeting=true};legacy.Meetings["2026-10-16"]="example-pair";
                var loaded=System.Text.Json.JsonSerializer.Deserialize<LocalState>(System.Text.Json.JsonSerializer.Serialize(legacy))!;
                Require(loaded.Settings.IgnoreDailyMeeting && loaded.Meetings["2026-10-16"]=="example-pair");
                loaded.Settings=loaded.Settings with {IgnoreDailyMeeting=false};
                Require(loaded.Meetings["2026-10-16"]=="example-pair");
                Require(legacy.SectionComments.Count==0);
                loaded.SectionComments["2026-10-16|123"]="Árvíztűrő\nReview";
                var comments=System.Text.Json.JsonSerializer.Deserialize<LocalState>(System.Text.Json.JsonSerializer.Serialize(loaded))!;
                Require(comments.SectionComments["2026-10-16|123"]=="Árvíztűrő\nReview");
            });
            Check("The background monitor's shared state carries the logging gaps, so its daily goal sees the same incomplete day", () =>
            {
                var file=Path.Combine(Path.GetTempPath(),"CompUsage-shared-"+Guid.NewGuid().ToString("N")+".json");
                try
                {
                    var saved=new LocalState();var from=new DateTimeOffset(2026,10,19,10,0,0,TimeSpan.FromHours(2));
                    saved.Gaps.Add(new(from,from.AddHours(1)));saved.Gaps.Add(new(from.AddHours(3),null));
                    File.WriteAllText(file,System.Text.Json.JsonSerializer.Serialize(saved));
                    var shared=LocalStore.ReadShared(file);
                    Require(shared.Gaps is [var closed,var open] && closed.From==from && closed.To==from.AddHours(1) && open.To is null);
                    Require(LocalStore.ReadShared(file+".missing").Gaps is null or { Count: 0 });
                }
                finally { File.Delete(file); }
            });
            Check("A restart treats a monitor without a fresh heartbeat as stuck and never ends a foreign process", () =>
            {
                var now=DateTimeOffset.UtcNow;
                Require(ActivityMonitor.Healthy(new() { HeartbeatAt=now.AddSeconds(-5) },now));
                Require(!ActivityMonitor.Healthy(new() { HeartbeatAt=now.AddMinutes(-2) },now));
                Require(!ActivityMonitor.Healthy(new() { HeartbeatAt=now, StoppedAt=now },now));
                using var foreign=Process.Start(new ProcessStartInfo("ping.exe","-n 30 127.0.0.1") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true })!;
                try { Require(!ActivityMonitor.EndMonitorProcess(foreign.Id) && !foreign.HasExited); Require(!ActivityMonitor.EndMonitorProcess(Environment.ProcessId)); }
                finally { foreign.Kill(); foreign.WaitForExit(); }
            });
            Check("A second launch queues activation during startup and can activate the existing instance repeatedly", () =>
            {
                var name = "CompUsage.Check." + Guid.NewGuid().ToString("N");
                using (var primary = new SingleInstance(name))
                {
                    using var secondary = new SingleInstance(name);
                    Require(primary.IsPrimary && !secondary.IsPrimary);
                    void LaunchAgain()
                    {
                        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                        start.ArgumentList.Add("--activate-instance"); start.ArgumentList.Add(name);
                        using var child = Process.Start(start) ?? throw new Exception("The second instance could not start.");
                        Require(child.WaitForExit(5000) && child.ExitCode == 0);
                    }
                    LaunchAgain();
                    using var received = new AutoResetEvent(false);
                    primary.Listen(() => received.Set());
                    Require(received.WaitOne(TimeSpan.FromSeconds(5)));
                    LaunchAgain();
                    Require(received.WaitOne(TimeSpan.FromSeconds(5)));
                }
                using var next = new SingleInstance(name);
                Require(next.IsPrimary);
            });
            Check("Read delegation adds only the requested account and access bit, preserving existing ACL entries", () =>
            {
                const string before = "O:BAG:SYD:(D;;0x4;;;WD)(A;;0x7;;;SY)(A;;0x7;;;BA)";
                var after = ReadAccessService.AddReadAccess(before, sid, ReadAccessService.EventLogRead);
                var parsed = new RawSecurityDescriptor(after);
                Require(parsed.DiscretionaryAcl!.Count == 4);
                var grant = (CommonAce)parsed.DiscretionaryAcl[3];
                Require(grant.AccessMask == 1 && grant.SecurityIdentifier.Value == sid);
                var original = new RawSecurityDescriptor(before);
                Require(parsed.Owner == original.Owner && parsed.Group == original.Group);
                for (var i = 0; i < 3; i++)
                {
                    var a = new byte[parsed.DiscretionaryAcl[i].BinaryLength]; parsed.DiscretionaryAcl[i].GetBinaryForm(a, 0);
                    var b = new byte[original.DiscretionaryAcl![i].BinaryLength]; original.DiscretionaryAcl[i].GetBinaryForm(b, 0);
                    Require(a.SequenceEqual(b));
                }
                Require(ReadAccessService.AddReadAccess(after, sid, 1) == after);
                var audit = new RawSecurityDescriptor(ReadAccessService.AddReadAccess(before, sid, ReadAccessService.AuditQuerySystemPolicy));
                Require(((CommonAce)audit.DiscretionaryAcl![3]).AccessMask == 2);
            });
            Check("Read delegation respects explicit deny rules and does not replace null DACLs", () =>
            {
                try { ReadAccessService.AddReadAccess($"D:(D;;0x1;;;{sid})", sid, 1); throw new Exception("Expected denial."); }
                catch (UnauthorizedAccessException) { }
                const string nullAcl = "O:BAG:SY";
                Require(ReadAccessService.AddReadAccess(nullAcl, sid, 1) == nullAcl);
            });
            Check("A denied read cannot launch a UAC helper", () =>
            {
                var launches = 0;
                using var broker = new SecurityBroker(_ => throw new UnauthorizedAccessException(), _ => { launches++; throw new Exception("Unexpected helper launch."); });
                try { broker.RequestAsync("read").GetAwaiter().GetResult(); throw new Exception("Expected denial."); }
                catch (UnauthorizedAccessException) { }
                Require(launches == 0);
            });
            Check("An explicit denied Windows configuration change can request a UAC helper", () =>
            {
                var launches = 0;
                using var broker = new SecurityBroker(_ => throw new Win32Exception(1314), info =>
                { Require(info.Verb == "runas"); launches++; throw new OperationCanceledException(); });
                try { broker.RequestAsync("grantRead").GetAwaiter().GetResult(); throw new Exception("Expected intercepted launch."); }
                catch (OperationCanceledException) { }
                Require(launches == 1);
            });
            Check("Real read returns data or a permission-required result without a helper", () =>
            {
                using var broker = new SecurityBroker(launch: _ => throw new Exception("Read attempted UAC."));
                var result = broker.RequestAsync("read").GetAwaiter().GetResult();
                Require(result.Data is not null || result.ReadAccessRequired);
            });
            Check("The application embeds a decodable small and large Windows icon", () =>
            {
                using var resources = typeof(CompUsage.App.MainWindow).Assembly.GetManifestResourceStream("CompUsage.g.resources")
                    ?? throw new Exception("Application resources are missing.");
                using var reader = new ResourceReader(resources);
                var entry = reader.Cast<System.Collections.DictionaryEntry>().Single(e => (string)e.Key == "assets/compusage.ico");
                using var source = (System.IO.Stream)entry.Value!;
                var icon = new IconBitmapDecoder(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Require(icon.Frames.Any(f => f.PixelWidth == 16) && icon.Frames.Any(f => f.PixelWidth == 256));
            });
            Check("Windows accepts light, dark and high-contrast native caption settings", () =>
            {
                using var window = new HwndSource(new HwndSourceParameters("CompUsage hidden titlebar check") { WindowStyle = 0x00CF0000 });
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
                Require(WindowTheme.ApplyTo(window.Handle, true) == 0);
                Require(WindowTheme.ApplyTo(window.Handle, false) == 0);
                Require(WindowTheme.ApplyTo(window.Handle, true, true) == 0);
            });
            Console.WriteLine($"{checks} Windows checks passed (no UAC, policy changes, visible window or workbook writes).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Require(bool value) { if (!value) throw new Exception("Assertion failed."); }
}
