using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
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
        Title = state.Settings.Language == "en" ? "CompUsage — Work log" : "CompUsage — Munkaidőnapló";
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

    private IReadOnlyList<WorkReport> Reports() => WorkReports.Build(state.Events, state.Settings, state.Lunches,
        DateTimeOffset.Now, TimeZoneInfo.Local, state.Gaps, activityLocks, state.Meetings, state.SectionComments);

    private void ReadActivity()
    {
        activityLocks = [];
        try
        {
            var journal = ActivityMonitor.Journal;
            var enabled = journal.Configuration().Enabled;
            var sessions = journal.ReadAll();
            activityLocks = sessions.SelectMany(s => s.Locks).ToArray();
            var sessionId = Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture);
            var current = sessions.FirstOrDefault(s => s.SessionId == sessionId);
            var healthy = current?.StoppedAt is null && current is not null
                && DateTimeOffset.UtcNow - current.HeartbeatAt < TimeSpan.FromSeconds(20);
            activityInfo = new { enabled, running = ActivityMonitor.IsRunning() && healthy,
                registered = ActivityMonitor.RegisteredAtLogon(), error = current?.Error };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { activityInfo = new { enabled = false, running = false, registered = false, error = ex.Message }; }
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
                        state.Events = state.Events.Concat(read.Events).DistinctBy(e => (e.RecordId, e.Timestamp)).OrderBy(e => e.Timestamp).ToList();
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
                {
                    ActivityMonitor.Journal.Configure(activityEnabled.GetBoolean());
                    if (activityEnabled.GetBoolean()) await ActivityMonitor.StartAndWaitAsync();
                    else await Task.Run(() => ActivityMonitor.Stop());
                }
                ReadActivity();
                Title = settings.Language == "en" ? "CompUsage — Work log" : "CompUsage — Munkaidőnapló";
                windowTheme.Set(settings.Theme);
                await UpdateWorkbookInfoAsync(); return Snapshot();
            case "previewTheme":
                windowTheme.Set(data.GetProperty("theme").GetString()!); return true;
            case "grantReadAccess":
                await broker.RequestAsync("grantRead");
                return await DispatchAsync("refresh", default);
            case "startActivity":
                await ActivityMonitor.StartAndWaitAsync(); ReadActivity(); return Snapshot();
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
                if (!File.Exists(state.Settings.WorkbookPath)) throw new FileNotFoundException("A beállított .xlsx fájl nem található.");
                Process.Start(new ProcessStartInfo(state.Settings.WorkbookPath) { UseShellExecute = true }); return true;
            case "preview":
                activePlan = await Task.Run(() => workbook.Preview(state.Settings.WorkbookPath, Reports()));
                planToken = Guid.NewGuid().ToString("N"); return PlanSnapshot(activePlan, planToken);
            case "commit":
                if (activePlan is null || data.GetProperty("token").GetString() != planToken) throw new IOException("Az előnézet lejárt. Kérj új előnézetet.");
                var selected = data.GetProperty("partials").Deserialize<int[]>(Json) ?? [];
                var plan = activePlan; activePlan = null;
                var backup = await Task.Run(() => workbook.Commit(plan, selected));
                await UpdateWorkbookInfoAsync(); return new { state = Snapshot(), backup };
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
        to = TimeValue(row.To, true), duration = display.Duration(row.Duration), durationExact = ExcelDisplay.ExactDuration(row.Duration),
        comment = row.Comment, excelCells = display.ExcelCells(row), excelCopy = display.CopyExcelRow(row),
        open = row.Open, copy = display.CopyRow(row) };
    private object PlanSnapshot(WorkbookPlan plan, string token)
    {
        display = new ExcelDisplay(plan.Formats, excelCulture, TimeZoneInfo.Local);
        return new { token, rows = plan.Rows.Select(RowSnapshot), partials = plan.Partials.Select(p => new
        { row = p.Row, section = RowSnapshot(p.Interval), keepsExistingComment = p.KeepsExistingComment }) };
    }

    private object? ActivityValue(LockEvent? entry)
    {
        if (entry is null || ActivityHistory.Match(activityLocks, entry) is not { LastInputAt: not null } record) return null;
        return new { lastInput = TimeValue(record.LastInputAt, true), idle = ActivityHistory.IdleText(record) };
    }

    private object Snapshot() => new
    {
        settings = state.Settings, logging = state.Logging, error = lastError, activity = activityInfo,
        permissions = new { readAccessRequired },
        user = Environment.UserName, refreshed = state.LastRefresh?.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture),
        oldest = state.OldestAvailable?.ToLocalTime().ToString("yyyy.MM.dd HH:mm", CultureInfo.InvariantCulture),
        workbook = workbookInfo, expanded = state.Settings.RememberView ? state.Expanded : [], scroll = state.Settings.RememberView ? state.Scroll : 0,
        days = Reports().Select(d => new
        {
            key = d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), date = display.Date(d.Date),
            weekday = d.Date.ToString("dddd", CultureInfo.GetCultureInfo("hu-HU")), status = d.Status, ongoing = d.Ongoing,
            incomplete = d.Incomplete, issues = d.Issues, first = TimeValue(d.First?.Timestamp), last = TimeValue(d.Last?.WorkTimestamp, true),
            firstFromSession = d.First?.IsSessionTransition ?? false, lastFromSession = d.Last?.IsSessionTransition ?? false,
            lastFromActivity = d.Last?.ActivityTimestamp is not null, lastActivity = ActivityValue(d.Last),
            manual = d.ManualLunch, none = d.NoLunch, undo = undo.ContainsKey(d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            lunch = d.Lunch is null ? null : new { id = d.Lunch.Id, from = TimeValue(d.Lunch.Lock.WorkTimestamp, true), to = TimeValue(d.Lunch.Unlock.Timestamp), minutes = (int)d.Lunch.Duration.TotalMinutes },
            meeting = d.Meeting is null ? null : new { id = d.Meeting.Id, from = TimeValue(d.Meeting.Lock.WorkTimestamp, true), to = TimeValue(d.Meeting.Unlock.Timestamp), minutes = (int)d.Meeting.Duration.TotalMinutes },
            manualMeeting = d.ManualMeeting, noMeeting = d.NoMeeting,
            pairs = d.Pairs.Select(p => new { id = p.Id, from = TimeValue(p.Lock.WorkTimestamp, true), to = TimeValue(p.Unlock.Timestamp), minutes = (int)p.Duration.TotalMinutes,
                fromActivity = p.Lock.ActivityTimestamp is not null, activity = ActivityValue(p.Lock) }),
            sections = d.Sections.Select(RowSnapshot),
            copy = !d.Incomplete && !d.Ongoing ? string.Join("\r\n", d.Sections.Select(display.CopyRow)) : "",
            eventCopy = "Dátum\tIdőpont\tEsemény\tAzonosító\r\n" + string.Join("\r\n", d.Events.Select(e => $"{TimeZoneInfo.ConvertTime(e.Timestamp, TimeZoneInfo.Local):yyyy.MM.dd}\t{Presentation.Time(e.Timestamp, TimeZoneInfo.Local, true)}\t{e.Label}\t{e.EventId}")),
            hasSessionEvents = d.Events.Any(e => e.IsSessionTransition),
            events = d.Events.Select(e => new { time = TimeValue(e.Timestamp, e.IsLock), label = e.Label, eventId = e.EventId, isLock = e.IsLock, activity = ActivityValue(e) })
        })
    };
}
