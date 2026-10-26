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

    public static IReadOnlyList<LockEvent> MergeSleeps(IEnumerable<LockEvent> events)
    {
        // Sleep followed by hibernation is recorded as overlapping low-power periods with the same start.
        // Merge overlapping periods into one sleep and its final wake.
        var list = events.ToArray();
        var periods = list.Where(e => e.EventId == LockEvent.Sleep)
            .Select(s => (Sleep: s, Wake: list.FirstOrDefault(w => w.EventId == LockEvent.Wake && w.RecordId == s.RecordId)))
            .Where(p => p.Wake is not null).OrderBy(p => p.Sleep.Timestamp).ThenBy(p => p.Wake!.Timestamp).ToArray();
        var merged = new List<LockEvent>();
        for (var i = 0; i < periods.Length;)
        {
            var (sleep, wake) = periods[i];
            for (i++; i < periods.Length && periods[i].Sleep.Timestamp <= wake!.Timestamp.AddSeconds(1); i++)
                if (periods[i].Wake!.Timestamp > wake.Timestamp) wake = periods[i].Wake;
            merged.Add(sleep); merged.Add(wake!);
        }
        return list.Where(e => e.EventId is not (LockEvent.Sleep or LockEvent.Wake)).Concat(merged).ToArray();
    }

    public static IReadOnlyList<LockEvent> WorkTransitions(IEnumerable<LockEvent> events)
    {
        var list = events.OrderBy(e => e.Timestamp).ThenBy(e => e.RecordId).ToArray();
        var result = new List<LockEvent>();
        var closed = new Dictionary<string, bool>();
        var sleeps = new Dictionary<string, SleepState>();
        for (var i = 0; i < list.Length; i++)
        {
            var entry = list[i];
            var previous = result.Count > 0 ? result[^1] : null;
            if (entry.IsSystemEvent)
            {
                if (entry.EventId == LockEvent.Wake)
                {
                    var sleeping = sleeps.Values.FirstOrDefault(s => s.Wake == entry);
                    if (sleeping is null) continue;
                    if (previous?.SessionKey != sleeping.Sleep.SessionKey)
                    {
                        sleeps.Remove(sleeping.Sleep.SessionKey);
                        continue;
                    }
                    // A wake is only a provisional work start when there is no native closing record.
                    // Keep the sleep context even when its synthetic close was replaced or never emitted.
                    if (!sleeping.RequiresUnlock && previous == sleeping.Sleep)
                    {
                        var wake = Adopt(entry, sleeping.Sleep);
                        sleeping.ProvisionalWake = wake;
                        result.Add(wake); closed[wake.SessionKey] = false;
                    }
                    continue;
                }
                // A new machine power transition ends any previous resume window. Shutdowns never reopen sessions.
                sleeps.Clear();
                if (previous is null) continue;
                var adopted = Adopt(entry, previous);
                if (entry.EventId == LockEvent.Sleep)
                {
                    // MergeSleeps supplies non-overlapping periods. Looking ahead to the next power event also
                    // recognizes resume locks timestamped just before Wake, independently of cross-log record IDs.
                    var nextPower = list.Skip(i + 1).FirstOrDefault(e => e.IsSystemEvent);
                    sleeps[adopted.SessionKey] = new(adopted, nextPower?.EventId == LockEvent.Wake ? nextPower : null, previous.IsLock);
                }
                if (previous.IsLock) continue;
                result.Add(adopted); closed[adopted.SessionKey] = true;
                continue;
            }
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
            if (duplicate) continue;
            if (sleeps.TryGetValue(entry.SessionKey, out var sleep))
            {
                if (!entry.IsLock)
                {
                    // The real unlock/reconnect wins over an inferred wake and ends duplicate suppression.
                    if (sleep.ProvisionalWake is { } wake) result.Remove(wake);
                    sleeps.Remove(entry.SessionKey);
                }
                else if (entry.EventId is 4800 or 4779 or 4647)
                {
                    if (previous == sleep.Sleep && entry.Timestamp - sleep.Sleep.Timestamp <= TimeSpan.FromSeconds(5)
                        && (sleep.Wake is null || entry.Timestamp < sleep.Wake.Timestamp))
                    {
                        // Prefer the native suspend close: its exact time and record ID keep saved break choices valid.
                        result[^1] = entry;
                        sleep.RequiresUnlock = true;
                        closed[entry.SessionKey] = true;
                        continue;
                    }
                    if (sleep.Wake is { } resumed && entry.Timestamp >= resumed.Timestamp.AddSeconds(-5)
                        && entry.Timestamp <= resumed.Timestamp.AddSeconds(60))
                    {
                        // Wake can precede or follow the lock-screen notification. Neither starts or ends work again.
                        if (sleep.ProvisionalWake is { } wake) result.Remove(wake);
                        sleep.ProvisionalWake = null;
                        sleep.RequiresUnlock = true;
                        closed[entry.SessionKey] = true;
                        continue;
                    }
                    if (sleep.Wake is { } ended && entry.Timestamp > ended.Timestamp) sleeps.Remove(entry.SessionKey);
                }
            }
            // Disconnecting, signing out or shutting down an already locked or disconnected session does not end work again.
            if (entry.EventId is 4647 or 4779 && closed.GetValueOrDefault(entry.SessionKey)) continue;
            result.Add(entry);
            closed[entry.SessionKey] = entry.IsLock;
        }
        return result;
    }

    private sealed class SleepState(LockEvent sleep, LockEvent? wake, bool requiresUnlock)
    {
        public LockEvent Sleep { get; } = sleep;
        public LockEvent? Wake { get; } = wake;
        public bool RequiresUnlock { get; set; } = requiresUnlock;
        public LockEvent? ProvisionalWake { get; set; }
    }

    private static LockEvent Adopt(LockEvent entry, LockEvent session) => entry with
    {
        UserSid = session.UserSid, UserName = session.UserName, Domain = session.Domain,
        SessionId = session.SessionId, LogonId = session.LogonId
    };

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
