using System.ComponentModel;
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

    private static (DateTime Stamp, SharedState? Value) savedState;

    // The monitor only reads the app's settings, day choices and logging gaps; the UI stays the only writer of state.json.
    public static SharedState SavedState()
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(LocalStore.FilePath);
            if (savedState.Value is null || stamp != savedState.Stamp) savedState = (stamp, LocalStore.ReadShared());
            return savedState.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { return savedState.Value ?? new(new(), new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>()); }
    }
    public static AppSettings SavedSettings() => SavedState().Settings;

    // Activity tracking is opt-in; break prompts and the daily goal are on by default. Any of them keeps the monitor running.
    public static bool Needed(AppSettings settings) => settings.BreakPrompt || settings.DailyGoalMinutes > 0;
    public static bool Wanted() => Journal.Configuration().Enabled || Needed(SavedSettings());

    public static void Start()
    {
        if (!Wanted() || IsRunning()) return;
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--activity-monitor" }, UseShellExecute = false, CreateNoWindow = true });
    }

    public static bool Healthy(ActivitySession state, DateTimeOffset now) =>
        state.StoppedAt is null && now - state.HeartbeatAt < TimeSpan.FromSeconds(20);

    // A restart first ends a monitor that still holds its mutex but stopped beating, and reports a monitor that does not come up.
    public static async Task StartAndWaitAsync(bool restart = false)
    {
        if (restart && IsRunning() && !Healthy(Journal.Load(SessionId), DateTimeOffset.UtcNow)) await Task.Run(EndStuck);
        Start();
        for (var attempt = 0; attempt < 30 && Wanted(); attempt++)
        {
            if (IsRunning() && Healthy(Journal.Load(SessionId), DateTimeOffset.UtcNow)) return;
            await Task.Delay(100);
        }
        if (restart && Wanted()) throw new InvalidOperationException("A háttérfigyelő nem indult újra. Próbáld újra később.");
    }

    // A hung dispatcher never polls the stop signal, so after a short wait the recorded process is ended.
    private static void EndStuck()
    {
        if (Stop(wait: TimeSpan.FromSeconds(3)) == 0 || Journal.Load(SessionId).ProcessId is not { } id) return;
        if (EndMonitorProcess(id)) for (var wait = Stopwatch.StartNew(); IsRunning() && wait.Elapsed < TimeSpan.FromSeconds(3);) Thread.Sleep(50);
    }

    // Only a CompUsage process in this session is ended, so a reused process id never stops an unrelated program.
    public static bool EndMonitorProcess(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            if (process.Id == Environment.ProcessId || process.SessionId != Process.GetCurrentProcess().SessionId
                || !string.Equals(process.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return false;
            process.Kill();
            return process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    public static bool RegisteredAtLogon()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("CompUsageActivity") is string value && value.Contains("--activity-monitor", StringComparison.Ordinal);
    }

    public static int Stop(string? testDirectory = null, TimeSpan? wait = null)
    {
        if (!IsRunning(testDirectory)) return 0;
        try { using var stop = EventWaitHandle.OpenExisting(StopName(testDirectory)); stop.Set(); }
        catch (WaitHandleCannotBeOpenedException) { return 5; }
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < (wait ?? TimeSpan.FromSeconds(12)))
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
        Func<AppSettings> settings = testDirectory is null ? SavedSettings : () => new AppSettings { BreakPrompt = false, DailyGoalMinutes = 0 };
        try { if (!journal.Configuration().Enabled && !Needed(settings())) return 0; }
        catch { return 6; }
        using var mutex = new Mutex(true, MutexName(testDirectory), out var created);
        if (!created) return 0;
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName(testDirectory));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            using var worker = new Worker(app, journal, SessionId, stop, WindowTitle(testDirectory), settings, testDirectory is null);
            app.SessionEnding += (_, _) => { worker.SessionEnding(); app.Shutdown(); };
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
        private bool remote;
        // When the session last became usable (monitor start, unlock or reconnect); RDP idle gaps never start before it.
        private DateTimeOffset activeSince = DateTimeOffset.UtcNow;
        private bool locked;
        private bool registered;
        private int ticks;
        private readonly bool interactive;
        private bool goalBusy;
        private volatile bool cacheBusy;
        private WorkReport? today;
        private DateTimeOffset? todayAt;

        public Worker(Application app, ActivityJournal journal, string session, EventWaitHandle stop, string title,
            Func<AppSettings> settings, bool interactive)
        {
            this.app = app; this.journal = journal; this.stop = stop; this.settings = settings; this.interactive = interactive;
            // The newest record of any session tells when this computer was last in use before this sign-in: an open
            // lock, a sign-out or shutdown, or the last heartbeat after a crash. A monitor stopped by hand tells nothing.
            var previous = journal.ReadAll().MaxBy(s => s.HeartbeatAt);
            var since = previous is null || previous.HeartbeatAt == default ? null : previous.LockedAt
                ?? (previous.StoppedAt is null ? previous.HeartbeatAt : previous.SessionEnded ? previous.StoppedAt : null);
            state = journal.Load(session);
            state.StartedAt = DateTimeOffset.UtcNow; state.ProcessId = Environment.ProcessId; state.StoppedAt = null; state.LastInputAt = null; state.LockedAt = null; state.SessionEnded = false;
            window = new HwndSource(new HwndSourceParameters(title) { Width = 0, Height = 0, WindowStyle = 0 });
            window.AddHook(Messages);
            var id = int.Parse(session, CultureInfo.InvariantCulture);
            locked = !IsInteractive(id);
            if (interactive)
            {
                tray = new TrayIcon(window, () => settings().Language == "en"
                    ? ("CompUsage – work log", "Open CompUsage", "Stop background monitor")
                    : ("CompUsage – munkaidőnapló", "CompUsage megnyitása", "Háttérfigyelő leállítása"), OpenApp, app.Shutdown, TodayBreaks);
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
                if (!state.Enabled && !Needed(settings())) { app.Shutdown(); return; }
                // Input times are only sampled when activity tracking is on; prompts need session notifications alone.
                if (state.Enabled && !locked && IsInteractive(int.Parse(state.SessionId, CultureInfo.InvariantCulture))) Sample();
                state.Error = registered ? null : "Session notifications are unavailable; retrying.";
                if (ticks % 60 == 1 && !locked) UpdateGoal();
                if (ticks % 60 == 31) CacheEvents();
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
            // Sampled with the input, so a lock right before an RDP disconnect still counts as remote.
            remote = GetSystemMetrics(SmRemoteSession) != 0;
            TrackIdle(now);
        }

        // In an RDP session a locked client computer leaves the remote session unlocked, so a long input gap is a break.
        private void TrackIdle(DateTimeOffset now)
        {
            var current = settings();
            if (!remote || current.LockTimeSource != "remote" || state.LastInputAt is not { } last) { state.IdleFrom = null; return; }
            if (state.IdleFrom is { } from && last > from.AddSeconds(1))
            {
                state.Idles.Add(new(from, last, state.SessionId)); state.IdleFrom = null; Save(true);
                app.Dispatcher.BeginInvoke(() => { Prompt(from, last); UpdateGoal(); });
            }
            else if (state.IdleFrom is null && ActivityHistory.IdleStart(now, last, activeSince, TimeSpan.FromMinutes(current.RemoteIdleMinutes)) is { } start)
            { state.IdleFrom = start; Save(); }
        }

        private IntPtr Messages(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != 0x02B1 || lParam.ToInt32().ToString(CultureInfo.InvariantCulture) != state.SessionId) return IntPtr.Zero;
            try
            {
                if (wParam.ToInt32() == 7)
                {
                    locked = true; state.LockedAt ??= DateTimeOffset.UtcNow; state.IdleFrom = null;
                    if (state.Enabled && journal.Configuration().Enabled)
                        state.Locks.Add(ActivityHistory.Capture(DateTimeOffset.UtcNow, state.LastInputAt, sampledAt, state.SessionId, remote));
                    sampledAt = null; Save(true);
                }
                else if (wParam.ToInt32() is 8 or 1 or 3)
                {
                    var from = state.LockedAt;
                    locked = false; sampledAt = null; state.LastInputAt = null; state.LockedAt = null; activeSince = DateTimeOffset.UtcNow; Tick(); Save();
                    if (from is { } start) { var to = DateTimeOffset.UtcNow; app.Dispatcher.BeginInvoke(() => Prompt(start, to)); }
                    UpdateGoal();
                }
                else if (wParam.ToInt32() == 6) { state.SessionEnded = true; Save(); }
                else if (wParam.ToInt32() is 2 or 4)
                {
                    if (state.IdleFrom is { } from) { state.Idles.Add(new(from, DateTimeOffset.UtcNow, state.SessionId)); state.IdleFrom = null; }
                    locked = true; state.LockedAt ??= DateTimeOffset.UtcNow; sampledAt = null; state.LastInputAt = null; Save(true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { state.Error = ex.Message; sampledAt = null; }
            return IntPtr.Zero;
        }

        public void SessionEnding() => state.SessionEnded = true;

        private void Prompt(DateTimeOffset from, DateTimeOffset to)
        {
            try
            {
                var current = settings();
                var zone = TimeZoneInfo.Local;
                if (!BreakReasons.ShouldPrompt(from, to, current, zone)) return;
                var day = WorkDayClock.GetDate(from, current.DayStart, zone);
                var lunchNoted = state.Breaks.Any(b => b.Reason == BreakReasons.Lunch && WorkDayClock.GetDate(b.LockedAt, current.DayStart, zone) == day);
                var suggestion = BreakReasons.Suggest(from, to, current, zone, lunchNoted);
                // Every asked break is kept, answered or not, so the tray menu can offer it again today.
                if (state.Breaks.All(b => b.UnlockedAt != to)) { state.Breaks.Add(new BreakNote(from, to, "", Suggestion: suggestion)); Save(true); }
                Show(new BreakPromptWindow(from, to, suggestion, current, Answer(from, to, suggestion)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                or ArgumentException or InvalidOperationException)
            { state.Error = ex.Message; }
        }

        // Reopens a break of today from the tray menu, starting from its saved answer or, if none, the lunch or meeting guess or the suggestion.
        private void Reopen(WorkReport day, BreakPair pair, string? shown)
        {
            try
            {
                var current = settings();
                var note = BreakReasons.Match(state.Breaks, pair);
                var from = note?.LockedAt ?? pair.Lock.Timestamp;
                var to = note?.UnlockedAt ?? pair.Unlock.Timestamp;
                var suggestion = pair.Reason is null && shown is not null ? shown
                    : note is not null && BreakReasons.Codes.Contains(note.Suggestion) ? note.Suggestion
                    : BreakReasons.Suggest(from, to, current, TimeZoneInfo.Local, day.Lunch is not null && day.Lunch.Id != pair.Id);
                Show(new BreakPromptWindow(from, to, suggestion, current, Answer(from, to, suggestion),
                    reopened: true, currentReason: pair.Reason, currentText: pair.ReasonText));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { state.Error = ex.Message; }
        }

        private void Show(BreakPromptWindow window)
        {
            prompt?.Close();
            prompt = window;
            prompt.Closed += (sender, _) => { if (ReferenceEquals(sender, prompt)) prompt = null; };
            prompt.Start();
        }

        private Action<string, string, bool> Answer(DateTimeOffset from, DateTimeOffset to, string suggestion) => (reason, text, automatic) =>
        {
            try
            {
                var note = new BreakNote(from, to, reason, BreakReasons.Parse(BreakReasons.Encode(reason, text)).Text, automatic, suggestion);
                state.Breaks.RemoveAll(b => b.UnlockedAt == to);
                state.Breaks.Add(note);
                Save(true);
                UpdateGoal();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            { state.Error = ex.Message; }
        };

        // Today's pairs come from the Security log, so breaks before this monitor started or without a prompt are listed as well.
        private IReadOnlyList<(string Text, Action? Run)> TodayBreaks()
        {
            var current = settings();
            var english = current.Language == "en";
            var zone = TimeZoneInfo.Local;
            if (todayAt is null || DateTimeOffset.Now - todayAt > TimeSpan.FromSeconds(10))
                try { var now = DateTimeOffset.Now; today = Today(SavedState(), ReadEvents(current, now), now); todayAt = now; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Diagnostics.Eventing.Reader.EventLogException
                    or InvalidOperationException or ArgumentException or System.Security.SecurityException) { today = null; }
            var list = new List<(string, Action?)> { (english ? "Today's breaks" : "Mai szünetek", null) };
            if (today is not { } day) { list.Add((english ? "The Security log cannot be read" : "A Security napló nem olvasható", null)); return list; }
            var pairs = day.Pairs.Where(p => p.Id == day.Lunch?.Id || p.Id == day.Meeting?.Id || p.Reason is not null
                || p.Duration.TotalMinutes > current.SplitBreakMinutes).ToArray();
            if (pairs.Length == 0) list.Add((english ? "No longer break yet today" : "Ma még nem volt hosszabb szünet", null));
            foreach (var pair in pairs)
            {
                var shown = Shown(day, pair);
                // A choice made in the app overrides the prompt, so it can only be changed there.
                var manual = pair.ReasonManual || pair.Id == day.Lunch?.Id && day.ManualLunch || pair.Id == day.Meeting?.Id && day.ManualMeeting;
                var answer = shown is null ? english ? "no answer" : "nincs válasz"
                    : BreakPromptWindow.Label(shown, english).TrimEnd('…') + (shown == pair.Reason && pair.ReasonText.Length > 0 ? ": " + pair.ReasonText : "")
                        + (manual ? english ? " (set in the app)" : " (az appban állítva)" : shown != pair.Reason ? english ? " (estimated)" : " (becsült)" : "");
                if (answer.Length > 70) answer = answer[..69] + "…";
                list.Add(($"{TimeZoneInfo.ConvertTime(pair.Lock.WorkTimestamp, zone):H:mm} – {TimeZoneInfo.ConvertTime(pair.Unlock.Timestamp, zone):H:mm} · {answer}"
                    .Replace("&", "&&", StringComparison.Ordinal), manual ? OpenApp : () => Reopen(day, pair, shown)));
            }
            return list;
        }

        // The day's lunch and meeting are shown as their reasons, like the app's reason list.
        private static string? Shown(WorkReport day, BreakPair pair) =>
            pair.Id == day.Lunch?.Id ? BreakReasons.Lunch : pair.Id == day.Meeting?.Id ? BreakReasons.Standup : pair.Reason;

        // Copies new log events into the journal every minute, locked or not, on a worker thread: the Security log is
        // circular and can lose a whole night before the app reads it. The first read reaches back as far as the log goes.
        private void CacheEvents()
        {
            if (!interactive || cacheBusy) return;
            cacheBusy = true;
            var session = state.SessionId;
            Task.Run(() =>
            {
                try
                {
                    var until = DateTimeOffset.UtcNow;
                    var from = journal.EventsReadUntil(session)?.AddMinutes(-5) ?? until.AddDays(-60);
                    var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
                    journal.SaveEvents(session, new SecurityEventService().Read(sid, from, until).Events, until);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                    or System.Diagnostics.Eventing.Reader.EventLogException or ArgumentException or InvalidOperationException) { }
                finally { cacheBusy = false; }
            });
        }

        private static IReadOnlyList<LockEvent> ReadEvents(AppSettings current, DateTimeOffset now)
        {
            var from = WorkDayClock.At(WorkDayClock.GetDate(now, current.DayStart, TimeZoneInfo.Local), current.DayStart, TimeZoneInfo.Local);
            return new SecurityEventService().Read(System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, from, now).Events;
        }

        // Today's events come from the log and from the journal, which still has the ones the log already overwrote.
        private WorkReport? Today(SharedState shared, IReadOnlyList<LockEvent> events, DateTimeOffset now) =>
            WorkReports.Build(events.Concat(journal.Events()), shared.Settings with { HistoryDays = 1 }, shared.Lunches, now, TimeZoneInfo.Local, shared.Gaps, state.Locks,
                shared.Meetings, null, state.Breaks, shared.BreakReasons, state.AllIdles(DateTimeOffset.UtcNow))
            .FirstOrDefault(d => d.Date == WorkDayClock.GetDate(now, shared.Settings.DayStart, TimeZoneInfo.Local));

        // Today's worked time from the Security log, with the app's day choices and this monitor's answers; also refreshes the tray list.
        private async void UpdateGoal()
        {
            if (!interactive || goalBusy || tray is null) return;
            goalBusy = true;
            try
            {
                var shared = SavedState();
                var current = shared.Settings;
                var now = DateTimeOffset.Now;
                var events = await Task.Run(() => ReadEvents(current, now));
                var day = today = Today(shared, events, now); todayAt = now;
                if (current.DailyGoalMinutes == 0) { tray.SetTip(null); return; }
                var goal = TimeSpan.FromMinutes(current.DailyGoalMinutes);
                var english = current.Language == "en";
                if (day is null || day.Incomplete) { tray.SetTip(english ? "CompUsage · today: incomplete log" : "CompUsage · ma: hiányos napló"); return; }
                var worked = WorkSummary.Worked(day);
                var left = goal - worked;
                tray.SetTip(english
                    ? $"CompUsage · today {WorkSummary.Clock(worked)} / {WorkSummary.Clock(goal)} · " + (left > TimeSpan.Zero ? $"{WorkSummary.Clock(left)} to go" : "goal reached")
                    : $"CompUsage · ma {WorkSummary.Clock(worked)} / {WorkSummary.Clock(goal)} · " + (left > TimeSpan.Zero ? $"még {WorkSummary.Clock(left)}" : "cél elérve"));
                var key = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (left <= TimeSpan.Zero && current.GoalNotification && state.GoalNotifiedOn != key)
                {
                    state.GoalNotifiedOn = key; Save();
                    tray.Notify(english ? "Daily goal reached" : "Napi cél elérve",
                        english ? $"{WorkSummary.Clock(worked)} worked today." : $"Ma {WorkSummary.Clock(worked)} munkaidő.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Diagnostics.Eventing.Reader.EventLogException
                or InvalidOperationException or ArgumentException or System.Security.SecurityException)
            { tray.SetTip(null); today = null; }
            finally { goalBusy = false; }
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
    private const int SmRemoteSession = 0x1000;
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
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
