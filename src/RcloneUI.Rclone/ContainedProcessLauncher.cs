using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RcloneUI.Rclone;

public enum ContainedLaunchFailure
{
    CreateProcessFailed,
    HostEnvironmentJobConflict,
    MembershipVerificationFailed,
    ResumeFailed,
}

public sealed class ContainedLaunchException(ContainedLaunchFailure failure, int nativeError)
    : Exception($"Contained rclone launch failed: {failure} (Win32 {nativeError}).")
{
    public ContainedLaunchFailure Failure { get; } = failure;
    public int NativeError { get; } = nativeError;
}

[SupportedOSPlatform("windows")]
public sealed class RcloneJob : IDisposable
{
    private readonly SafeKernelHandle job;
    private readonly SafeKernelHandle completionPort;

    public RcloneJob()
    {
        job = NativeMethods.CreateJobObject(0, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        completionPort = NativeMethods.CreateIoCompletionPort(new(-1, ownsHandle: false), 0, 0, 1);
        try
        {
            if (completionPort.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new JobExtendedLimitInformation { BasicLimitInformation = new() { LimitFlags = 0x00002000 } };
            if (!NativeMethods.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimitInformation>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var association = new JobCompletionPort { CompletionKey = 1, CompletionPort = completionPort.DangerousGetHandle() };
            if (!NativeMethods.SetInformationJobObject(job, 7, ref association, (uint)Marshal.SizeOf<JobCompletionPort>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch
        {
            completionPort.Dispose();
            job.Dispose();
            throw;
        }
    }

    public ContainedRcloneProcess Launch(VerifiedRcloneBinary binary, IReadOnlyList<string> arguments)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var commandLine = Quote(binary.Path) + string.Concat(arguments.Select(argument => " " + Quote(argument)));
        var commandLinePointer = Marshal.StringToHGlobalUni(commandLine);
        ProcessInformation info;
        try
        {
            if (!NativeMethods.CreateProcess(binary.Path, commandLinePointer, 0, 0, false, 0x00000004 | 0x08000000, 0, Path.GetDirectoryName(binary.Path), ref startup, out info))
                throw new ContainedLaunchException(ContainedLaunchFailure.CreateProcessFailed, Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(commandLinePointer);
        }
        using var thread = new SafeKernelHandle(info.Thread, ownsHandle: true);
        var process = new SafeKernelHandle(info.Process, ownsHandle: true);
        try
        {
            if (!NativeMethods.AssignProcessToJobObject(job, process))
                throw new ContainedLaunchException(ContainedLaunchFailure.HostEnvironmentJobConflict, Marshal.GetLastWin32Error());
            if (!NativeMethods.IsProcessInJob(process, job, out var contained) || !contained)
                throw new ContainedLaunchException(ContainedLaunchFailure.MembershipVerificationFailed, Marshal.GetLastWin32Error());
            if (NativeMethods.ResumeThread(thread) == uint.MaxValue)
                throw new ContainedLaunchException(ContainedLaunchFailure.ResumeFailed, Marshal.GetLastWin32Error());
            return new(process, info.ProcessId, binary.Identity);
        }
        catch
        {
            NativeMethods.TerminateProcess(process, 0xDEAD);
            process.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        job.Dispose();
        completionPort.Dispose();
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder(value.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"') result.Append('\\', (backslashes * 2) + 1);
            else result.Append('\\', backslashes);
            result.Append(character);
            backslashes = 0;
        }

        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }
}

public sealed class ContainedRcloneProcess(SafeKernelHandle process, uint processId, RcloneBinaryIdentity binary) : IDisposable
{
    public uint ProcessId { get; } = processId;
    public RcloneBinaryIdentity Binary { get; } = binary;
    public bool WaitForExit(TimeSpan timeout) => NativeMethods.WaitForSingleObject(process, checked((uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1))) == 0;
    public void Dispose() => process.Dispose();
}

public sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeKernelHandle() : base(true) { }
    public SafeKernelHandle(nint handle, bool ownsHandle) : base(ownsHandle) => SetHandle(handle);
    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfo
{
    internal int Size;
    private nint reserved;
    private nint desktop;
    private nint title;
    private uint x;
    private uint y;
    private uint xSize;
    private uint ySize;
    private uint xCountChars;
    private uint yCountChars;
    private uint fillAttribute;
    private uint flags;
    private ushort showWindow;
    private ushort reserved2;
    private nint reserved2Pointer;
    private nint standardInput;
    private nint standardOutput;
    private nint standardError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation { internal nint Process; internal nint Thread; internal uint ProcessId; internal uint ThreadId; }
[StructLayout(LayoutKind.Sequential)]
internal struct IoCounters { internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
[StructLayout(LayoutKind.Sequential)]
internal struct JobBasicLimitInformation { internal long PerProcessUserTimeLimit, PerJobUserTimeLimit; internal uint LimitFlags; internal nuint MinimumWorkingSetSize, MaximumWorkingSetSize; internal uint ActiveProcessLimit; internal nuint Affinity; internal uint PriorityClass, SchedulingClass; }
[StructLayout(LayoutKind.Sequential)]
internal struct JobExtendedLimitInformation { internal JobBasicLimitInformation BasicLimitInformation; internal IoCounters IoInfo; internal nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
[StructLayout(LayoutKind.Sequential)]
internal struct JobCompletionPort { internal nuint CompletionKey; internal nint CompletionPort; }

internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] internal static partial SafeKernelHandle CreateJobObject(nint attributes, string? name);
    [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetInformationJobObject(SafeKernelHandle job, int informationClass, ref JobExtendedLimitInformation information, uint length);
    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetInformationJobObject(SafeKernelHandle job, int informationClass, ref JobCompletionPort information, uint length);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool CreateProcess(string applicationName, nint commandLine, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool AssignProcessToJobObject(SafeKernelHandle job, SafeKernelHandle process);
    [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool IsProcessInJob(SafeKernelHandle process, SafeKernelHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial uint ResumeThread(SafeKernelHandle thread);
    [LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool TerminateProcess(SafeKernelHandle process, uint exitCode);
    [LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool CloseHandle(nint handle);
    [LibraryImport("kernel32.dll")] internal static partial uint WaitForSingleObject(SafeKernelHandle handle, uint milliseconds);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial SafeKernelHandle CreateIoCompletionPort(SafeKernelHandle fileHandle, nint existingCompletionPort, nuint completionKey, uint concurrentThreads);
}
