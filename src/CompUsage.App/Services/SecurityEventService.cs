using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
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
        using var identity = WindowsIdentity.GetCurrent();
        var account = identity.User?.Value == validatedSid ? identity.Name
            : new SecurityIdentifier(validatedSid).Translate(typeof(NTAccount)).Value;
        var start = from.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
        var end = until.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
        // Sign-ins are limited to interactive (2), remote-interactive (10) and cached-interactive (11) logons;
        // network and service logons are frequent and never start a desktop session.
        var xpath = $"*[System[(EventID=4800 or EventID=4801 or EventID=4778 or EventID=4779 or EventID=4624 or EventID=4647) and TimeCreated[@SystemTime>='{start}' and @SystemTime<'{end}']] and (EventData[Data[@Name='TargetUserSid']='{validatedSid}'] or System[EventID=4778 or EventID=4779])"
            + " and (System[EventID!=4624] or EventData[Data[@Name='LogonType']='2' or Data[@Name='LogonType']='10' or Data[@Name='LogonType']='11'])]";
        using var reader = new EventLogReader(new EventLogQuery("Security", PathType.LogName, xpath));
        var events = new List<LockEvent>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var record = reader.ReadEvent();
            if (record is null) break;
            var parsed = EventXmlParser.Parse(record.ToXml());
            if (parsed is not null) events.AddRange(SessionEvents.ForUser([parsed], validatedSid, account));
        }

        using var oldestReader = new EventLogReader(new EventLogQuery("Security", PathType.LogName));
        using var oldest = oldestReader.ReadEvent();
        DateTimeOffset? oldestTime = oldest?.TimeCreated is { } time ? new DateTimeOffset(time).ToUniversalTime() : null;
        // The current session may not have locked yet, so its own logon ID identifies today's sign-in.
        var active = identity.User?.Value == validatedSid ? LogonId(identity) : null;
        return new EventReadResult(SessionEvents.DropTransientSignIns(events, active)
            .OrderBy(e => e.Timestamp).ThenBy(e => e.RecordId).ToArray(), oldestTime);
    }

    public static string? LogonId(WindowsIdentity identity)
    {
        if (!GetTokenInformation(identity.AccessToken, TokenStatisticsClass, out var statistics,
                Marshal.SizeOf<TokenStatistics>(), out _)) return null;
        return string.Create(CultureInfo.InvariantCulture, $"0x{statistics.AuthenticationId:x}");
    }

    private const int TokenStatisticsClass = 10;
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenStatistics
    {
        public long TokenId, AuthenticationId, ExpirationTime;
        public int TokenType, ImpersonationLevel, DynamicCharged, DynamicAvailable, GroupCount, PrivilegeCount;
        public long ModifiedId;
    }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out TokenStatistics information, int length, out int returnLength);
}
