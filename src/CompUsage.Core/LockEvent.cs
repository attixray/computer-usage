namespace CompUsage.Core;

public sealed record LockEvent(
    long RecordId,
    DateTimeOffset Timestamp,
    int EventId,
    string UserSid,
    string UserName,
    string Domain,
    string SessionId,
    string LogonId)
{
    // The log write time bounds retrospective shutdown estimates to the following boot.
    public DateTimeOffset? RecordedAt { get; init; }
    public DateTimeOffset? ActivityTimestamp { get; init; }
    public DateTimeOffset WorkTimestamp => ActivityTimestamp ?? Timestamp;
    // System-log events have no account: 42 is the start and 1 the end of a sleep/hibernation (both from
    // Power-Troubleshooter 1), 6008 an unexpected shutdown's last-alive time, 13 an orderly OS shutdown.
    public const int Sleep = 42, Wake = 1, UnexpectedShutdown = 6008, SystemShutdown = 13;
    // Not Windows events: the start and end of an input gap the monitor saw in a Remote Desktop session (see ActivityHistory.Idle).
    public const int IdleStart = 90001, IdleEnd = 90002;
    public const int ObservedUnlock = 90003;
    public bool IsLock => EventId is 4800 or 4779 or 4647 or Sleep or UnexpectedShutdown or SystemShutdown or IdleStart;
    public bool IsSessionTransition => EventId is 4778 or 4779;
    public bool IsSignInOrOut => EventId is 4624 or 4647;
    public bool IsSystemEvent => EventId is Sleep or Wake or UnexpectedShutdown or SystemShutdown;
    public bool IsWorkTransition => EventId is 4800 or 4801 or 4778 or 4779 or 4624 or 4647 or ObservedUnlock || IsSystemEvent;
    public string Label => EventId switch
    {
        Sleep => "Alvás / hibernálás",
        Wake => "Ébredés",
        UnexpectedShutdown => "Váratlan leállás (becsült)",
        SystemShutdown => "Rendszerleállítás",
        IdleStart => "Inaktivitás (RDP)",
        IdleEnd => "Aktivitás újra (RDP)",
        ObservedUnlock => "Feloldott állapot észlelve",
        4624 => "Bejelentkezés",
        4647 => "Kijelentkezés / leállítás",
        4778 => "Munkamenet csatlakoztatása",
        4779 => "Munkamenet leválasztása",
        4800 => "Zárolás",
        _ => "Feloldás"
    };
    public string SessionKey => $"{UserSid}|{SessionId}|{LogonId.ToLowerInvariant()}";
}
