namespace CompUsage.Core;

public static class SessionEvents
{
    public static IEnumerable<LockEvent> ForUser(IEnumerable<LockEvent> events, string sid, string account)
    {
        foreach (var entry in events)
        {
            if (!entry.IsWorkTransition) continue;
            if (entry.UserSid == sid) yield return entry;
            else if (entry.IsSessionTransition && string.IsNullOrEmpty(entry.UserSid)
                && string.Equals($"{entry.Domain}\\{entry.UserName}", account, StringComparison.OrdinalIgnoreCase))
                yield return entry with { UserSid = sid };
        }
    }

    public static IReadOnlyList<LockEvent> ResolveSessions(IEnumerable<LockEvent> events)
    {
        var list = events.ToArray();
        var sessions = list.Where(e => !e.IsSessionTransition && e.SessionId.Length > 0 && HasLogon(e))
            .GroupBy(LogonKey).ToDictionary(g => g.Key, g => g.Select(e => e.SessionId).Distinct().ToArray());
        return list.Select(e => e.IsSessionTransition && e.SessionId.Length == 0 && HasLogon(e)
            && sessions.TryGetValue(LogonKey(e), out var known) && known.Length == 1
                ? e with { SessionId = known[0] } : e).ToArray();
    }

    public static IReadOnlyList<LockEvent> WorkTransitions(IEnumerable<LockEvent> events)
    {
        var list = events.OrderBy(e => e.Timestamp).ThenBy(e => e.RecordId).ToArray();
        var result = new List<LockEvent>();
        for (var i = 0; i < list.Length; i++)
        {
            var entry = list[i];
            var duplicate = false;
            if (entry.IsSessionTransition && HasLogon(entry))
            {
                // Prefer a real lock/unlock within five seconds, preserving its exact timestamp and lunch-pair ID.
                // Never coalesce through an opposite transition or across accounts/logon sessions.
                foreach (var direction in new[] { -1, 1 })
                {
                    for (var j = i + direction; j >= 0 && j < list.Length; j += direction)
                    {
                        var other = list[j];
                        if (Math.Abs((other.Timestamp - entry.Timestamp).TotalSeconds) > 5) break;
                        if (other.SessionKey != entry.SessionKey) continue;
                        if (other.IsLock != entry.IsLock) break;
                        if (!other.IsSessionTransition) { duplicate = true; break; }
                    }
                    if (duplicate) break;
                }
            }
            if (!duplicate) result.Add(entry);
        }
        return result;
    }

    private static bool HasLogon(LockEvent entry) => entry.LogonId.Length > 0 && entry.LogonId != "0x0";
    private static string LogonKey(LockEvent entry) => $"{entry.UserSid}|{entry.LogonId.ToLowerInvariant()}";
}
