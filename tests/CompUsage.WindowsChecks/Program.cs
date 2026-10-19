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
    private static int Main()
    {
        try
        {
            var checks = 0;
            void Check(string name, Action test) { test(); checks++; Console.WriteLine("PASS " + name); }
            const string sid = "S-1-5-21-123-456-789-1001";
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
