using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Security.Principal;
using CompUsage.Core;

namespace CompUsage.App.Services;

public sealed record EventReadResult(IReadOnlyList<LockEvent> Events, DateTimeOffset? OldestAvailable);

public sealed class SecurityEventService
{
    public EventReadResult Read(string userSid, DateTimeOffset from, DateTimeOffset until,
        CancellationToken cancellationToken = default)
    {
        // Validate the SID before including it in the Windows event XPath expression.
        var validatedSid = new SecurityIdentifier(userSid).Value;
        var start = from.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
        var end = until.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
        var xpath = $"*[System[(EventID=4800 or EventID=4801) and TimeCreated[@SystemTime>='{start}' and @SystemTime<'{end}']] and EventData[Data[@Name='TargetUserSid']='{validatedSid}']]";
        using var reader = new EventLogReader(new EventLogQuery("Security", PathType.LogName, xpath));
        var events = new List<LockEvent>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var record = reader.ReadEvent();
            if (record is null) break;
            var parsed = EventXmlParser.Parse(record.ToXml());
            if (parsed is not null) events.Add(parsed);
        }

        using var oldestReader = new EventLogReader(new EventLogQuery("Security", PathType.LogName));
        using var oldest = oldestReader.ReadEvent();
        DateTimeOffset? oldestTime = oldest?.TimeCreated is { } time ? new DateTimeOffset(time).ToUniversalTime() : null;
        return new EventReadResult(events.OrderBy(e => e.Timestamp).ThenBy(e => e.RecordId).ToArray(), oldestTime);
    }
}
