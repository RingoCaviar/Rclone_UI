using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

if (args is ["--worker", var marker])
{
    using var grandchild = Process.Start(new ProcessStartInfo
    {
        FileName = Path.Combine(Environment.SystemDirectory, "PING.EXE"),
        Arguments = "127.0.0.1 -t",
        UseShellExecute = false,
        CreateNoWindow = true
    })!;
    File.WriteAllText(marker, JsonSerializer.Serialize(new { workerPid = Environment.ProcessId, grandchildPid = grandchild.Id }));
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

var scratch = Directory.CreateTempSubdirectory("RcloneUI-PROTOTYPE-job-");
var markerPath = Path.Combine(scratch.FullName, "pids.json");
var job = Native.CreateJobObject(IntPtr.Zero, null);
if (job == IntPtr.Zero) throw Win32("CreateJobObject");
var completionPort = Native.CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
if (completionPort == IntPtr.Zero) throw Win32("CreateIoCompletionPort");

try
{
    var limits = new Native.JobObjectExtendedLimitInformation();
    limits.BasicLimitInformation.LimitFlags = Native.JobObjectLimitKillOnJobClose;
    SetJob(job, Native.JobObjectInfoClass.ExtendedLimitInformation, limits);
    SetJob(job, Native.JobObjectInfoClass.AssociateCompletionPortInformation,
        new Native.JobObjectAssociateCompletionPort { CompletionKey = new IntPtr(0x5243), CompletionPort = completionPort });

    Native.IsProcessInJob(Process.GetCurrentProcess().Handle, IntPtr.Zero, out var hostAlreadyInJob);
    var exe = Environment.ProcessPath!;
    var command = new StringBuilder($"\"{exe}\" --worker \"{markerPath}\"");
    var startup = new Native.StartupInfo { cb = Marshal.SizeOf<Native.StartupInfo>() };
    if (!Native.CreateProcess(exe, command, IntPtr.Zero, IntPtr.Zero, false,
            Native.CreateSuspended | Native.CreateNoWindow, IntPtr.Zero, null, ref startup, out var created))
        throw Win32("CreateProcess");

    try
    {
        var aliveBeforeAssign = Process.GetProcessById((int)created.dwProcessId).HasExited == false;
        var noWorkerSideEffectBeforeResume = !File.Exists(markerPath);
        if (!Native.AssignProcessToJobObject(job, created.hProcess)) throw Win32("AssignProcessToJobObject");
        Native.IsProcessInJob(created.hProcess, job, out var workerAssignedBeforeResume);
        if (Native.ResumeThread(created.hThread) == uint.MaxValue) throw Win32("ResumeThread");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(markerPath) && DateTime.UtcNow < deadline) await Task.Delay(25);
        if (!File.Exists(markerPath)) throw new TimeoutException("Worker did not publish descendant PID.");
        var pids = JsonSerializer.Deserialize<Pids>(File.ReadAllText(markerPath))!;
        using var grandchild = Process.GetProcessById(pids.grandchildPid);
        Native.IsProcessInJob(grandchild.Handle, job, out var grandchildInheritedJob);

        var events = new List<object>();
        var eventDeadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < eventDeadline && events.Count < 2)
        {
            if (Native.GetQueuedCompletionStatus(completionPort, out var message, out var key, out var value, 100))
                events.Add(new { message, completionKey = key.ToUInt64(), processId = value.ToInt64() });
        }

        Native.CloseHandle(job);
        job = IntPtr.Zero;
        var workerExited = WaitExited(pids.workerPid, 5000);
        var grandchildExited = WaitExited(pids.grandchildPid, 5000);

        var report = new
        {
            prototype = true,
            os = Environment.OSVersion.VersionString,
            framework = RuntimeInformation.FrameworkDescription,
            hostAlreadyInExternalJob = hostAlreadyInJob,
            childCreatedSuspended = aliveBeforeAssign && noWorkerSideEffectBeforeResume,
            workerAssignedBeforeResume,
            grandchildInheritedJob,
            completionPortEvents = events,
            killOnJobClose = new { workerExited, grandchildExited },
            untested = new[] { "real rclone executable", "Windows 10", ".NET 10", "Explorer", "Windows Terminal", "debugger", "CI runner", "updater", "intentionally incompatible parent job" }
        };
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return workerAssignedBeforeResume && grandchildInheritedJob && workerExited && grandchildExited ? 0 : 2;
    }
    finally
    {
        Native.CloseHandle(created.hThread);
        Native.CloseHandle(created.hProcess);
    }
}
finally
{
    if (job != IntPtr.Zero) Native.CloseHandle(job);
    if (completionPort != IntPtr.Zero) Native.CloseHandle(completionPort);
    scratch.Delete(true);
}

static void SetJob<T>(IntPtr job, Native.JobObjectInfoClass infoClass, T value) where T : struct
{
    var size = Marshal.SizeOf<T>();
    var buffer = Marshal.AllocHGlobal(size);
    try
    {
        Marshal.StructureToPtr(value, buffer, false);
        if (!Native.SetInformationJobObject(job, infoClass, buffer, (uint)size)) throw Win32("SetInformationJobObject");
    }
    finally { Marshal.FreeHGlobal(buffer); }
}

static bool WaitExited(int pid, int milliseconds)
{
    try { using var process = Process.GetProcessById(pid); return process.WaitForExit(milliseconds); }
    catch (ArgumentException) { return true; }
}

static Exception Win32(string call) => new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), call);

internal sealed record Pids(int workerPid, int grandchildPid);

static partial class Native
{
    internal const uint CreateSuspended = 0x00000004;
    internal const uint CreateNoWindow = 0x08000000;
    internal const uint JobObjectLimitKillOnJobClose = 0x00002000;

    internal enum JobObjectInfoClass { AssociateCompletionPortInformation = 7, ExtendedLimitInformation = 9 }

    [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] internal struct BasicLimitInformation { internal long PerProcessUserTimeLimit, PerJobUserTimeLimit; internal uint LimitFlags; internal UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; internal uint ActiveProcessLimit; internal UIntPtr Affinity; internal uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] internal struct JobObjectExtendedLimitInformation { internal BasicLimitInformation BasicLimitInformation; internal IoCounters IoInfo; internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential)] internal struct JobObjectAssociateCompletionPort { internal IntPtr CompletionKey, CompletionPort; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo { internal int cb; internal string? lpReserved, lpDesktop, lpTitle; internal int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; internal short wShowWindow, cbReserved2; internal IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { internal IntPtr hProcess, hThread; internal uint dwProcessId, dwThreadId; }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] internal static partial IntPtr CreateJobObject(IntPtr attributes, string? name);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetInformationJobObject(IntPtr job, JobObjectInfoClass infoClass, IntPtr info, uint length);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcess(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial uint ResumeThread(IntPtr thread);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial IntPtr CreateIoCompletionPort(IntPtr fileHandle, IntPtr existingCompletionPort, UIntPtr completionKey, uint concurrentThreads);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetQueuedCompletionStatus(IntPtr completionPort, out uint bytesTransferred, out UIntPtr completionKey, out IntPtr overlapped, uint milliseconds);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool CloseHandle(IntPtr handle);
}
