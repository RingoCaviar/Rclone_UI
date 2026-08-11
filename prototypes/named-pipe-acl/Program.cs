using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

const int clientCount = 100;
var pipeName = $"RcloneUI-PROTOTYPE-{Guid.NewGuid():N}";
var current = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
var userSid = current.User!;
var logonSid = TokenInspector.GetLogonSid(current.AccessToken);
var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);

var security = new PipeSecurity();
security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
security.AddAccessRule(new PipeAccessRule(networkSid, PipeAccessRights.FullControl, AccessControlType.Deny));
security.AddAccessRule(new PipeAccessRule(logonSid, PipeAccessRights.FullControl, AccessControlType.Allow));

NamedPipeServerStream CreateServer(bool first) => NamedPipeServerStreamAcl.Create(
    pipeName,
    PipeDirection.InOut,
    clientCount,
    PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
    4096,
    4096,
    security,
    HandleInheritability.None);

var servers = new List<NamedPipeServerStream> { CreateServer(first: true) };
var firstInstanceProtected = false;
try { using var impostor = CreateServer(first: true); }
catch (IOException) { firstInstanceProtected = true; }
catch (UnauthorizedAccessException) { firstInstanceProtected = true; }
for (var i = 1; i < clientCount; i++) servers.Add(CreateServer(first: false));

var observedClients = new System.Collections.Concurrent.ConcurrentBag<object>();
var accepts = servers.Select(async server =>
{
    await server.WaitForConnectionAsync();
    string? impersonatedUser = null;
    string? impersonatedLogon = null;
    server.RunAsClient(() =>
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        impersonatedUser = identity.User?.Value;
        impersonatedLogon = TokenInspector.GetLogonSid(identity.AccessToken).Value;
    });
    var one = new byte[1];
    await server.ReadExactlyAsync(one);
    await server.WriteAsync(one);
    observedClients.Add(new { impersonatedUser, impersonatedLogon });
}).ToArray();

var clients = Enumerable.Range(0, clientCount).Select(async i =>
{
    await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
    await client.ConnectAsync(10_000);
    await client.WriteAsync(new[] { (byte)i });
    var reply = new byte[1];
    await client.ReadExactlyAsync(reply);
    return reply[0] == (byte)i;
}).ToArray();

await Task.WhenAll(clients);
await Task.WhenAll(accepts);

var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
    .Cast<PipeAccessRule>()
    .Select(rule => new
    {
        sid = rule.IdentityReference.Value,
        type = rule.AccessControlType.ToString(),
        rights = rule.PipeAccessRights.ToString()
    }).ToArray();

var report = new
{
    prototype = true,
    os = Environment.OSVersion.VersionString,
    framework = RuntimeInformation.FrameworkDescription,
    processSessionId = Environment.ProcessId is var pid ? System.Diagnostics.Process.GetProcessById(pid).SessionId : -1,
    currentUserSid = userSid.Value,
    logonSid = logonSid.Value,
    pipeName,
    firstInstanceProtected,
    aclProtected = security.AreAccessRulesProtected,
    rules,
    clientsConnected = clients.Count(task => task.Result),
    allClientUserSidsMatch = observedClients.All(item => (string?)item.GetType().GetProperty("impersonatedUser")!.GetValue(item) == userSid.Value),
    allClientLogonSidsMatch = observedClients.All(item => (string?)item.GetType().GetProperty("impersonatedLogon")!.GetValue(item) == logonSid.Value),
    untested = new[] { "other Windows user", "Fast User Switching session", "runas", "elevated token", "remote SMB pipe", "Windows 10", ".NET 10", "FAT/exFAT runtime-secret exposure" }
};

Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var server in servers) server.Dispose();
return firstInstanceProtected && clients.All(task => task.Result) ? 0 : 2;

static partial class TokenInspector
{
    private const uint SeGroupLogonId = 0xC0000000;
    private const int TokenGroups = 2;

    internal static SecurityIdentifier GetLogonSid(SafeAccessTokenHandle token)
    {
        GetTokenInformation(token, TokenGroups, IntPtr.Zero, 0, out var length);
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenGroups, buffer, length, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var count = Marshal.ReadInt32(buffer);
            var entry = buffer + IntPtr.Size;
            var size = Marshal.SizeOf<SidAndAttributes>();
            for (var i = 0; i < count; i++)
            {
                var value = Marshal.PtrToStructure<SidAndAttributes>(entry + i * size);
                if ((value.Attributes & SeGroupLogonId) == SeGroupLogonId)
                    return new SecurityIdentifier(value.Sid);
            }
            throw new InvalidOperationException("Current token has no logon SID.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { internal IntPtr Sid; internal uint Attributes; }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(SafeAccessTokenHandle token, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);
}
