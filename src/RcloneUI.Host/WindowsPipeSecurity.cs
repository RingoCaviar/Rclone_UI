using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RcloneUI.Host;

[SupportedOSPlatform("windows")]
internal sealed record HostWindowsIdentity(SecurityIdentifier UserSid, SecurityIdentifier LogonSid, int SessionId)
{
    internal static HostWindowsIdentity Current()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return new(identity.User ?? throw new InvalidOperationException("The current Windows token has no user SID."), TokenInspector.GetLogonSid(identity.AccessToken), process.SessionId);
    }
}

[SupportedOSPlatform("windows")]
internal static class SecureNamedPipeFactory
{
    internal static NamedPipeServerStream Create(string pipeName, HostWindowsIdentity identity, bool firstInstance)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new(identity.LogonSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            65_536,
            65_536,
            security,
            HandleInheritability.None);
    }

    internal static bool VerifyConnectedClient(NamedPipeServerStream pipe, HostWindowsIdentity expected)
    {
        SecurityIdentifier? userSid = null;
        SecurityIdentifier? logonSid = null;
        var sessionId = -1;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            userSid = identity.User;
            logonSid = TokenInspector.GetLogonSid(identity.AccessToken);
            sessionId = GetTokenSessionId(identity.AccessToken);
        });
        return userSid == expected.UserSid && logonSid == expected.LogonSid && sessionId == expected.SessionId;
    }

    private static int GetTokenSessionId(SafeAccessTokenHandle token)
    {
        var sessionId = 0;
        var returnLength = 0;
        if (!TokenInspector.GetTokenInformation(token, 12, ref sessionId, sizeof(int), ref returnLength))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return sessionId;
    }
}

[SupportedOSPlatform("windows")]
internal static partial class TokenInspector
{
    private const uint SeGroupLogonId = 0xC0000000;

    internal static unsafe SecurityIdentifier GetLogonSid(SafeAccessTokenHandle token)
    {
        GetTokenInformation(token, 2, 0, 0, out var length);
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, 2, buffer, length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var count = Marshal.ReadInt32(buffer);
            var entry = buffer + IntPtr.Size;
            var size = sizeof(SidAndAttributes);
            SecurityIdentifier? result = null;
            for (var index = 0; index < count; index++)
            {
                var value = *(SidAndAttributes*)(entry + (index * size));
                if ((value.Attributes & SeGroupLogonId) != SeGroupLogonId) continue;
                if (result is not null) throw new InvalidOperationException("The token contains multiple logon SIDs.");
                result = new(value.Sid);
            }

            return result ?? throw new InvalidOperationException("The token contains no logon SID.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, nint information, int length, out int returnLength);

    [LibraryImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, ref int information, int length, ref int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        internal nint Sid;
        internal uint Attributes;
    }
}
