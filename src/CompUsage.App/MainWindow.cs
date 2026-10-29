using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using CompUsage.App.Services;
using CompUsage.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace CompUsage.App;

public sealed class MainWindow : Window
{
    private const string Origin = "https://compusage.local/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly WebView2 view = new();
    private readonly LocalStore store = new();
    private readonly LocalState state;
    private readonly SecurityBroker broker = new();
    private readonly CultureInfo excelCulture = WindowsRegionalCulture.Create();
    private readonly WorkbookService workbook;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (string? Lunch, string? Meeting)> undo = [];
    private WorkbookPlan? activePlan;
    private string? planToken;
    private string? lastError;
    private object? workbookInfo;
    private ExcelDisplay display;
    private IReadOnlyList<ActivityLock> activityLocks = [];
    private IReadOnlyList<BreakNote> breakNotes = [];
    private IReadOnlyList<IdleBreak> idleBreaks = [];
    private IReadOnlyList<LockEvent> cachedEvents = [];
    private IReadOnlyList<ActivityHeartbeat> activityHeartbeats = [];
    private IReadOnlyList<ActivityOpen> activityOpens = [];
    private TrayIcon? tray;
    private static readonly TimeSpan MonitorStartup = TimeSpan.FromSeconds(5);
    private readonly DateTime opened = DateTime.UtcNow;
    private object? activityInfo;
    private readonly WindowTheme windowTheme;
    private bool readAccessRequired;
    private bool closing;
    private bool commentsPending;
    private bool closeAfterComments;
    private bool commentsSaveFailed;

