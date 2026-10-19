using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CompUsage.App.Services;

public sealed class AuditPolicyService
{
    public static readonly Guid Subcategory = new("0CCE921C-69AE-11D9-BED3-505054503030");

    public bool IsEnabled()
    {
        if (!AuditQuerySystemPolicy([Subcategory], 1, out var buffer))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var policy = Marshal.PtrToStructure<AuditPolicyInformation>(buffer);
            return (policy.AuditingInformation & 1) != 0;
        }
        finally { AuditFree(buffer); }
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "auditpol.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("/set");
        start.ArgumentList.Add($"/subcategory:{{{Subcategory}}}");
        start.ArgumentList.Add(enabled ? "/success:enable" : "/success:disable");
        using var process = Process.Start(start) ?? throw new IOException("Az auditpol nem indult el.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            throw new TimeoutException("A Windows nem válaszolt az auditbeállítás módosítására.");
        }
        if (process.ExitCode != 0)
            throw new IOException($"Az auditbeállítás módosítása sikertelen. {await output} {await errors}".Trim());
        await output;
        await errors;
        if (IsEnabled() != enabled)
            throw new IOException("A Windows nem alkalmazta a kért auditbeállítást.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AuditPolicyInformation
    {
        public Guid AuditSubCategoryGuid;
        public uint AuditingInformation;
        public Guid AuditCategoryGuid;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AuditQuerySystemPolicy([In] Guid[] subcategories, uint count, out IntPtr policies);

    [DllImport("advapi32.dll")]
    private static extern void AuditFree(IntPtr buffer);
}
