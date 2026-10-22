using System.Text.Json;
using CompUsage.Core;

namespace CompUsage.App.Services;

public sealed class LocalState
{
    public AppSettings Settings { get; set; } = new();
    public Dictionary<string, string> Lunches { get; set; } = [];
    public Dictionary<string, string> Meetings { get; set; } = [];
    public Dictionary<string, string> SectionComments { get; set; } = [];
    // Pair ID -> break reason chosen in the app; it overrides the background prompt's answer.
    public Dictionary<string, string> BreakReasons { get; set; } = [];
    public List<LockEvent> Events { get; set; } = [];
    public List<LoggingGap> Gaps { get; set; } = [];
    public string[] Expanded { get; set; } = [];
    public double Scroll { get; set; }
    public double Width { get; set; } = 1180;
    public double Height { get; set; } = 850;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Maximized { get; set; }
    public DateTimeOffset? LastRefresh { get; set; }
    public DateTimeOffset? OldestAvailable { get; set; }
    public bool? Logging { get; set; }
}

public sealed class LocalStore
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CompUsage");
    public static string FilePath => Path.Combine(DirectoryPath, "state.json");
    private readonly string path = FilePath;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public LocalState Load()
    {
        if (!File.Exists(path)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<LocalState>(File.ReadAllText(path), Json) ?? new();
            state.Settings.Validate();
            state.Width = Math.Clamp(state.Width, 640, 4000); state.Height = Math.Clamp(state.Height, 480, 2400);
            state.Scroll = double.IsFinite(state.Scroll) ? Math.Max(0, state.Scroll) : 0;
            return state;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidDataException($"Nem olvasható a beállításfájl: {path}. Az eredeti fájl megmaradt.", ex);
        }
    }
    // Other processes read only the settings, and must never block the UI's replace of the file.
    public static AppSettings ReadSettings()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var settings = document.RootElement.TryGetProperty(nameof(LocalState.Settings), out var value)
                ? value.Deserialize<AppSettings>(Json) ?? new() : new();
            settings.Validate();
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"Nem olvasható a beállításfájl: {FilePath}.", ex);
        }
    }
    public void Save(LocalState state)
    {
        var today = WorkDayClock.GetDate(DateTimeOffset.Now, state.Settings.DayStart, TimeZoneInfo.Local);
        var cutoff = today.AddDays(-59);
        state.Events.RemoveAll(e => WorkDayClock.GetDate(e.Timestamp, state.Settings.DayStart, TimeZoneInfo.Local) < cutoff);
        foreach (var key in state.Lunches.Keys.Where(k => !DateOnly.TryParse(k, out var date) || date < cutoff).ToArray()) state.Lunches.Remove(key);
        foreach (var key in state.Meetings.Keys.Where(k => !DateOnly.TryParse(k, out var date) || date < cutoff).ToArray()) state.Meetings.Remove(key);
        foreach (var key in state.SectionComments.Keys.Where(k => !DateOnly.TryParse(k.Split('|')[0],out var date) || date < cutoff).ToArray()) state.SectionComments.Remove(key);
        foreach (var key in state.BreakReasons.Keys.Where(k => k.Split(':') is not [_, var ticks, _, _]
            || !long.TryParse(ticks, out var value) || value < DateTimeOffset.MinValue.UtcTicks || value > DateTimeOffset.MaxValue.UtcTicks
            || new DateTimeOffset(value, TimeSpan.Zero) < DateTimeOffset.UtcNow.AddDays(-61)).ToArray()) state.BreakReasons.Remove(key);
        state.Gaps.RemoveAll(g => g.To is { } to && to < DateTimeOffset.UtcNow.AddDays(-61));
        Directory.CreateDirectory(DirectoryPath);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, path, true);
    }
}
