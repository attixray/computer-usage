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

    public static IReadOnlyList<LockEvent> DropTransientSignIns(IEnumerable<LockEvent> events, string? activeLogonId)
    {
        // Windows also records a short cached-interactive logon while verifying credentials at unlock.
        // A sign-in starts a desktop session only if that logon ID is later used by the session's own
        // lock, unlock, connection or sign-out events, or if it is the reader's current logon session.
        var list = events.ToArray();
        var used = list.Where(e => e.EventId != 4624 && HasLogon(e)).Select(LogonKey).ToHashSet();
        return list.Where(e => e.EventId != 4624 || used.Contains(LogonKey(e))
            || activeLogonId is not null && string.Equals(e.LogonId, activeLogonId, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public static IReadOnlyList<LockEvent> ResolveSessions(IEnumerable<LockEvent> events)
    {
        var list = events.ToArray();
        var sessions = list.Where(e => !e.IsSessionTransition && e.SessionId.Length > 0 && HasLogon(e))
            .GroupBy(LogonKey).ToDictionary(g => g.Key, g => g.Select(e => e.SessionId).Distinct().ToArray());
        return list.Select(e => (e.IsSessionTransition || e.IsSignInOrOut) && e.SessionId.Length == 0 && HasLogon(e)
            && sessions.TryGetValue(LogonKey(e), out var known) && known.Length == 1
                ? e with { SessionId = known[0] } : e).ToArray();
    }

    public static IReadOnlyList<LockEvent> WorkTransitions(IEnumerable<LockEvent> events)
    {
        var list = events.OrderBy(e => e.Timestamp).ThenBy(e => e.RecordId).ToArray();
        var result = new List<LockEvent>();
        var closed = new Dictionary<string, bool>();
        for (var i = 0; i < list.Length; i++)
        {
            var entry = list[i];
            var duplicate = false;
            if (Rank(entry) > 0 && HasLogon(entry))
            {
                // Prefer a real lock/unlock within five seconds, preserving its exact timestamp and lunch-pair ID;
                // a connection record likewise wins over a sign-in or sign-out record.
                // Never coalesce through an opposite transition or across accounts/logon sessions.
                foreach (var direction in new[] { -1, 1 })
                {
                    for (var j = i + direction; j >= 0 && j < list.Length; j += direction)
                    {
                        var other = list[j];
                        if (Math.Abs((other.Timestamp - entry.Timestamp).TotalSeconds) > 5) break;
                        if (other.SessionKey != entry.SessionKey) continue;
                        if (other.IsLock != entry.IsLock) break;
                        if (Rank(other) < Rank(entry)) { duplicate = true; break; }
                    }
                    if (duplicate) break;
                }
            }
            // Disconnecting, signing out or shutting down an already locked or disconnected session does not end work again.
            if (entry.EventId is 4647 or 4779 && closed.GetValueOrDefault(entry.SessionKey)) duplicate = true;
            if (duplicate) continue;
            result.Add(entry);
            closed[entry.SessionKey] = entry.IsLock;
        }
        return result;
    }

    public static bool Sequential(IReadOnlyList<LockEvent> events)
    {
        // A sign-out, restart and new sign-in create a new logon session. Sessions that follow each other,
        // each ending with a lock, disconnect or sign-out, form one timeline; interleaved sessions do not.
        var finished = new HashSet<string>();
        for (var i = 1; i < events.Count; i++)
        {
            if (events[i].SessionKey == events[i - 1].SessionKey) continue;
            if (!events[i - 1].IsLock || finished.Contains(events[i].SessionKey)) return false;
            finished.Add(events[i - 1].SessionKey);
        }
        return true;
    }

    private static int Rank(LockEvent entry) => entry.IsSignInOrOut ? 2 : entry.IsSessionTransition ? 1 : 0;
    private static bool HasLogon(LockEvent entry) => entry.LogonId.Length > 0 && entry.LogonId != "0x0";
    private static string LogonKey(LockEvent entry) => $"{entry.UserSid}|{entry.LogonId.ToLowerInvariant()}";
}
