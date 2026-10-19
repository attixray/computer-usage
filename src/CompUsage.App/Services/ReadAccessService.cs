using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace CompUsage.App.Services;

public sealed class ReadAccessService
{
    public const int EventLogRead = 0x1;
    public const int AuditQuerySystemPolicy = 0x2;

    public void Grant(string userSid)
    {
        var sid = new SecurityIdentifier(userSid);
        if (sid != WindowsIdentity.GetCurrent().User) throw new UnauthorizedAccessException("A segédet ugyanazzal a Windows-fiókkal kell jóváhagyni.");
        using var privilege = new SecurityPrivilege();
        using var log = new EventLogConfiguration("Security");
        var originalLog = log.SecurityDescriptor ?? throw new InvalidDataException("A Security napló jogosultságai nem olvashatók.");
        var originalAudit = AuditDescriptor();
        var updatedLog = AddReadAccess(originalLog, userSid, EventLogRead);
        var updatedAudit = AddReadAccess(originalAudit, userSid, AuditQuerySystemPolicy);
        // Add only read rights to this account. Keep existing owners, deny entries and other permissions.
        if (updatedLog != originalLog) { log.SecurityDescriptor = updatedLog; log.SaveChanges(); }
        if (updatedAudit != originalAudit)
        {
            var descriptor = new RawSecurityDescriptor(updatedAudit);
            var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
            if (!AuditSetSecurity(4, bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public static string AddReadAccess(string sddl, string userSid, int access)
    {
        if (access is not (EventLogRead or AuditQuerySystemPolicy)) throw new ArgumentException("Invalid read access mask.");
        var sid = new SecurityIdentifier(userSid);
        var descriptor = new RawSecurityDescriptor(sddl);
        var acl = descriptor.DiscretionaryAcl;
        // A null DACL already grants access. Never replace it with a restrictive one as a side effect.
        if (acl is null) return sddl;
        foreach (var ace in acl.OfType<CommonAce>().Where(a => a.SecurityIdentifier == sid && (a.AceFlags & AceFlags.InheritOnly) == 0))
        {
            if (ace.AceQualifier == AceQualifier.AccessDenied && (ace.AccessMask & access) != 0)
                throw new UnauthorizedAccessException("A Windows-házirend kifejezetten tiltja ezt az olvasási jogot.");
            if (ace.AceQualifier == AceQualifier.AccessAllowed && (ace.AccessMask & access) == access) return sddl;
        }
        var index = 0;
        while (index < acl.Count && (acl[index].AceFlags & AceFlags.Inherited) == 0) index++;
        acl.InsertAce(index, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, access, sid, false, null));
        return descriptor.GetSddlForm(AccessControlSections.All);
    }

    private static string AuditDescriptor()
    {
        if (!AuditQuerySecurity(4, out var buffer)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var bytes = new byte[GetSecurityDescriptorLength(buffer)]; Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return new RawSecurityDescriptor(bytes, 0).GetSddlForm(AccessControlSections.Access);
        }
        finally { AuditFree(buffer); }
    }

    private sealed class SecurityPrivilege : IDisposable
    {
        private readonly SafeAccessTokenHandle token;
        private TokenPrivileges previous;
        public SecurityPrivilege()
        {
            if (!OpenProcessToken(new IntPtr(-1), 0x28, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!LookupPrivilegeValue(null, "SeSecurityPrivilege", out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var requested = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
                if (!AdjustTokenPrivileges(token, false, ref requested, Marshal.SizeOf<TokenPrivileges>(), out previous, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var error = Marshal.GetLastWin32Error();
                if (error != 0) throw new Win32Exception(error);
            }
            catch { token.Dispose(); throw; }
        }
        public void Dispose()
        {
            RestoreTokenPrivileges(token, false, ref previous, 0, IntPtr.Zero, IntPtr.Zero);
            token.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivileges state,
        int length, out TokenPrivileges previous, out int returned);
    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RestoreTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivileges state,
        int length, IntPtr previous, IntPtr returned);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AuditQuerySecurity(uint information, out IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AuditSetSecurity(uint information, byte[] descriptor);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("advapi32.dll")]
    private static extern void AuditFree(IntPtr buffer);
}
