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
}
