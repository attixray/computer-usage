using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CompUsage.Core;

// A coverage span contains observation times and input-update counts, never keys, text or application data.
public sealed record MeasurementSpan(string SessionId, string LogonId, string RunId, DateTimeOffset From, DateTimeOffset To,
    string State, bool? Remote, int Samples, int InputChanges, DateTimeOffset? FirstInputAt, DateTimeOffset? LastInputAt)
{
    public int Version { get; init; } = 1;
}

public sealed class MeasurementJournal(string directory)
{
    public string DirectoryPath => directory;
    public void Checkpoint(MeasurementSpan? span)
    {
        if (span is null) return;
        if (!int.TryParse(span.SessionId, out var id) || id < 0) throw new ArgumentException("Invalid Windows session ID.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"measurement-current-{id}.json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, span); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void RecoverCheckpoint(string session)
    {
        if (!int.TryParse(session, out var id) || id < 0) throw new ArgumentException("Invalid Windows session ID.");
        var path = Path.Combine(directory, $"measurement-current-{id}.json");
        if (!File.Exists(path)) return;
        var span = ReadCheckpoint(path);
        if (span is not null) Append(span);
        File.Delete(path);
    }

    private static MeasurementSpan? ReadCheckpoint(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var span = JsonSerializer.Deserialize<MeasurementSpan>(file);
            return Valid(span) ? span : null;
        }
        catch (JsonException) { return null; }
    }
    public void Append(MeasurementSpan span)
    {
        if (!int.TryParse(span.SessionId, NumberStyles.None, CultureInfo.InvariantCulture, out var session) || session < 0)
            throw new ArgumentException("Invalid Windows session ID.");
        if (span.From > span.To) throw new ArgumentException("Invalid measurement interval.");
        // Long sleeps or suspended processes may cross several UTC files. Keep each retained day's
        // gap discoverable when the reader asks only for that day, even if observation resumed later.
        if (span.State == "gap" && span.From.UtcDateTime.Date != span.To.UtcDateTime.Date)
        {
            var from = span.From > span.To.AddDays(-61) ? span.From : span.To.AddDays(-61);
            while (from < span.To)
            {
                var midnight = new DateTimeOffset(from.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
                var to = midnight < span.To ? midnight : span.To;
                AppendLine(span with { From = from, To = to }, session);
                from = to;
            }
            return;
        }
        AppendLine(span, session);
    }

    private void AppendLine(MeasurementSpan span, int session)
    {
        Directory.CreateDirectory(directory);
        var date = span.From.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"measurement-{session}-{date}.jsonl");
        using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        // A crash may leave a partial final line. Separate it from the next complete record.
        if (file.Length > 0)
        {
            file.Seek(-1, SeekOrigin.End);
            if (file.ReadByte() != '\n') { file.Seek(0, SeekOrigin.End); file.WriteByte((byte)'\n'); }
        }
        file.Seek(0, SeekOrigin.End);
        file.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(span) + "\n"));
        file.Flush(true);
    }

    public IReadOnlyList<MeasurementSpan> Read(DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<MeasurementSpan>();
        if (!Directory.Exists(directory)) return result;
        foreach (var path in Directory.EnumerateFiles(directory, "measurement-*.jsonl"))
        {
            var date = FileDate(path);
            // Spans are flushed at least every 30 seconds; allow the preceding UTC file at midnight.
            if (date is null || date < DateOnly.FromDateTime(from.UtcDateTime).AddDays(-1) || date > DateOnly.FromDateTime(to.UtcDateTime)) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    var span = JsonSerializer.Deserialize<MeasurementSpan>(line);
                    if (Valid(span) && span!.To >= from && span.From <= to) result.Add(span);
                }
                catch (JsonException) { /* A torn record is unknown coverage; later complete records are still usable. */ }
            }
        }
        foreach (var path in Directory.EnumerateFiles(directory, "measurement-current-*.json"))
            if (ReadCheckpoint(path) is { Version: 1 } span && span.From <= span.To && span.To >= from && span.From <= to) result.Add(span);
        return result.GroupBy(s => (s.SessionId, s.RunId, s.From, s.State, s.Remote)).Select(g => g.MaxBy(s => s.To)!)
            .OrderBy(s => s.From).ThenBy(s => s.To).ToArray();
    }

    public void Prune(DateTimeOffset now)
    {
        if (!Directory.Exists(directory)) return;
        var cutoff = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-61);
        foreach (var path in Directory.EnumerateFiles(directory, "measurement-*.jsonl"))
            if (FileDate(path) is { } date && date < cutoff) File.Delete(path);
        foreach (var path in Directory.EnumerateFiles(directory, "measurement-current-*.json"))
            if (ReadCheckpoint(path) is { } span && span.To < now.AddDays(-61)) File.Delete(path);
    }

    private static DateOnly? FileDate(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length >= 8 && DateOnly.TryParseExact(name[^8..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    private static bool Valid(MeasurementSpan? span) => span is { Version: 1, Samples: >= 0, InputChanges: >= 0 }
        && int.TryParse(span.SessionId, NumberStyles.None, CultureInfo.InvariantCulture, out var session) && session >= 0
        && span.LogonId is not null && !string.IsNullOrEmpty(span.RunId) && !string.IsNullOrEmpty(span.State)
        && span.From <= span.To && span.InputChanges <= span.Samples;
}

// One compact record per 30 seconds of stable measurement state. A poll delayed over five seconds
// explicitly breaks coverage; absence of an idle event must never be treated as proof of measurement.
public sealed class MeasurementRecorder(MeasurementJournal journal, string sessionId, string logonId)
{
    public static readonly TimeSpan MaximumPollGap = TimeSpan.FromSeconds(5);
    private readonly string run = Guid.NewGuid().ToString("N");
    private MeasurementSpan? pending;
    private DateTimeOffset? previousAt;
    private uint? previousInput;

    public void Observe(DateTimeOffset at, string state, bool? remote = null, uint? inputTick = null, DateTimeOffset? inputAt = null)
    {
        if (previousAt is { } previous && (at < previous || at - previous > MaximumPollGap))
        {
            Flush();
            journal.Append(new(sessionId, logonId, run, at < previous ? at : previous, at,
                at < previous ? "clock-change" : "gap", null, 0, 0, null, null));
            previousInput = null;
        }
        if (pending is not null && (pending.State != state || pending.Remote != remote)) Flush();
        if (pending is not null && pending.From.UtcTicks / TimeSpan.TicksPerMinute != at.UtcTicks / TimeSpan.TicksPerMinute) Flush();
        pending ??= new(sessionId, logonId, run, at, at, state, remote, 0, 0, null, null);
        // Compare raw Windows input ticks: converting the same tick to wall-clock time has sub-second jitter.
        var changed = state == "sampling" && inputTick is not null && previousInput is not null && inputTick != previousInput;
        pending = pending with { To = at, Samples = pending.Samples + (state == "sampling" ? 1 : 0),
            InputChanges = pending.InputChanges + (changed ? 1 : 0),
            FirstInputAt = pending.FirstInputAt ?? inputAt, LastInputAt = inputAt ?? pending.LastInputAt };
        previousAt = at;
        previousInput = state == "sampling" ? inputTick : null;
        if (pending.To - pending.From >= TimeSpan.FromSeconds(30))
        {
            Flush();
            pending = new(sessionId, logonId, run, at, at, state, remote, 0, 0, inputAt, inputAt);
        }
    }

    public void Flush()
    {
        if (pending is null) return;
        journal.Append(pending);
        pending = null;
    }

    public void Stop(DateTimeOffset at)
    {
        Observe(at, "stopped"); Flush();
        pending = null;
    }
    public MeasurementSpan? Current => pending;
}

public static class MeasurementSummary
{
    public static IReadOnlyList<MeasurementSpan> Timeline(IEnumerable<MeasurementSpan> records, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<MeasurementSpan>();
        foreach (var span in records.Where(s => s.To > from && s.From < to).OrderBy(s => s.From).ThenBy(s => s.To))
        {
            var clipped = span with { From = span.From < from ? from : span.From, To = span.To > to ? to : span.To };
            if (result.Count > 0 && result[^1] is var previous && previous.RunId == clipped.RunId
                && previous.SessionId == clipped.SessionId && previous.LogonId == clipped.LogonId
                && previous.State == clipped.State && previous.Remote == clipped.Remote && clipped.From >= previous.To
                && clipped.From - previous.To <= MeasurementRecorder.MaximumPollGap)
                result[^1] = previous with { To = clipped.To, Samples = previous.Samples + clipped.Samples,
                    InputChanges = previous.InputChanges + clipped.InputChanges, LastInputAt = clipped.LastInputAt ?? previous.LastInputAt };
            else result.Add(clipped);
        }
        // Missing coverage before, between and after durable spans stays explicitly unknown.
        var complete = new List<MeasurementSpan>();
        var cursor = from;
        foreach (var span in result)
        {
            if (span.From - cursor > MeasurementRecorder.MaximumPollGap)
                complete.Add(new("", "", "", cursor, span.From, "unknown", null, 0, 0, null, null));
            complete.Add(span);
            if (span.To > cursor) cursor = span.To;
        }
        if (to - cursor > MeasurementRecorder.MaximumPollGap)
            complete.Add(new("", "", "", cursor, to, "unknown", null, 0, 0, null, null));
        return complete;
    }
}
