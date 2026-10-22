using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CompUsage.Core;
using Microsoft.Win32;

namespace CompUsage.App.Services;

public static class ActivityMonitor
{
    private static string SessionId => Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture);
    public static ActivityJournal Journal => new(Path.Combine(LocalStore.DirectoryPath, "Activity"));
    private static string Scope(string? testDirectory) => testDirectory is null ? "CompUsage.Activity" : "CompUsage.Activity.Test." + Path.GetFileName(testDirectory);
    private static string MutexName(string? testDirectory) => @"Local\" + Scope(testDirectory) + "." + SessionId;
    private static string StopName(string? testDirectory) => MutexName(testDirectory) + ".Stop";

    public static bool IsRunning(string? testDirectory = null)
    {
        try { using var mutex = Mutex.OpenExisting(MutexName(testDirectory)); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }

    private static (DateTime Stamp, AppSettings? Value) savedSettings;

    // The monitor only reads the app's settings; the UI stays the only writer of state.json.
    public static AppSettings SavedSettings()
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(LocalStore.FilePath);
            if (savedSettings.Value is null || stamp != savedSettings.Stamp) savedSettings = (stamp, new LocalStore().Load().Settings);
            return savedSettings.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return savedSettings.Value ?? new(); }
    }

    // Activity tracking is opt-in; break prompts are on by default. Either one keeps the monitor running.
    public static bool Wanted() => Journal.Configuration().Enabled || SavedSettings().BreakPrompt;

    public static void Start()
    {
        if (!Wanted() || IsRunning()) return;
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--activity-monitor" }, UseShellExecute = false, CreateNoWindow = true });
    }

    public static async Task StartAndWaitAsync()
    {
        Start();
        for (var attempt = 0; attempt < 30 && Wanted(); attempt++)
        {
            var state = Journal.Load(SessionId);
            if (IsRunning() && state.StoppedAt is null && DateTimeOffset.UtcNow - state.HeartbeatAt < TimeSpan.FromSeconds(20)) return;
            await Task.Delay(100);
        }
    }

    public static bool RegisteredAtLogon()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("CompUsageActivity") is string value && value.Contains("--activity-monitor", StringComparison.Ordinal);
    }

    public static int Stop(string? testDirectory = null)
    {
        if (!IsRunning(testDirectory)) return 0;
        try { using var stop = EventWaitHandle.OpenExisting(StopName(testDirectory)); stop.Set(); }
        catch (WaitHandleCannotBeOpenedException) { return 5; }
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(12))
        {
            if (!IsRunning(testDirectory)) return 0;
            Thread.Sleep(50);
        }
        return 5;
    }

    public static int Run(string? testDirectory = null)
    {
        var journal = testDirectory is null ? Journal : new ActivityJournal(testDirectory);
        // Checks run with prompts off so they never show a window or touch the user's settings.
        Func<AppSettings> settings = testDirectory is null ? SavedSettings : () => new AppSettings { BreakPrompt = false };
        try { if (!journal.Configuration().Enabled && !settings().BreakPrompt) return 0; }
        catch { return 6; }
        using var mutex = new Mutex(true, MutexName(testDirectory), out var created);
        if (!created) return 0;
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName(testDirectory));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            using var worker = new Worker(app, journal, SessionId, stop, WindowTitle(testDirectory), settings, testDirectory is null);
            app.SessionEnding += (_, _) => app.Shutdown();
            return app.Run();
        }
        catch (Exception ex)
        {
            // Background mode never opens error dialogs or starts an elevated helper.
            try { Directory.CreateDirectory(journal.DirectoryPath); File.WriteAllText(Path.Combine(journal.DirectoryPath, "monitor-error.txt"), ex.GetType().Name + ": " + ex.Message); }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { }
            return 6;
        }
    }

    public static int Verify()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CompUsage-monitor-check-" + Guid.NewGuid().ToString("N"));
        Process? child = null;
        try
        {
            // A fresh install must not launch a persistent collector before the user opts in.
            var journal = new ActivityJournal(directory);
            using (var disabled = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                { ArgumentList = { "--activity-monitor-check", directory }, UseShellExecute = false, CreateNoWindow = true }))
            {
                if (disabled is null || !disabled.WaitForExit(5000) || disabled.ExitCode != 0 || journal.ReadAll().Count != 0) return 7;
            }
            new ActivityJournal(directory).Configure(true);
            child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            { ArgumentList = { "--activity-monitor-check", directory }, UseShellExecute = false, CreateNoWindow = true });
            if (child is null) return 7;
            var wait = Stopwatch.StartNew();
            ActivitySession? state = null;
            while (wait.Elapsed < TimeSpan.FromSeconds(15) && !child.HasExited)
            {
                state = journal.ReadAll().FirstOrDefault();
                if (state is not null && state.HeartbeatAt > state.StartedAt) break;
                Thread.Sleep(100);
            }
            if (state is null || state.HeartbeatAt <= state.StartedAt || !IsRunning(directory)) return 7;
            if (IsInteractive(int.Parse(SessionId, CultureInfo.InvariantCulture)) && (state.LastInputAt is null || state.Error is not null)) return 7;
            // Starting a second collector for the same session must not create a second writer.
            using var duplicate = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            { ArgumentList = { "--activity-monitor-check", directory }, UseShellExecute = false, CreateNoWindow = true });
            if (duplicate is null || !duplicate.WaitForExit(5000) || duplicate.ExitCode != 0) return 7;
            // Exercise the collector's notification path without locking the user's desktop.
            var window = FindWindow(null, WindowTitle(directory));
            if (window == IntPtr.Zero || !PostMessage(window, 0x02B1, new IntPtr(7), new IntPtr(int.Parse(SessionId, CultureInfo.InvariantCulture)))) return 7;
            wait.Restart();
            while (wait.Elapsed < TimeSpan.FromSeconds(5) && journal.Load(SessionId).Locks.Count == 0) Thread.Sleep(50);
            if (journal.Load(SessionId).Locks.Count != 1) return 7;
            if (Stop(directory) != 0 || !child.WaitForExit(5000) || child.ExitCode != 0) return 7;
            if (journal.ReadAll().Single().StoppedAt is null) return 7;
            // Disabling survives a later Windows-startup invocation, without starting a collector.
            journal.Configure(false);
            using var paused = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                { ArgumentList = { "--activity-monitor-check", directory }, UseShellExecute = false, CreateNoWindow = true });
            return paused is not null && paused.WaitForExit(5000) && paused.ExitCode == 0 && !IsRunning(directory) ? 0 : 7;
        }
        finally
        {
            Stop(directory);
            if (child is { HasExited: false }) { child.Kill(); child.WaitForExit(); }
            child?.Dispose();
            var resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "CompUsage-monitor-check-", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private sealed class Worker : IDisposable
    {
        private readonly Application app;
        private readonly ActivityJournal journal;
        private readonly ActivitySession state;
        private readonly EventWaitHandle stop;
        private readonly DispatcherTimer timer;
        private readonly HwndSource window;
        private readonly Func<AppSettings> settings;
        private readonly TrayIcon? tray;
        private BreakPromptWindow? prompt;
        private DateTimeOffset? sampledAt;
        private bool locked;
        private bool registered;
        private int ticks;

        public Worker(Application app, ActivityJournal journal, string session, EventWaitHandle stop, string title,
            Func<AppSettings> settings, bool interactive)
        {
            this.app = app; this.journal = journal; this.stop = stop; this.settings = settings;
            // The newest record of any session tells when this computer was last in use before this sign-in.
            var previous = journal.ReadAll().MaxBy(s => s.HeartbeatAt);
            var since = previous is null || previous.HeartbeatAt == default ? (DateTimeOffset?)null : previous.LockedAt ?? previous.StoppedAt ?? previous.HeartbeatAt;
            state = journal.Load(session);
            state.StartedAt = DateTimeOffset.UtcNow; state.StoppedAt = null; state.LastInputAt = null; state.LockedAt = null;
            window = new HwndSource(new HwndSourceParameters(title) { Width = 0, Height = 0, WindowStyle = 0 });
            window.AddHook(Messages);
            var id = int.Parse(session, CultureInfo.InvariantCulture);
            locked = !IsInteractive(id);
            if (interactive)
            {
                tray = new TrayIcon(window, () => settings().Language == "en"
                    ? ("CompUsage – work log", "Open CompUsage", "Stop background monitor")
                    : ("CompUsage – munkaidőnapló", "CompUsage megnyitása", "Háttérfigyelő leállítása"), OpenApp, app.Shutdown);
                // A sign-in shortly after a restart or sign-out ends a break as well; started later, the monitor stays quiet.
                if (since is { } from && LogonTime(id) is { } logon && from < logon && DateTimeOffset.UtcNow - logon < TimeSpan.FromMinutes(5))
                    new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Normal, (sender, _) =>
                    { ((DispatcherTimer)sender!).Stop(); if (!locked) Prompt(from, logon); }, app.Dispatcher).Start();
            }
            timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), app.Dispatcher);
            Tick();
        }

        private void Tick()
        {
            if (stop.WaitOne(0)) { app.Shutdown(); return; }
            try
            {
                if (!registered)
                {
                    registered = WTSRegisterSessionNotification(window.Handle, 0);
                    if (registered) locked = !IsInteractive(int.Parse(state.SessionId, CultureInfo.InvariantCulture));
                }
                state.Enabled = journal.Configuration().Enabled;
                if (!state.Enabled) { sampledAt = null; state.LastInputAt = null; }
                if (!state.Enabled && !settings().BreakPrompt) { app.Shutdown(); return; }
                // Input times are only sampled when activity tracking is on; prompts need session notifications alone.
                if (state.Enabled && !locked && IsInteractive(int.Parse(state.SessionId, CultureInfo.InvariantCulture))) Sample();
                state.Error = registered ? null : "Session notifications are unavailable; retrying.";
                if (ticks++ % 5 == 0) Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { state.Error = ex.Message; sampledAt = null; }
        }

        private void Sample()
        {
            var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
            var now = DateTimeOffset.UtcNow;
            if (!GetLastInputInfo(ref input)) { sampledAt = null; state.LastInputAt = null; return; }
            state.LastInputAt = ActivityHistory.InputTime(now, GetTickCount64(), input.Tick);
            sampledAt = state.LastInputAt is null ? null : now;
        }

        private IntPtr Messages(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != 0x02B1 || lParam.ToInt32().ToString(CultureInfo.InvariantCulture) != state.SessionId) return IntPtr.Zero;
            try
            {
                if (wParam.ToInt32() == 7)
                {
                    locked = true; state.LockedAt ??= DateTimeOffset.UtcNow;
                    if (state.Enabled && journal.Configuration().Enabled)
                        state.Locks.Add(ActivityHistory.Capture(DateTimeOffset.UtcNow, state.LastInputAt, sampledAt, state.SessionId));
                    sampledAt = null; Save(true);
                }
                else if (wParam.ToInt32() is 8 or 1 or 3)
                {
                    var from = state.LockedAt;
                    locked = false; sampledAt = null; state.LastInputAt = null; state.LockedAt = null; Tick(); Save();
                    if (from is { } start) { var to = DateTimeOffset.UtcNow; app.Dispatcher.BeginInvoke(() => Prompt(start, to)); }
                }
                else if (wParam.ToInt32() is 2 or 4)
                { locked = true; state.LockedAt ??= DateTimeOffset.UtcNow; sampledAt = null; state.LastInputAt = null; Save(); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { state.Error = ex.Message; sampledAt = null; }
            return IntPtr.Zero;
        }

        private void Prompt(DateTimeOffset from, DateTimeOffset to)
        {
            try
            {
                var current = settings();
                var zone = TimeZoneInfo.Local;
                if (!BreakReasons.ShouldPrompt(from, to, current, zone)) return;
                var day = WorkDayClock.GetDate(from, current.DayStart, zone);
                var lunchNoted = state.Breaks.Any(b => b.Reason == BreakReasons.Lunch && WorkDayClock.GetDate(b.LockedAt, current.DayStart, zone) == day);
                prompt?.Close();
                prompt = new BreakPromptWindow(from, to, BreakReasons.Suggest(from, to, current, zone, lunchNoted), current, (reason, text, automatic) =>
                {
                    state.Breaks.RemoveAll(b => b.UnlockedAt == to);
                    state.Breaks.Add(new BreakNote(from, to, reason, BreakReasons.Parse(BreakReasons.Encode(reason, text)).Text, automatic));
                    Save(true);
                });
                prompt.Closed += (sender, _) => { if (ReferenceEquals(sender, prompt)) prompt = null; };
                prompt.Start();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                or ArgumentException or InvalidOperationException)
            { state.Error = ex.Message; }
        }

        private static void OpenApp()
        {
            // The app's single-instance handoff brings an already open window to the front.
            try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private void Save(bool history = false)
        {
            state.HeartbeatAt = DateTimeOffset.UtcNow;
            if (history) journal.Save(state, state.HeartbeatAt);
            journal.Heartbeat(state, state.HeartbeatAt);
        }

        public void Dispose()
        {
            timer.Stop();
            prompt?.Close();
            tray?.Dispose();
            if (registered) WTSUnRegisterSessionNotification(window.Handle);
            window.Dispose();
            state.StoppedAt = DateTimeOffset.UtcNow;
            try { Save(true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string WindowTitle(string? testDirectory) => "CompUsage activity monitor" + (testDirectory is null ? "" : " " + Path.GetFileName(testDirectory));

    private static bool IsInteractive(int session)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, 25, out var buffer, out var bytes)) return false;
        try
        {
            // WTSINFOEXW's union is aligned to eight bytes on x64; only its fixed header is needed.
            return bytes >= 20 && Marshal.ReadInt32(buffer) == 1 && Marshal.ReadInt32(buffer, 12) == 0
                && Marshal.ReadInt32(buffer, 16) == 1;
        }
        finally { WTSFreeMemory(buffer); }
    }

    private static DateTimeOffset? LogonTime(int session)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, 25, out var buffer, out var bytes)) return null;
        try
        {
            // WTSINFOEX_LEVEL1_W.LogonTime follows the fixed-size name fields, aligned to eight bytes.
            var value = bytes >= 176 ? Marshal.ReadInt64(buffer, 168) : 0;
            return value > 0 ? DateTimeOffset.FromFileTime(value) : null;
        }
        finally { WTSFreeMemory(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size; public uint Tick; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInput input);
    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);
    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int information, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
