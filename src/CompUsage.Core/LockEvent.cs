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
    public bool IsLock => EventId is 4800 or 4779;
    public bool IsSessionTransition => EventId is 4778 or 4779;
    public bool IsWorkTransition => EventId is 4800 or 4801 or 4778 or 4779;
    public string Label => EventId switch
    {
        4778 => "Munkamenet csatlakoztatása",
        4779 => "Munkamenet leválasztása",
        4800 => "Zárolás",
        _ => "Feloldás"
    };
    public string SessionKey => $"{UserSid}|{SessionId}|{LogonId.ToLowerInvariant()}";
}
