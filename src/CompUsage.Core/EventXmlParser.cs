using System.Globalization;
using System.Xml.Linq;

namespace CompUsage.Core;

public static class EventXmlParser
{
    public static LockEvent? Parse(string xml)
    {
        var root = XElement.Parse(xml);
        var ns = root.Name.Namespace;
        var system = root.Element(ns + "System");
        if (!int.TryParse(system?.Element(ns + "EventID")?.Value, out var id) || id is not (4800 or 4801 or 4778 or 4779 or 4624 or 4647))
            return null;

        var timestamp = system?.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
        if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var time))
            throw new FormatException("Az eseményből hiányzik az időpont.");

        var data = root.Element(ns + "EventData")?.Elements(ns + "Data")
            .Where(e => e.Attribute("Name") is not null)
            .ToDictionary(e => e.Attribute("Name")!.Value, e => e.Value)
            ?? new Dictionary<string, string>();
        string Field(string name) => data.GetValueOrDefault(name, "");
        // Only interactive, remote-interactive and cached-interactive sign-ins can start a desktop session.
        if (id == 4624 && Field("LogonType") is not ("2" or "10" or "11")) return null;
        if (!long.TryParse(system?.Element(ns + "EventRecordID")?.Value, out var recordId))
            throw new FormatException("Az eseményből hiányzik a naplóazonosító.");

        // Session reconnect/disconnect records carry an account and logon ID, but no SID or numeric session ID.
        // The reader must verify the account before assigning the requested user's SID.
        var session = id is 4778 or 4779;
        return new LockEvent(recordId, time.ToUniversalTime(), id, Field("TargetUserSid"),
            Field(session ? "AccountName" : "TargetUserName"), Field(session ? "AccountDomain" : "TargetDomainName"),
            Field("SessionId"), Field(session ? "LogonID" : "TargetLogonId"));
    }

    public static IReadOnlyList<LockEvent> ParseSystem(string xml)
    {
        var root = XElement.Parse(xml);
        var ns = root.Name.Namespace;
        var system = root.Element(ns + "System");
        var provider = system?.Element(ns + "Provider")?.Attribute("Name")?.Value ?? "";
        if (!int.TryParse(system?.Element(ns + "EventID")?.Value, out var id)
            || !long.TryParse(system?.Element(ns + "EventRecordID")?.Value, out var recordId)) return [];
        var data = root.Element(ns + "EventData");
        string Field(string name) => data?.Elements(ns + "Data").FirstOrDefault(e => e.Attribute("Name")?.Value == name)?.Value ?? "";
        LockEvent Entry(int eventId, DateTimeOffset time) => new(recordId, time.ToUniversalTime(), eventId, "", "", "", "", "");
        static DateTimeOffset? Utc(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var time) && time.Year > 1601 ? time.ToUniversalTime() : null;

        switch (provider, id)
        {
            case ("Microsoft-Windows-Power-Troubleshooter", 1):
                // One wake record carries both exact UTC endpoints of the low-power period.
                return Utc(Field("SleepTime")) is { } sleep && Utc(Field("WakeTime")) is { } wake && wake > sleep
                    ? [Entry(LockEvent.Sleep, sleep), Entry(LockEvent.Wake, wake)] : [];
            case ("EventLog", 6008):
                // Written at the next boot; the binary data holds the last-alive time as local and UTC SYSTEMTIMEs.
                return LastAlive(data?.Element(ns + "Binary")?.Value) is { } alive
                    ? [Entry(LockEvent.UnexpectedShutdown, alive) with
                        { RecordedAt = Utc(system?.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value ?? "") }] : [];
            case ("Microsoft-Windows-Kernel-General", 13):
                return Utc(system?.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value ?? "") is { } stop
                    ? [Entry(LockEvent.SystemShutdown, stop)] : [];
            default: return [];
        }
    }

    private static DateTimeOffset? LastAlive(string? hex)
    {
        if (hex is null || hex.Length < 64) return null;
        try
        {
            var bytes = Convert.FromHexString(hex[..64]);
            int Word(int index) => BitConverter.ToUInt16(bytes, 16 + index * 2);
            return new DateTimeOffset(Word(0), Word(1), Word(3), Word(4), Word(5), Word(6), Word(7), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException) { return null; }
    }
}
