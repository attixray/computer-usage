using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CompUsage.Core;

namespace CompUsage.App.Services;

public sealed record BrokerReply(EventReadResult? Data, bool? Logging, string? Error);
public sealed record BrokerRequest(string Operation, string Sid, bool? Enabled = null);

public sealed class SecurityBroker : IDisposable
{
    private readonly string sid = WindowsIdentity.GetCurrent().User!.Value;
    private readonly SemaphoreSlim gate = new(1, 1);
    private NamedPipeServerStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;

    public async Task<BrokerReply> RequestAsync(string operation, bool? enabled = null)
    {
        await gate.WaitAsync();
        try
        {
            if (pipe is null)
            {
                try { return await ExecuteAsync(new(operation, sid, enabled)); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception
                    or System.Diagnostics.Eventing.Reader.EventLogException) { }
                catch (IOException) when (operation == "audit") { }
                var name = "CompUsage-" + Guid.NewGuid().ToString("N");
                pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                    Arguments = $"--broker {name} {Environment.ProcessId}"
                };
                using var helper = Process.Start(start) ?? throw new IOException("Nem indult el a Windows-napló segédje.");
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await pipe.WaitForConnectionAsync(connectTimeout.Token);
                reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            }
            await writer!.WriteLineAsync(JsonSerializer.Serialize(new BrokerRequest(operation, sid, enabled)));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var line = await reader!.ReadLineAsync(timeout.Token) ?? throw new IOException("A Windows-segéd leállt.");
            var reply = JsonSerializer.Deserialize<BrokerReply>(line) ?? throw new IOException("Érvénytelen Windows-válasz.");
            if (reply.Error is not null) throw new IOException(reply.Error);
            return reply;
        }
        catch { Disconnect(); throw; }
        finally { gate.Release(); }
    }

    public static async Task RunAsync(string name, int parentId)
    {
        if (!Regex.IsMatch(name, "^CompUsage-[0-9a-f]{32}$")) return;
        using var parent = Process.GetProcessById(parentId);
        // A broker is useful only to this installed executable, under the same user's pipe ACL.
        if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return;
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(15000);
        using var input = new StreamReader(client, Encoding.UTF8, false, 4096, true);
        using var output = new StreamWriter(client, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        while (await input.ReadLineAsync() is { } line)
        {
            BrokerReply reply;
            try
            {
                if (line.Length > 2048) throw new InvalidDataException("Túl hosszú Windows-kérés.");
                var request = JsonSerializer.Deserialize<BrokerRequest>(line) ?? throw new InvalidDataException("Üres kérés.");
                if (request.Sid != WindowsIdentity.GetCurrent().User!.Value)
                    throw new UnauthorizedAccessException("A segédet ugyanazzal a Windows-fiókkal kell jóváhagyni.");
                reply = await ExecuteAsync(request);
            }
            catch (Exception ex) { reply = new(null, null, ex.Message); }
            await output.WriteLineAsync(JsonSerializer.Serialize(reply));
        }
    }

    private static async Task<BrokerReply> ExecuteAsync(BrokerRequest request)
    {
        var policy = new AuditPolicyService();
        switch (request.Operation)
        {
            case "read":
                var now = DateTimeOffset.UtcNow;
                var data = await Task.Run(() => new SecurityEventService().Read(request.Sid, now.AddDays(-61), now));
                return new(data, policy.IsEnabled(), null);
            case "audit" when request.Enabled is not null:
                await policy.SetEnabledAsync(request.Enabled.Value);
                return new(null, policy.IsEnabled(), null);
            default: throw new InvalidOperationException("Ismeretlen Windows-művelet.");
        }
    }
    private void Disconnect() { writer?.Dispose(); reader?.Dispose(); pipe?.Dispose(); writer = null; reader = null; pipe = null; }
    public void Dispose() { Disconnect(); gate.Dispose(); }
}
