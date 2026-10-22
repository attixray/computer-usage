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
    public DateTimeOffset? ActivityTimestamp { get; init; }
    public DateTimeOffset WorkTimestamp => ActivityTimestamp ?? Timestamp;
    public bool IsLock => EventId is 4800 or 4779 or 4647;
    public bool IsSessionTransition => EventId is 4778 or 4779;
    public bool IsSignInOrOut => EventId is 4624 or 4647;
    public bool IsWorkTransition => EventId is 4800 or 4801 or 4778 or 4779 or 4624 or 4647;
    public string Label => EventId switch
    {
        4624 => "Bejelentkezés",
        4647 => "Kijelentkezés / leállítás",
        4778 => "Munkamenet csatlakoztatása",
        4779 => "Munkamenet leválasztása",
        4800 => "Zárolás",
        _ => "Feloldás"
    };
    public string SessionKey => $"{UserSid}|{SessionId}|{LogonId.ToLowerInvariant()}";
}
