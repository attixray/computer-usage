using System.Globalization;
using System.Text.Json;

namespace CompUsage.Core;

// Remote is true when the lock happened in a Remote Desktop session, null for observations saved before it was recorded.
public sealed record ActivityLock(DateTimeOffset LockedAt, DateTimeOffset? LastInputAt, DateTimeOffset? SampledAt, string SessionId, bool? Remote = null);
public sealed record ActivityConfiguration(bool Enabled = false);
// An input gap in a Remote Desktop session: a locked client computer leaves the remote session unlocked. To is null while it lasts.
public sealed record IdleBreak(DateTimeOffset From, DateTimeOffset? To, string SessionId);
public sealed class ActivitySession
{
    public string SessionId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    // The monitor's own process, so a restart can end it when it hangs and no longer sees the stop signal.
    public int? ProcessId { get; set; }
    public DateTimeOffset HeartbeatAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public DateTimeOffset? LastInputAt { get; set; }
    public bool Enabled { get; set; }
    public string? Error { get; set; }
    // Start of the current break as seen by the monitor; it survives a restart so the next sign-in can ask about it.
    public DateTimeOffset? LockedAt { get; set; }
    // True when Windows ended the session (sign-out, restart, shutdown), as opposed to a manual stop.
    public bool SessionEnded { get; set; }
    // Work date (yyyy-MM-dd) of the last daily-goal notification, so a restart does not repeat it.
    public string? GoalNotifiedOn { get; set; }
    public List<ActivityLock> Locks { get; set; } = [];
    public List<BreakNote> Breaks { get; set; } = [];
    public List<IdleBreak> Idles { get; set; } = [];
    // Last input before the current RDP input gap, once the gap reached the configured length.
    public DateTimeOffset? IdleFrom { get; set; }
    // The ended gaps and, while the monitor is alive, the current one.
    public IEnumerable<IdleBreak> AllIdles(DateTimeOffset now) => IdleFrom is { } from && StoppedAt is null
        && now - HeartbeatAt < TimeSpan.FromSeconds(20) ? Idles.Append(new(from, null, SessionId)) : Idles;
}

public static class ActivityHistory
{
    // LASTINPUTINFO uses a wrapping 32-bit tick count, while system uptime is 64-bit.
    public static DateTimeOffset? InputTime(DateTimeOffset now, ulong uptime, uint inputTick)
    {
        var age = unchecked((uint)uptime - inputTick);
        return age <= uptime ? now.AddMilliseconds(-age) : null;
    }

