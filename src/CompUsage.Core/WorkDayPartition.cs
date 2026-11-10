namespace CompUsage.Core;

// Validate real transitions first, then carry only matched, same-session work through calendar boundaries.
internal static class WorkDayPartition
{
    // A fresh successful sample can confirm an ongoing overnight session. Old unmatched history cannot.
    public static IReadOnlyList<LockEvent> WithLiveEnd(IReadOnlyList<LockEvent> timeline, IEnumerable<MeasurementSpan> measurements, DateTimeOffset now)
    {
        if (timeline.LastOrDefault() is not { IsLock: false } last) return timeline;
        if (!measurements.Any(m => m.State == "sampling" && m.SessionId == last.SessionId && m.LogonId.Equals(last.LogonId, StringComparison.OrdinalIgnoreCase)
            && m.To <= now && now - m.To <= TimeSpan.FromSeconds(10) && m.To >= last.Timestamp)) return timeline;
        return [..timeline, Boundary(last, now, true)];
    }
    public static Dictionary<DateOnly, LockEvent[]> Days(IReadOnlyList<LockEvent> timeline, AppSettings settings, TimeZoneInfo zone)
    {
        var days = new Dictionary<DateOnly, List<LockEvent>>();
        DateOnly Date(DateTimeOffset at) => WorkDayClock.GetDate(at, settings.DayStart, zone);
        void Add(DateOnly day, LockEvent entry)
        {
            if (!days.TryGetValue(day, out var list)) days[day] = list = [];
            list.Add(entry);
        }
        for (var i = 0; i < timeline.Count; i++)
        {
            var entry = timeline[i];
            var at = entry.WorkTimestamp;
            // An actual end exactly at the boundary belongs to the section that just ended.
            var matched = i > 0 && !timeline[i - 1].IsLock && entry.IsLock && timeline[i - 1].SessionKey == entry.SessionKey;
            var date = Date(at);
            if (matched && at == WorkDayClock.At(date, settings.DayStart, zone) && timeline[i - 1].Timestamp < at) date = Date(at.AddTicks(-1));
            Add(date, entry);
            if (entry.IsLock || i + 1 >= timeline.Count || i > 0 && !timeline[i - 1].IsLock) continue;
            var close = timeline[i + 1];
            if (!close.IsLock || close.SessionKey != entry.SessionKey || close.WorkTimestamp <= entry.Timestamp) continue;
            for (var day = Date(entry.Timestamp);; day = day.AddDays(1))
            {
                var boundary = WorkDayClock.At(day.AddDays(1), settings.DayStart, zone);
                if (boundary >= close.WorkTimestamp) break;
                Add(day, Boundary(entry, boundary, true));
                Add(day.AddDays(1), Boundary(entry, boundary, false));
            }
        }
        return days.ToDictionary(p => p.Key, p => p.Value.OrderBy(e => e.WorkTimestamp).ThenBy(e => e.RecordId).ToArray());
    }

    private static LockEvent Boundary(LockEvent source, DateTimeOffset at, bool end) => source with
    {
        RecordId = -at.UtcTicks, Timestamp = at, EventId = end ? LockEvent.DayBoundaryEnd : LockEvent.DayBoundaryStart,
        ActivityTimestamp = null, RecordedAt = null
    };

    public static IReadOnlyList<WorkInterval> Sections(IReadOnlyList<LockEvent> timeline, IReadOnlyList<WorkReport> days,
        IReadOnlyList<BreakPair> pairs, AppSettings settings, DateTimeOffset now, TimeZoneInfo zone)
    {
        var lunches = days.Where(d => d.Lunch is not null).Select(d => d.Lunch!.Id).ToHashSet();
        var meetings = days.Where(d => d.Meeting is not null).Select(d => d.Meeting!.Id).ToHashSet();
        var gaps = pairs.ToDictionary(p => (p.Lock.RecordId, p.Unlock.RecordId, p.Lock.Timestamp, p.Unlock.Timestamp));
        var ranges = new List<(DateTimeOffset From, DateTimeOffset To, bool Open, int End)>();
        for (var i = 0; i < timeline.Count; i++)
        {
            var opened = timeline[i];
            if (opened.IsLock) continue;
            var close = i + 1 < timeline.Count ? timeline[i + 1] : null;
            var live = close?.EventId == LockEvent.DayBoundaryEnd && close.Timestamp == now
                || close is null && WorkDayClock.GetDate(opened.Timestamp, settings.DayStart, zone) == WorkDayClock.GetDate(now, settings.DayStart, zone);
            if (!live && (close is not { IsLock: true } || close.SessionKey != opened.SessionKey)) continue;
            var to = live ? now : close!.WorkTimestamp;
            if (to <= opened.Timestamp) continue;
            var join = false;
            if (ranges.Count > 0 && ranges[^1].End == i - 1 && i > 0)
            {
                var previous = timeline[i - 1];
                if (gaps.TryGetValue((previous.RecordId, opened.RecordId, previous.Timestamp, opened.Timestamp), out var gap))
                    join = !lunches.Contains(gap.Id) && (meetings.Contains(gap.Id) || BreakReasons.CountsAsWork(gap.Reason)
                        || gap.Duration.TotalMinutes <= settings.SplitBreakMinutes);
            }
            if (join) ranges[^1] = (ranges[^1].From, to, live, i + 1);
            else ranges.Add((opened.Timestamp, to, live, i + 1));
        }
        var result = new List<WorkInterval>();
        foreach (var range in ranges.Where(r => r.Open || r.To - r.From >= TimeSpan.FromMinutes(settings.MinimumWorkMinutes)))
        {
            var date = WorkDayClock.GetDate(range.From, settings.DayStart, zone);
            var split = WorkDayClock.GetDate(range.To.AddTicks(-1), settings.DayStart, zone) != date;
            for (var from = range.From; from < range.To; date = date.AddDays(1))
            {
                var boundary = WorkDayClock.At(date.AddDays(1), settings.DayStart, zone);
                var to = boundary < range.To ? boundary : range.To;
                result.Add(new(date, from, to, range.Open && to == range.To)
                    { UnsplitDuration = split ? range.To - range.From : null });
                from = to;
            }
        }
        return result;
    }
}
