using System.Globalization;
using System.Text.Json;

namespace CompUsage.Core;

public sealed record ActivityLock(DateTimeOffset LockedAt, DateTimeOffset? LastInputAt, DateTimeOffset? SampledAt, string SessionId);
public sealed record ActivityConfiguration(bool Enabled = false);
public sealed class ActivitySession
{
    public string SessionId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset HeartbeatAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public DateTimeOffset? LastInputAt { get; set; }
    public bool Enabled { get; set; }
    public string? Error { get; set; }
    // Start of the current break as seen by the monitor; it survives a restart so the next sign-in can ask about it.
    public DateTimeOffset? LockedAt { get; set; }
    public List<ActivityLock> Locks { get; set; } = [];
    public List<BreakNote> Breaks { get; set; } = [];
}

public static class ActivityHistory
{
    // LASTINPUTINFO uses a wrapping 32-bit tick count, while system uptime is 64-bit.
    public static DateTimeOffset? InputTime(DateTimeOffset now, ulong uptime, uint inputTick)
    {
        var age = unchecked((uint)uptime - inputTick);
        return age <= uptime ? now.AddMilliseconds(-age) : null;
    }

    public static ActivityLock Capture(DateTimeOffset lockedAt, DateTimeOffset? input, DateTimeOffset? sampledAt, string session)
    {
        // Never present an old sample after suspend, a stalled process or a failed API call as a fresh observation.
        var fresh = sampledAt is { } sample && sample <= lockedAt && lockedAt - sample <= TimeSpan.FromSeconds(3)
            && input is { } last && last <= sample;
        return new(lockedAt, fresh ? input : null, fresh ? sampledAt : null, session);
    }

    public static ActivityLock? Match(IEnumerable<ActivityLock> records, LockEvent entry)
    {
        if (!entry.IsLock) return null;
        var matches = records.Where(r => r.SessionId == entry.SessionId && Math.Abs((r.LockedAt - entry.Timestamp).TotalSeconds) <= 5)
            .OrderBy(r => Math.Abs((r.LockedAt - entry.Timestamp).Ticks)).Take(2).ToArray();
        // Closely repeated transitions are ambiguous: do not attach one observation to two possible lock events.
        return matches.Length == 1 ? matches[0] : null;
    }

    public static string IdleText(ActivityLock record)
    {
        if (record.LastInputAt is not { } input) return "";
        var seconds = Math.Max(0, (long)(record.LockedAt - input).TotalSeconds);
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}");
    }

    public static IEnumerable<LockEvent> Apply(IEnumerable<LockEvent> entries, IEnumerable<ActivityLock> records,
        AppSettings settings, TimeZoneInfo zone)
    {
        var observations = records.ToArray();
        var starts = new Dictionary<string, DateTimeOffset>();
        foreach (var entry in entries.OrderBy(e => e.Timestamp).ThenBy(e => e.RecordId))
        {
            var current = entry with { ActivityTimestamp = null };
            if (!entry.IsLock) starts[entry.SessionKey] = entry.Timestamp;
            else if (settings.LockTimeSource == "activity" && Match(observations, entry) is { LastInputAt: { } input } match
                && Capture(match.LockedAt, input, match.SampledAt, match.SessionId).LastInputAt is not null
                && input <= entry.Timestamp && starts.TryGetValue(entry.SessionKey, out var from) && input >= from
                && WorkDayClock.GetDate(input, settings.DayStart, zone) == WorkDayClock.GetDate(entry.Timestamp, settings.DayStart, zone))
                current = current with { ActivityTimestamp = input };
            yield return current;
        }
    }
}

// Activity data has separate files from the UI state. Only the monitor for a session writes its journal.
public sealed class ActivityJournal(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string DirectoryPath => directory;
    public ActivityConfiguration Configuration() => Read<ActivityConfiguration>(Path.Combine(directory, "configuration.json")) ?? new();
    public void Configure(bool enabled) => Write("configuration.json", new ActivityConfiguration(enabled));
    public ActivitySession Load(string session)
    {
        var history = Read<ActivitySession>(SessionPath(session)) ?? new() { SessionId = session };
        var status = Read<ActivitySession>(Path.Combine(directory, $"status-{session}.json"));
        if (status is null || status.HeartbeatAt < history.HeartbeatAt) return history;
        status.Locks = history.Locks; status.Breaks = history.Breaks;
        return status;
    }
    public IReadOnlyList<ActivitySession> ReadAll() => !Directory.Exists(directory) ? [] :
        Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Where(n => n.StartsWith("session-", StringComparison.Ordinal) || n.StartsWith("status-", StringComparison.Ordinal))
            .Select(n => n[(n.IndexOf('-') + 1)..]).Where(n => int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= 0)
            .Distinct().Select(Load).ToArray();
    public void Heartbeat(ActivitySession session, DateTimeOffset now)
    {
        session.HeartbeatAt = now;
        // Keep the five-second health update small instead of rewriting sixty days of history each time.
        Write($"status-{session.SessionId}.json", new { session.SessionId, session.StartedAt, session.HeartbeatAt,
            session.StoppedAt, session.LastInputAt, session.Enabled, session.Error, session.LockedAt });
    }
    public void Save(ActivitySession session, DateTimeOffset now)
    {
        session.Locks.RemoveAll(r => r.LockedAt < now.AddDays(-61));
        session.Breaks.RemoveAll(b => b.UnlockedAt < now.AddDays(-61));
        Write(Path.GetFileName(SessionPath(session.SessionId)), session);
    }
    private string SessionPath(string session)
    {
        if (!int.TryParse(session, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 0)
            throw new ArgumentException("Invalid Windows session ID.");
        return Path.Combine(directory, $"session-{id}.json");
    }
    private static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        // Readers must allow the monitor's atomic replacement while the UI has the old version open.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, Json);
    }
    private void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, Json); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
