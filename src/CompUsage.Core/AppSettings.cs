namespace CompUsage.Core;

public sealed record AppSettings
{
    public TimeOnly DayStart { get; init; } = new(4, 0);
    public TimeOnly ExpectedLunch { get; init; } = new(11, 30);
    public int MinimumLunchMinutes { get; init; } = 15;
    public int SplitBreakMinutes { get; init; } = 10;
    public bool IgnoreDailyMeeting { get; init; }
    public TimeOnly MeetingStartFrom { get; init; } = new(9, 25);
    public TimeOnly MeetingStartUntil { get; init; } = new(9, 40);
    public int MaximumMeetingMinutes { get; init; } = 20;
    public int HistoryDays { get; init; } = 60;
    public string WorkbookPath { get; init; } = "";
    public double FontScale { get; init; } = 1.15;
    public bool RememberView { get; init; } = true;
    public string Language { get; init; } = "hu";
    public string Theme { get; init; } = "auto";
    public string LockTimeSource { get; init; } = "lock";
    // With the "remote" source, an input gap of at least this long in a Remote Desktop session counts as a break.
    public int RemoteIdleMinutes { get; init; } = 10;
    public bool BreakPrompt { get; init; } = true;
    public int BreakPromptSeconds { get; init; } = 40;
    public int CoffeeBreakMaxMinutes { get; init; } = 25;
    // Quick choices offered for the free-text reasons in the prompt and the pairs table.
    public string[] BreakSuggestions { get; init; } = [];
    public string[] WorkSuggestions { get; init; } = ["Megbeszélés"];
    // 0 turns the daily goal off; the summary compares days with data against it.
    public int DailyGoalMinutes { get; init; } = 480;
    public bool GoalNotification { get; init; } = true;
    public DayOfWeek WeekStart { get; init; } = DayOfWeek.Monday;

    public void Validate()
    {
        if (HistoryDays is < 1 or > 60 || MinimumLunchMinutes is < 1 or > 120 || SplitBreakMinutes is < 1 or > 480
            || MaximumMeetingMinutes is < 1 or > 120 || RemoteIdleMinutes is < 3 or > 240 || BreakPromptSeconds is < 5 or > 3600 || CoffeeBreakMaxMinutes is < 1 or > 480)
            throw new ArgumentException("Érvénytelen nap- vagy perchatár.");
        if (FontScale is not (1 or 1.15 or 1.3)) throw new ArgumentException("Érvénytelen betűméret.");
        if (Language is not ("hu" or "en") || Theme is not ("auto" or "dark" or "light"))
            throw new ArgumentException("Érvénytelen nyelv vagy téma.");
        if (LockTimeSource is not ("lock" or "activity" or "remote")) throw new ArgumentException("Érvénytelen aktivitási időforrás.");
        if (DailyGoalMinutes is < 0 or > 1440 || !Enum.IsDefined(WeekStart)) throw new ArgumentException("Érvénytelen napi cél vagy hétkezdés.");
        foreach (var list in new[] { BreakSuggestions, WorkSuggestions })
        {
            if (list is null || list.Length > 20) throw new ArgumentException("Legfeljebb 20 ajánlat adható meg.");
            foreach (var item in list)
                if (string.IsNullOrWhiteSpace(item) || BreakReasons.CleanText(item) != item) throw new ArgumentException("Érvénytelen ajánlat.");
        }
        if (!string.IsNullOrWhiteSpace(WorkbookPath) && !WorkbookPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A munkaidőfájl .xlsx formátumú legyen.");
    }
}
