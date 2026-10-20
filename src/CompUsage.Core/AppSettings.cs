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

    public void Validate()
    {
        if (HistoryDays is < 1 or > 60 || MinimumLunchMinutes is < 1 or > 120 || SplitBreakMinutes is < 1 or > 480
            || MaximumMeetingMinutes is < 1 or > 120)
            throw new ArgumentException("Érvénytelen nap- vagy perchatár.");
        if (FontScale is not (1 or 1.15 or 1.3)) throw new ArgumentException("Érvénytelen betűméret.");
        if (Language is not ("hu" or "en") || Theme is not ("auto" or "dark" or "light"))
            throw new ArgumentException("Érvénytelen nyelv vagy téma.");
        if (LockTimeSource is not ("lock" or "activity")) throw new ArgumentException("Érvénytelen aktivitási időforrás.");
        if (!string.IsNullOrWhiteSpace(WorkbookPath) && !WorkbookPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A munkaidőfájl .xlsx formátumú legyen.");
    }
}