    public MainWindow()
    {
        state = store.Load();
        windowTheme = new WindowTheme(this, state.Settings.Theme);
        workbook = new WorkbookService(TimeZoneInfo.Local, excelCulture);
        display = new ExcelDisplay(new WorkbookFormats(), excelCulture, TimeZoneInfo.Local);
        Title = "CompUsage";
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/CompUsage.ico"));
        MinWidth = 600; MinHeight = 480;
        Width = state.Settings.RememberView ? state.Width : 1180;
        Height = state.Settings.RememberView ? state.Height : 850;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (state.Settings.RememberView && state.Left is { } left && state.Top is { } top
            && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100
            && left + Width > SystemParameters.VirtualScreenLeft + 100
            && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100
            && top + Height > SystemParameters.VirtualScreenTop + 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual; Left = left; Top = top;
        }
        if (state.Settings.RememberView && state.Maximized) WindowState = WindowState.Maximized;
        Content = view;
        Loaded += async (_, _) => await InitializeAsync();
        SourceInitialized += (_, _) =>
        {
            UpdateTray();
            var settle = new System.Windows.Threading.DispatcherTimer { Interval = MonitorStartup };
            settle.Tick += (_, _) => { settle.Stop(); UpdateTray(); };
            settle.Start();
        };
        // While the window is active, ask the UI for a silent refresh on activation and once a minute.
        var autoRefresh = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        autoRefresh.Tick += (_, _) => RequestAutoRefresh();
        Activated += (_, _) => { RequestAutoRefresh(); autoRefresh.Start(); };
        Deactivated += (_, _) => autoRefresh.Stop();
        Closing += (_, e) =>
        {
            if (commentsPending)
            {
                if (commentsSaveFailed)
                {
                    if (MessageBox.Show(state.Settings.Language == "en" ? "Close and discard unsaved comments?" : "Kilépsz a nem mentett megjegyzések eldobásával?",
                        "CompUsage",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No) != MessageBoxResult.Yes)
                    { e.Cancel=true;closeAfterComments=false;return; }
                    commentsPending=false;
                }
                else
                {
                    e.Cancel = true; closeAfterComments = true;
                    view.CoreWebView2?.PostWebMessageAsJson("{\"command\":\"saveCommentsBeforeClose\"}");
                    return;
                }
            }
            if (gate.CurrentCount == 0) { e.Cancel = true; return; }
            closing = true;
            try
            {
                state.Maximized = WindowState == WindowState.Maximized;
                var rect = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                state.Left = rect.Left; state.Top = rect.Top; state.Width = rect.Width; state.Height = rect.Height;
                store.Save(state);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, state.Settings.Language == "en" ? "Could not save the view" : "A nézet mentése nem sikerült"); }
            tray?.Dispose(); tray = null;
            windowTheme.Dispose(); broker.Dispose(); view.Dispose();
        };
    }

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(LocalStore.DirectoryPath, "WebView2"));
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("compusage.local", Path.Combine(AppContext.BaseDirectory, "Ui"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = true;
            core.NavigationStarting += (_, e) => { if (e.Uri != Origin + "index.html") e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.WebMessageReceived += Receive;
            core.ProcessFailed += (_, _) => { commentsPending = false; MessageBox.Show(state.Settings.Language == "en"
                ? "The interface stopped. Close and restart CompUsage. Saved data is preserved."
                : "A felület leállt. Zárd be és indítsd újra a CompUsage alkalmazást. A mentett adatok megmaradtak.", "CompUsage"); };
            core.Navigate(Origin + "index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show((state.Settings.Language == "en"
                ? "The interface could not start. Install Microsoft Edge WebView2 Runtime or run Setup again.\n\n"
                : "A felület nem indítható. Telepítsd a Microsoft Edge WebView2 Runtime-ot, vagy futtasd újra a telepítőt.\n\n") + ex.Message, "CompUsage", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void RequestAutoRefresh()
    {
        if (!closing) view.CoreWebView2?.PostWebMessageAsJson("{\"command\":\"autoRefresh\"}");
    }

    private async void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != Origin + "index.html" || closing) return;
        string? id = null;
        await gate.WaitAsync();
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var message = document.RootElement;
            id = message.GetProperty("id").GetString();
            var command = message.GetProperty("command").GetString();
            var data = message.TryGetProperty("data", out var payload) ? payload : default;
            var result = await DispatchAsync(command, data);
            if (!closing) view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = true, result }, Json));
        }
        catch (Exception ex)
        {
            var error = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }
                ? "A Windows-jóváhagyás elmaradt. A kért beállítás módosítása nem történt meg." : ex.Message;
            if (!closing) view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = false, error }, Json));
        }
        finally { gate.Release(); }
    }

    private IReadOnlyList<WorkReport> Reports(AppSettings? settings = null) => WorkReports.Build(state.Events.Concat(cachedEvents), settings ?? state.Settings, state.Lunches,
        DateTimeOffset.Now, TimeZoneInfo.Local, state.Gaps, activityLocks, state.Meetings, state.SectionComments, breakNotes, state.BreakReasons, idleBreaks, activityHeartbeats, activityOpens);

    // The Windows log may reach back less than the history; events stored here or by the monitor cover the days before it.
    private DateTimeOffset? Oldest()
    {
        var stored = state.Events.Concat(cachedEvents).Select(e => (DateTimeOffset?)e.Timestamp).Min();
        return stored is { } first && (state.OldestAvailable is null || first < state.OldestAvailable) ? first : state.OldestAvailable;
    }

    // Weekly and monthly totals always cover the whole 60-day history, independent of the day list's range.
    private object Summary()
    {
        var settings = state.Settings with { HistoryDays = 60 };
        var days = Reports(settings);
        var today = WorkDayClock.GetDate(DateTimeOffset.Now, settings.DayStart, TimeZoneInfo.Local);
        var first = today.AddDays(-59);
        if (Oldest() is { } oldest && WorkDayClock.GetDate(oldest, settings.DayStart, TimeZoneInfo.Local) > first)
            first = WorkDayClock.GetDate(oldest, settings.DayStart, TimeZoneInfo.Local);
        object Period(SummaryPeriod p) => new
        {
            from = display.Date(p.From), to = display.Date(p.To), year = p.From.Year, month = p.From.Month, days = p.Days, incomplete = p.IncompleteDays,
            worked = WorkSummary.Clock(p.Worked), expected = WorkSummary.Clock(p.Expected), balance = WorkSummary.Clock(p.Balance),
            ahead = p.Balance >= TimeSpan.Zero, reasons = p.Reasons.ToDictionary(r => r.Key, r => WorkSummary.Clock(r.Value)),
            unanswered = WorkSummary.Clock(p.Unanswered), partial = p.Partial, ongoing = p.Ongoing
        };
        return new { goal = settings.DailyGoalMinutes, weeks = WorkSummary.Weeks(days, settings, first, today).Select(Period),
            months = WorkSummary.Months(days, settings, first, today).Select(Period) };
    }

    // The background monitor owns the notification-area icon; the window shows its own only while no monitor runs.
    private void UpdateTray()
    {
        if (closing) return;
        var handle = new WindowInteropHelper(this).Handle;
        bool starting;
        try { starting = DateTime.UtcNow - opened < MonitorStartup && ActivityMonitor.Wanted(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { starting = false; }
        var wanted = handle != IntPtr.Zero && !ActivityMonitor.IsRunning() && !starting;
        if (wanted && tray is null)
            tray = new TrayIcon(HwndSource.FromHwnd(handle), () => state.Settings.Language == "en"
                ? ("CompUsage – work log", "Open CompUsage", "Exit")
                : ("CompUsage – munkaidőnapló", "CompUsage megnyitása", "Kilépés"), () => SingleInstance.BringToFront(this), Close);
        else if (!wanted && tray is not null) { tray.Dispose(); tray = null; }
    }

    private void ReadActivity()
    {
        activityLocks = []; breakNotes = []; idleBreaks = []; cachedEvents = []; activityHeartbeats = []; activityOpens = [];
        try
        {
            var journal = ActivityMonitor.Journal;
            var enabled = journal.Configuration().Enabled;
            var sessions = journal.ReadAll();
            activityLocks = sessions.SelectMany(s => s.Locks).ToArray();
            activityOpens = sessions.SelectMany(s => s.Opens).ToArray();
            breakNotes = sessions.SelectMany(s => s.Breaks).ToArray();
            idleBreaks = sessions.SelectMany(s => s.AllIdles(DateTimeOffset.UtcNow)).ToArray();
            cachedEvents = journal.Events();
            activityHeartbeats = journal.Heartbeats();
            var sessionId = Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture);
            var current = sessions.FirstOrDefault(s => s.SessionId == sessionId);
            var healthy = current?.StoppedAt is null && current is not null
                && DateTimeOffset.UtcNow - current.HeartbeatAt < TimeSpan.FromSeconds(20);
            activityInfo = new { enabled, running = ActivityMonitor.IsRunning() && healthy, prompts = state.Settings.BreakPrompt,
                registered = ActivityMonitor.RegisteredAtLogon(), error = current?.Error };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { activityInfo = new { enabled = false, running = false, prompts = state.Settings.BreakPrompt, registered = false, error = ex.Message }; }
        UpdateTray();
    }

    private async Task<object?> DispatchAsync(string? command, JsonElement data)
    {
        switch (command)
        {
            case "load":
                ReadActivity();
                await UpdateWorkbookInfoAsync(); return Snapshot();
            case "refresh":
                ReadActivity();
                try
                {
                    var reply = await broker.RequestAsync("read");
                    var read = reply.Data;
                    readAccessRequired = reply.ReadAccessRequired;
                    if (read is not null)
                    {
                        state.Events = SessionEvents.MergeRecords(state.Events.Concat(read.Events)).ToList();
                        state.OldestAvailable = read.OldestAvailable; state.LastRefresh = DateTimeOffset.Now;
                    }
                    RecordLogging(reply.Logging);
                    lastError = null; store.Save(state);
                }
                catch (Exception ex) { lastError = "A napló frissítése nem sikerült: " + ex.Message; }
                activePlan = null; await UpdateWorkbookInfoAsync(); return Snapshot();
            case "settings":
                var settings = data.GetProperty("settings").Deserialize<AppSettings>(Json) ?? throw new ArgumentException("Hiányzó beállítások.");
                settings.Validate();
                if (data.TryGetProperty("logging", out var logging) && logging.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && state.Logging != logging.GetBoolean())
                {
                    var reply = await broker.RequestAsync("audit", logging.GetBoolean()); RecordLogging(reply.Logging);
                }
                state.Settings = settings; activePlan = null; store.Save(state);
                if (data.TryGetProperty("activityEnabled", out var activityEnabled) && activityEnabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    ActivityMonitor.Journal.Configure(activityEnabled.GetBoolean());
                // The monitor runs for activity tracking, break prompts or both; it picks up other setting changes itself.
                if (ActivityMonitor.Wanted()) await ActivityMonitor.StartAndWaitAsync();
                else await Task.Run(() => ActivityMonitor.Stop());
                ReadActivity();
                windowTheme.Set(settings.Theme);
                await UpdateWorkbookInfoAsync(); return Snapshot();
            case "previewTheme":
                windowTheme.Set(data.GetProperty("theme").GetString()!); return true;
            case "grantReadAccess":
                await broker.RequestAsync("grantRead");
                return await DispatchAsync("refresh", default);
            case "startActivity":
                await ActivityMonitor.StartAndWaitAsync(restart: true); ReadActivity(); return Snapshot();
            case "history":
                var history = data.GetProperty("days").GetInt32();
                if (history is not (7 or 30 or 60)) throw new ArgumentException("Érvénytelen időtartam.");
                state.Settings = state.Settings with { HistoryDays = history };
                activePlan = null; store.Save(state); await UpdateWorkbookInfoAsync(); return Snapshot();
            case "lunch":
            case "meeting":
                var date = data.GetProperty("date").GetString()!;
                var chosen = data.GetProperty("value").ValueKind == JsonValueKind.Null ? null : data.GetProperty("value").GetString();
                var day = Reports().Single(d => d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == date);
                if (chosen is not null && chosen != "none" && day.Pairs.All(p => p.Id != chosen)) throw new ArgumentException("Nincs ilyen szünetpár.");
                if (command == "meeting" && !state.Settings.IgnoreDailyMeeting) throw new ArgumentException("Előbb engedélyezd a napi meeting figyelmen kívül hagyását a beállításokban.");
                undo[date] = (state.Lunches.GetValueOrDefault(date), state.Meetings.GetValueOrDefault(date));
                var choices = command == "meeting" ? state.Meetings : state.Lunches;
                var otherChoices = command == "meeting" ? state.Lunches : state.Meetings;
                if (chosen is not null && chosen != "none" && otherChoices.GetValueOrDefault(date) == chosen) otherChoices.Remove(date);
                if (chosen is null) choices.Remove(date); else choices[date] = chosen;
                activePlan = null; store.Save(state); await UpdateWorkbookInfoAsync(); return Snapshot();
            case "undo":
                var key = data.GetProperty("date").GetString()!;
                if (undo.Remove(key, out var old))
                {
                    if (old.Lunch is null) state.Lunches.Remove(key); else state.Lunches[key] = old.Lunch;
                    if (old.Meeting is null) state.Meetings.Remove(key); else state.Meetings[key] = old.Meeting;
                }
                activePlan = null; store.Save(state); await UpdateWorkbookInfoAsync(); return Snapshot();
            case "breakReason":
                var pairId = data.GetProperty("pairId").GetString();
                var pairDay = Reports().FirstOrDefault(d => d.Pairs.Any(p => p.Id == pairId))
                    ?? throw new ArgumentException("A szünetpár megváltozott. Frissítsd a naplót, majd próbáld újra.");
                var pair = pairDay.Pairs.First(p => p.Id == pairId);
                var dayKey = pairDay.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var reasonValue = data.GetProperty("reason");
                var encoded = reasonValue.ValueKind == JsonValueKind.Null ? null : BreakReasons.Encode(reasonValue.GetString()!,
                    data.TryGetProperty("text", out var reasonText) && reasonText.ValueKind == JsonValueKind.String ? reasonText.GetString() : null);
                var previous = (Reasons: new Dictionary<string, string>(state.BreakReasons), Lunch: state.Lunches.GetValueOrDefault(dayKey), Meeting: state.Meetings.GetValueOrDefault(dayKey));
                // The reason list is the only day choice now: an older lunch or meeting pick on another pair becomes that pair's reason.
                foreach (var (choice, code) in new[] { (previous.Lunch, BreakReasons.Lunch), (previous.Meeting, BreakReasons.Standup) })
                    if (choice is not null && choice != "none" && choice != pair.Id && pairDay.Pairs.Any(p => p.Id == choice)) state.BreakReasons.TryAdd(choice, code);
                state.Lunches.Remove(dayKey); state.Meetings.Remove(dayKey); undo.Remove(dayKey);
                if (encoded is null) state.BreakReasons.Remove(pair.Id); else state.BreakReasons[pair.Id] = encoded;
                try { store.Save(state); }
                catch
                {
                    state.BreakReasons.Clear();
                    foreach (var (id, value) in previous.Reasons) state.BreakReasons[id] = value;
                    if (previous.Lunch is not null) state.Lunches[dayKey] = previous.Lunch;
                    if (previous.Meeting is not null) state.Meetings[dayKey] = previous.Meeting;
                    throw;
                }
                activePlan = null; await UpdateWorkbookInfoAsync(); return Snapshot();
            case "copy":
                var text = data.GetProperty("text").GetString() ?? "";
                if (text.Length > 2_000_000) throw new ArgumentException("Túl sok másolandó adat.");
                for (var attempt = 0; ; attempt++)
                {
                    try { if (text.Length == 0) Clipboard.Clear(); else Clipboard.SetText(text); break; }
                    catch (System.Runtime.InteropServices.COMException) when (attempt < 4) { await Task.Delay(80); }
                }
                return true;
            case "paste": return Clipboard.ContainsText() ? Clipboard.GetText() : "";
            case "commentsPending":
                commentsPending = data.GetProperty("pending").GetBoolean();
                commentsSaveFailed = false;
                if (!commentsPending && closeAfterComments)
                    _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(Close));
                return true;
            case "commentsFailed": commentsSaveFailed = true; return true;
            case "removeManualEvent":
                var correctionId = long.Parse(data.GetProperty("id").GetString()!, CultureInfo.InvariantCulture);
                var correction = state.Events.SingleOrDefault(e => e.IsManual && e.RecordId == correctionId)
                    ?? throw new ArgumentException("A kézi korrekció nem található.");
                state.Events.Remove(correction);
                try { store.Save(state); }
                catch { state.Events.Add(correction); throw; }
                activePlan = null;
                await UpdateWorkbookInfoAsync(); return Snapshot();
            case "sectionComment":
                var sectionId = data.GetProperty("sectionId").GetString();
                var section = Reports().SelectMany(d => d.Sections).SingleOrDefault(s => s.Id == sectionId)
                    ?? throw new ArgumentException("A munkaszakasz megváltozott. Frissítsd a naplót, majd próbáld újra.");
                var edited = section with { Comment = data.GetProperty("comment").GetString() ?? "" };
                var previousComment = state.SectionComments.GetValueOrDefault(section.Id);
                if (edited.Comment.Length == 0) state.SectionComments.Remove(section.Id);
                else state.SectionComments[section.Id] = edited.Comment;
                try { store.Save(state); }
                catch
                {
                    if (previousComment is null) state.SectionComments.Remove(section.Id);
                    else state.SectionComments[section.Id] = previousComment;
                    throw;
                }
                activePlan = null; return true;
            case "pickFile":
                var dialog = new OpenFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", CheckFileExists = true,
                    Title = state.Settings.Language == "en" ? "Select a work-log workbook" : "Munkaidőfájl kiválasztása" };
                return dialog.ShowDialog(this) == true ? dialog.FileName : null;
            case "openFile":
                OpenWorkbook(state.Settings.WorkbookPath); return true;
            case "openSavedWorkbook":
                var previousSettings = state.Settings;
                state.Settings = state.Settings with { OpenSavedWorkbook = data.GetProperty("enabled").GetBoolean() };
                try { store.Save(state); }
                catch { state.Settings = previousSettings; throw; }
                return state.Settings.OpenSavedWorkbook;
            case "preview":
                activePlan = await Task.Run(() => workbook.Preview(state.Settings.WorkbookPath, Reports()));
                planToken = Guid.NewGuid().ToString("N"); return PlanSnapshot(activePlan, planToken);
            case "commit":
                if (activePlan is null || data.GetProperty("token").GetString() != planToken) throw new IOException("Az előnézet lejárt. Kérj új előnézetet.");
                var selected = data.GetProperty("partials").Deserialize<int[]>(Json) ?? [];
                var plan = activePlan; activePlan = null;
                var backup = await Task.Run(() => workbook.Commit(plan, selected));
                await UpdateWorkbookInfoAsync();
                string? openError = null;
                if (state.Settings.OpenSavedWorkbook)
                {
                    // Opening is a separate outcome: a shell/association failure must not suggest repeating the save.
                    try { OpenWorkbook(plan.Path); }
                    catch (Exception ex) { openError = ex.Message; }
                }
                return new { state = Snapshot(), backup, openError };
            case "view":
                if (state.Settings.RememberView)
                {
                    state.Expanded = (data.GetProperty("expanded").Deserialize<string[]>(Json) ?? []).Take(60).ToArray();
                    state.Scroll = Math.Clamp(data.GetProperty("scroll").GetDouble(), 0, 1_000_000);
                    store.Save(state);
                }
                return true;
            default: throw new ArgumentException("Ismeretlen művelet.");
        }
    }

    private static void OpenWorkbook(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("A beállított .xlsx fájl nem található.");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    private void RecordLogging(bool? enabled)
    {
        if (enabled == false && state.Logging != false) state.Gaps.Add(new(DateTimeOffset.Now, null));
        if (enabled == true)
            for (var i = 0; i < state.Gaps.Count; i++) if (state.Gaps[i].To is null) state.Gaps[i] = state.Gaps[i] with { To = DateTimeOffset.Now };
        state.Logging = enabled;
    }

    private async Task UpdateWorkbookInfoAsync()
    {
        try
        {
            var reports = Reports();
            var plan = await Task.Run(() => workbook.Preview(state.Settings.WorkbookPath, reports));
            display = new ExcelDisplay(plan.Formats, excelCulture, TimeZoneInfo.Local);
            workbookInfo = new { available = true, lastDate = plan.LastDate is { } date ? display.Date(date) : null,
                days = plan.Rows.Select(r => r.Date).Distinct().Count(), rows = plan.Rows.Count, partials = plan.Partials.Count, error = (string?)null };
        }
        catch (Exception ex)
        {
            display = new ExcelDisplay(new WorkbookFormats(), excelCulture, TimeZoneInfo.Local);
            workbookInfo = new { available = false, error = ex.Message };
        }
    }

    private object? TimeValue(DateTimeOffset? time, bool end = false) => time is null ? null : new
    {
        display = display.Time(time.Value, end),
        exact = Presentation.Time(time.Value, TimeZoneInfo.Local, true),
        date = display.Date(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.Value, TimeZoneInfo.Local).DateTime))
    };
    private object RowSnapshot(WorkInterval row) => new { id = row.Id, date = display.Date(row.Date), from = TimeValue(row.From),
        to = TimeValue(row.To, true), duration = ExcelDisplay.RoundedDuration(row.Duration), durationExact = ExcelDisplay.ExactDuration(row.Duration),
        comment = row.Comment,
        open = row.Open, copy = display.CopyRow(row) };
    private object PlanSnapshot(WorkbookPlan plan, string token)
    {
        display = new ExcelDisplay(plan.Formats, excelCulture, TimeZoneInfo.Local);
        return new { token, rows = plan.Rows.Select(RowSnapshot), partials = plan.Partials.Select(p => new
        { row = p.Row, section = RowSnapshot(p.Interval), following = p.Following.Select(RowSnapshot), completesRow = p.CompletesRow }) };
    }

    private object? ActivityValue(LockEvent? entry)
    {
        if (entry is null || ActivityHistory.Match(activityLocks, entry) is not { LastInputAt: not null } record) return null;
        return new { lastInput = TimeValue(record.LastInputAt, true), idle = ActivityHistory.IdleText(record) };
    }

    // The window title shows today's worked time; an incomplete or missing day shows the app name only.
    private void UpdateTitle(IReadOnlyList<WorkReport> days)
    {
        var today = days.FirstOrDefault(d => d.Ongoing);
        var worked = today is null || today.Incomplete ? (TimeSpan?)null : WorkSummary.Worked(today);
        Title = worked is { } value ? $"Computer usage: {(int)value.TotalHours}h {value.Minutes}m" : "CompUsage";
    }

    private object Snapshot()
    {
        var reports = Reports();
        UpdateTitle(reports);
        return SnapshotData(reports);
    }

    private object SnapshotData(IReadOnlyList<WorkReport> reports) => new
    {
        version = AppVersion.Current, settings = state.Settings, logging = state.Logging, error = lastError, activity = activityInfo, summary = Summary(),
        permissions = new { readAccessRequired },
        user = Environment.UserName, refreshed = state.LastRefresh?.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture),
        oldest = state.OldestAvailable?.ToLocalTime().ToString("yyyy.MM.dd HH:mm", CultureInfo.InvariantCulture),
        workbook = workbookInfo, expanded = state.Settings.RememberView ? state.Expanded : [], scroll = state.Settings.RememberView ? state.Scroll : 0,
        days = reports.Select(d => new
        {
            key = d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), date = display.Date(d.Date),
            weekday = d.Date.ToString("dddd", CultureInfo.GetCultureInfo("hu-HU")), status = d.Status, ongoing = d.Ongoing,
            incomplete = d.Incomplete, issues = d.Issues, first = TimeValue(d.First?.Timestamp), last = TimeValue(d.Last?.WorkTimestamp, true),
            firstFromSession = d.First?.IsSessionTransition ?? false, lastFromSession = d.Last?.IsSessionTransition ?? false,
            firstFromSignIn = d.First?.IsSignInOrOut ?? false, lastFromSignOut = d.Last?.IsSignInOrOut ?? false,
            firstSystem = d.First?.IsSystemEvent == true || d.First?.EventId == LockEvent.ObservedUnlock || d.First?.IsManual == true ? d.First.Label : null,
            lastSystem = d.Last?.IsSystemEvent == true ? d.Last.Label : null,
            lastFromActivity = d.Last?.ActivityTimestamp is not null, lastActivity = ActivityValue(d.Last),
            manual = d.ManualLunch, none = d.NoLunch, undo = undo.ContainsKey(d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            lunch = d.Lunch is null ? null : new { id = d.Lunch.Id, from = TimeValue(d.Lunch.Lock.WorkTimestamp, true), to = TimeValue(d.Lunch.Unlock.Timestamp), minutes = (int)d.Lunch.Duration.TotalMinutes },
            meeting = d.Meeting is null ? null : new { id = d.Meeting.Id, from = TimeValue(d.Meeting.Lock.WorkTimestamp, true), to = TimeValue(d.Meeting.Unlock.Timestamp), minutes = (int)d.Meeting.Duration.TotalMinutes },
            manualMeeting = d.ManualMeeting, noMeeting = d.NoMeeting,
            pairs = d.Pairs.Select(p => new { id = p.Id, from = TimeValue(p.Lock.WorkTimestamp, true), to = TimeValue(p.Unlock.Timestamp), minutes = (int)p.Duration.TotalMinutes,
                fromActivity = p.Lock.ActivityTimestamp is not null, activity = ActivityValue(p.Lock),
                reason = p.Reason, reasonText = p.ReasonText, reasonManual = p.ReasonManual, reasonAutomatic = p.ReasonAutomatic }),
            sections = d.Sections.Select(RowSnapshot),
            copy = !d.Incomplete && !d.Ongoing ? string.Join("\r\n", d.Sections.Select(display.CopyRow)) : "",
            eventCopy = "Dátum\tIdőpont\tEsemény\tAzonosító\r\n" + string.Join("\r\n", d.Events.Select(e => $"{TimeZoneInfo.ConvertTime(e.Timestamp, TimeZoneInfo.Local):yyyy.MM.dd}\t{Presentation.Time(e.Timestamp, TimeZoneInfo.Local, true)}\t{e.Label}\t{e.EventId}")),
            hasSessionEvents = d.Events.Any(e => e.IsSessionTransition), hasSignInEvents = d.Events.Any(e => e.IsSignInOrOut),
            hasSystemEvents = d.Events.Any(e => e.IsSystemEvent), hasCorrections = d.Events.Any(e => e.IsManual),
            events = d.Events.Select(e => new { time = TimeValue(e.Timestamp, e.IsLock), label = e.Label, eventId = e.EventId, isLock = e.IsLock,
                correctionId = e.IsManual ? e.RecordId.ToString(CultureInfo.InvariantCulture) : null, activity = ActivityValue(e) })
        })
    };
}
