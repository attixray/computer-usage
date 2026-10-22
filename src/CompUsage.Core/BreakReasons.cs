namespace CompUsage.Core;

// A break the background prompt asked about. Reason is empty while unanswered; Automatic marks a suggestion saved
// when the prompt timed out; Suggestion keeps what the prompt proposed, so it can be asked again later.
public sealed record BreakNote(DateTimeOffset LockedAt, DateTimeOffset UnlockedAt, string Reason, string Text = "", bool Automatic = false,
    string Suggestion = "");

public static class BreakReasons
{
    public const string Standup = "standup", Lunch = "lunch", Coffee = "coffee", Meeting = "meeting", Work = "work", Other = "other";
    // Prompt order: the clock- and length-based reasons first, then the two free-text ones, "other break" last.
    public static readonly string[] Codes = [Standup, Lunch, Coffee, Meeting, Work, Other];
    public const int MaximumText = 200;

    public static bool CountsAsWork(string? reason) => reason is Standup or Meeting or Work;
    public static bool HasText(string? reason) => reason is Other or Work;
    // Only reasons that follow from the clock are trusted without an answer; length alone is too weak a signal.
    public static bool SavedOnTimeout(string reason) => reason is Standup or Lunch;

    public static bool ShouldPrompt(DateTimeOffset lockedAt, DateTimeOffset unlockedAt, AppSettings settings, TimeZoneInfo zone) =>
        settings.BreakPrompt && (unlockedAt - lockedAt).TotalMinutes > settings.SplitBreakMinutes
        && WorkDayClock.GetDate(lockedAt, settings.DayStart, zone) == WorkDayClock.GetDate(unlockedAt, settings.DayStart, zone);

    public static string Suggest(DateTimeOffset lockedAt, DateTimeOffset unlockedAt, AppSettings settings, TimeZoneInfo zone, bool lunchNoted)
    {
        var minutes = (unlockedAt - lockedAt).TotalMinutes;
        if (minutes <= settings.MaximumMeetingMinutes && WorkReports.IsMeetingStart(lockedAt, settings, zone)) return Standup;
        if (!lunchNoted && IsLunchCandidate(lockedAt, minutes, settings, zone)) return Lunch;
        return minutes <= settings.CoffeeBreakMaxMinutes ? Coffee : Meeting;
    }

    public static bool IsLunchCandidate(DateTimeOffset lockedAt, double minutes, AppSettings settings, TimeZoneInfo zone)
    {
        var date = WorkDayClock.GetDate(lockedAt, settings.DayStart, zone);
        var expected = WorkDayClock.At(settings.ExpectedLunch < settings.DayStart ? date.AddDays(1) : date, settings.ExpectedLunch, zone);
        return minutes >= settings.MinimumLunchMinutes && minutes <= 120 && Math.Abs((lockedAt - expected).TotalMinutes) <= 120;
    }

    // Overrides are stored as a code, "other:<text>", "work:<text>" or "none" (explicitly no reason).
    public static (string? Reason, string Text) Parse(string? value)
    {
        if (string.IsNullOrEmpty(value) || value == "none") return (null, "");
        foreach (var code in new[] { Other, Work })
            if (value.StartsWith(code + ":", StringComparison.Ordinal)) return (code, value[(code.Length + 1)..]);
        return Codes.Contains(value) ? (value, "") : (null, "");
    }

    public static string CleanText(string? text)
    {
        var clean = (text ?? "").Trim();
        if (clean.Length > MaximumText) throw new ArgumentException($"A szünet leírása legfeljebb {MaximumText} karakter lehet.");
        try { System.Xml.XmlConvert.VerifyXmlChars(clean); }
        catch (System.Xml.XmlException ex) { throw new ArgumentException("A szünet leírása nem megengedett karaktert tartalmaz.", ex); }
        return clean;
    }

    public static string Encode(string reason, string? text)
    {
        if (reason == "none") return reason;
        if (!Codes.Contains(reason)) throw new ArgumentException("Ismeretlen szünetok.");
        var clean = CleanText(text);
        return HasText(reason) && clean.Length > 0 ? reason + ":" + clean : reason;
    }

    // The prompt sees the session notification, the report sees the event log: allow for their small clock differences.
    public static BreakNote? Match(IEnumerable<BreakNote> notes, BreakPair pair) => notes
        .Where(n => Math.Abs((n.UnlockedAt - pair.Unlock.Timestamp).TotalMinutes) <= 2 && Math.Abs((n.LockedAt - pair.Lock.Timestamp).TotalMinutes) <= 10)
        .OrderBy(n => Math.Abs((n.UnlockedAt - pair.Unlock.Timestamp).Ticks)).FirstOrDefault();
}
