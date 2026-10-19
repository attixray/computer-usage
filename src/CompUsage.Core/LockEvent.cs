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
    public bool IsLock => EventId == 4800;
    public string Label => IsLock ? "Zárolás" : "Feloldás";
    public string SessionKey => $"{UserSid}|{SessionId}|{LogonId}";
}