    public static ActivityLock Capture(DateTimeOffset lockedAt, DateTimeOffset? input, DateTimeOffset? sampledAt, string session, bool? remote = null)
    {
        // Never present an old sample after suspend, a stalled process or a failed API call as a fresh observation.
        var fresh = sampledAt is { } sample && sample <= lockedAt && lockedAt - sample <= TimeSpan.FromSeconds(3)
            && input is { } last && last <= sample;
        return new(lockedAt, fresh ? input : null, fresh ? sampledAt : null, session, remote);
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
            // "remote" uses the last input only for locks in a Remote Desktop session, where a locked client machine leaves
            // the remote session running until its own idle lock; local locks keep their Windows lock time.
            else if (settings.LockTimeSource is "activity" or "remote" && Match(observations, entry) is { LastInputAt: { } input } match
                && (settings.LockTimeSource == "activity" || match.Remote == true)
                && Capture(match.LockedAt, input, match.SampledAt, match.SessionId).LastInputAt is not null
                && input <= entry.Timestamp && starts.TryGetValue(entry.SessionKey, out var from) && input >= from
                && WorkDayClock.GetDate(input, settings.DayStart, zone) == WorkDayClock.GetDate(entry.Timestamp, settings.DayStart, zone))
                current = current with { ActivityTimestamp = input };
            yield return current;
        }
    }

    // An RDP input gap counts only from when the session became usable: the last input before an unlock or reconnect
    // belongs to the lock or disconnect, not to a new gap.
    public static DateTimeOffset? IdleStart(DateTimeOffset now, DateTimeOffset lastInput, DateTimeOffset activeSince, TimeSpan threshold)
    {
        var from = lastInput > activeSince ? lastInput : activeSince;
        return now - from >= threshold ? from : null;
    }

    // With the "remote" source an RDP idle break becomes a lock at its last input and an unlock at the input that ended it.
    // A gap ended by a lock or a disconnect moves that event back to the last input instead, so no empty section remains.
    public static IReadOnlyList<LockEvent> Idle(IEnumerable<LockEvent> transitions, IEnumerable<IdleBreak> idles,
        AppSettings settings, DateTimeOffset now)
    {
        var list = transitions.ToList();
        if (settings.LockTimeSource != "remote") return list;
        foreach (var idle in idles.Where(i => i.From <= now).OrderBy(i => i.From))
        {
            var before = list.FindLastIndex(e => e.Timestamp <= idle.From);
            // A sign-in (4624) carries no session id, so it accepts the monitor's own session.
            if (before < 0 || list[before].IsLock || list[before].SessionId is var session && session.Length > 0 && session != idle.SessionId) continue;
            var next = before + 1 < list.Count ? list[before + 1] : null;
            var end = idle.To ?? now;
            if (next is { IsLock: true } && next.Timestamp <= end.AddSeconds(5))
            {
                if (next.WorkTimestamp > idle.From) list[before + 1] = next with { ActivityTimestamp = idle.From };
            }
            else if (next is null || next.Timestamp > end)
            {
                var source = list[before];
                list.Insert(before + 1, source with { RecordId = -2 * idle.From.ToUnixTimeSeconds(), Timestamp = idle.From, EventId = LockEvent.IdleStart, ActivityTimestamp = null });
                if (idle.To is { } to && to > idle.From)
                    list.Insert(before + 2, source with { RecordId = -2 * to.ToUnixTimeSeconds() - 1, Timestamp = to, EventId = LockEvent.IdleEnd, ActivityTimestamp = null });
            }
        }
        return list;
    }
}

// Log events a monitor read, and the time its last read reached.
public sealed record EventCache(DateTimeOffset? ReadUntil, List<LockEvent> Events);

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
        status.Locks = history.Locks; status.Breaks = history.Breaks; status.Idles = history.Idles;
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
            session.StoppedAt, session.LastInputAt, session.Enabled, session.Error, session.LockedAt, session.SessionEnded, session.GoalNotifiedOn, session.IdleFrom });
    }
    // Windows overwrites the oldest Security-log records, on a busy computer within a day, so the monitor keeps what it read.
    public IReadOnlyList<LockEvent> Events() => !Directory.Exists(directory) ? [] :
        Directory.EnumerateFiles(directory, "events-*.json").SelectMany(path =>
        {
            try { return Read<EventCache>(path)?.Events ?? []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
        }).DistinctBy(e => (e.EventId, e.RecordId, e.Timestamp)).OrderBy(e => e.Timestamp).ThenBy(e => e.RecordId).ToArray();
    public DateTimeOffset? EventsReadUntil(string session) => Read<EventCache>(EventsPath(session))?.ReadUntil;
    public void SaveEvents(string session, IEnumerable<LockEvent> events, DateTimeOffset readUntil)
    {
        var path = EventsPath(session);
        var kept = (Read<EventCache>(path)?.Events ?? []).Concat(events).Where(e => e.Timestamp >= readUntil.AddDays(-61))
            .DistinctBy(e => (e.EventId, e.RecordId, e.Timestamp)).OrderBy(e => e.Timestamp).ThenBy(e => e.RecordId).ToList();
        Write(Path.GetFileName(path), new EventCache(readUntil, kept));
    }
    public void Save(ActivitySession session, DateTimeOffset now)
    {
        session.Locks.RemoveAll(r => r.LockedAt < now.AddDays(-61));
        session.Idles.RemoveAll(r => r.From < now.AddDays(-61));
        session.Breaks.RemoveAll(b => b.UnlockedAt < now.AddDays(-61));
        Write(Path.GetFileName(SessionPath(session.SessionId)), session);
    }
    private string EventsPath(string session) => Path.Combine(directory, "events-" + Path.GetFileName(SessionPath(session))["session-".Length..]);
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
