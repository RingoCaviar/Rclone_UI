using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace RcloneUI.Host;

internal enum HostLifecycleObservation
{
    SessionLocked,
    SessionUnlocked,
    Suspending,
    ResumedAutomatic,
    QueryEndSession,
    EndSession,
    RestartManagerQuery,
}

internal sealed record LifecycleRecord(HostLifecycleObservation Observation, DateTimeOffset RecordedUtc, string Action, double HandlerMilliseconds);

internal sealed class HostLifecycleJournal
{
    private readonly string path;
    private readonly object sync = new();

    internal HostLifecycleJournal(string dataRootPath)
    {
        var runtime = Path.Combine(dataRootPath, "runtime");
        Directory.CreateDirectory(runtime);
        path = Path.Combine(runtime, "lifecycle.jsonl");
    }

    internal void Record(HostLifecycleObservation observation, Stopwatch stopwatch)
    {
        lock (sync)
        {
            var record = new LifecycleRecord(observation, DateTimeOffset.UtcNow, "checkpoint-intent-only", stopwatch.Elapsed.TotalMilliseconds);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            JsonSerializer.Serialize(stream, record);
            stream.WriteByte((byte)'\n');
            stream.Flush(flushToDisk: true);
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed unsafe partial class HostLifecycleWindow : IDisposable
{
    private const uint WmClose = 0x0010;
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmWtsSessionChange = 0x02B1;
    private const int WtsSessionLock = 0x7;
    private const int WtsSessionUnlock = 0x8;
    private const int PbtApmSuspend = 0x4;
    private const int PbtApmResumeAutomatic = 0x12;
    private const int EndSessionCloseApp = 0x1;
    private static readonly ConcurrentDictionary<nint, HostLifecycleWindow> Windows = new();
    private readonly HostLifecycleJournal journal;
    private readonly ManualResetEventSlim started = new(false);
    private readonly Thread thread;
    private nint handle;
    private Exception? startupFailure;

    private HostLifecycleWindow(HostLifecycleJournal journal)
    {
        this.journal = journal;
        thread = new Thread(MessageLoop) { IsBackground = true, Name = "RcloneUI Host lifecycle window" };
        thread.Start();
        started.Wait();
        if (startupFailure is not null) throw new InvalidOperationException("The Host lifecycle window could not start.", startupFailure);
    }

    internal static HostLifecycleWindow Start(HostLifecycleJournal journal) => new(journal);

    public void Dispose()
    {
        if (handle != 0) NativeMethods.PostMessage(handle, WmClose, 0, 0);
        thread.Join();
        started.Dispose();
    }

    private void MessageLoop()
    {
        var className = $"RcloneUI.Host.Lifecycle.{Guid.NewGuid():N}";
        var classNamePointer = Marshal.StringToHGlobalUni(className);
        try
        {
            var windowClass = new WindowClass
            {
                Size = (uint)sizeof(WindowClass),
                WindowProcedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProcedure,
                Instance = NativeMethods.GetModuleHandle(null),
                ClassName = classNamePointer,
            };
            if (NativeMethods.RegisterClass(ref windowClass) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            handle = NativeMethods.CreateWindow(0, className, "RcloneUI Background Host", 0, -32_000, -32_000, 1, 1, 0, 0, windowClass.Instance, 0);
            if (handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Windows[handle] = this;
            if (!NativeMethods.WtsRegisterSessionNotification(handle, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            started.Set();
            while (NativeMethods.GetMessage(out var message, 0, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(message);
                NativeMethods.DispatchMessage(message);
            }
        }
        catch (Exception exception)
        {
            startupFailure = exception;
            started.Set();
        }
        finally
        {
            if (handle != 0)
            {
                NativeMethods.WtsUnregisterSessionNotification(handle);
                Windows.TryRemove(handle, out _);
                NativeMethods.DestroyWindow(handle);
                handle = 0;
            }

            Marshal.FreeHGlobal(classNamePointer);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        if (!Windows.TryGetValue(window, out var owner)) return NativeMethods.DefWindowProcedure(window, message, wParam, lParam);
        var stopwatch = Stopwatch.StartNew();
        HostLifecycleObservation? observation = message switch
        {
            WmWtsSessionChange when (int)wParam == WtsSessionLock => HostLifecycleObservation.SessionLocked,
            WmWtsSessionChange when (int)wParam == WtsSessionUnlock => HostLifecycleObservation.SessionUnlocked,
            WmPowerBroadcast when (int)wParam == PbtApmSuspend => HostLifecycleObservation.Suspending,
            WmPowerBroadcast when (int)wParam == PbtApmResumeAutomatic => HostLifecycleObservation.ResumedAutomatic,
            WmQueryEndSession when ((long)lParam & EndSessionCloseApp) != 0 => HostLifecycleObservation.RestartManagerQuery,
            WmQueryEndSession => HostLifecycleObservation.QueryEndSession,
            WmEndSession when wParam != 0 => HostLifecycleObservation.EndSession,
            _ => null,
        };
        if (observation is not null)
        {
            owner.journal.Record(observation.Value, stopwatch);
            return message is WmQueryEndSession or WmPowerBroadcast ? 1 : 0;
        }

        if (message == WmClose)
        {
            NativeMethods.PostQuitMessage(0);
            return 0;
        }

        return NativeMethods.DefWindowProcedure(window, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        internal uint Size;
        internal uint Style;
        internal nint WindowProcedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal nint MenuName;
        internal nint ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal nint Window;
        internal uint Message;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int PointX;
        internal int PointY;
        internal uint Private;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint GetModuleHandle(string? moduleName);

        [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
        internal static partial ushort RegisterClass(ref WindowClass windowClass);

        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint CreateWindow(uint extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

        [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
        internal static partial nint DefWindowProcedure(nint window, uint message, nuint wParam, nint lParam);

        [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
        internal static partial int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TranslateMessage(NativeMessage message);

        [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
        internal static partial nint DispatchMessage(NativeMessage message);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyWindow(nint window);

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

        [LibraryImport("user32.dll")]
        internal static partial void PostQuitMessage(int exitCode);

        [LibraryImport("wtsapi32.dll", EntryPoint = "WTSRegisterSessionNotification", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool WtsRegisterSessionNotification(nint window, int flags);

        [LibraryImport("wtsapi32.dll", EntryPoint = "WTSUnRegisterSessionNotification")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool WtsUnregisterSessionNotification(nint window);
    }
}

internal enum DurableWorkStatus
{
    Running,
    Completed,
    InterruptedBySystemOrCrash,
}

internal sealed record DurableWorkState(Guid WorkId, DurableWorkStatus Status);

internal sealed class HostWorkReconciler
{
    private readonly string path;

    internal HostWorkReconciler(string dataRootPath)
    {
        path = Path.Combine(dataRootPath, "runtime", "work-state.json");
        if (!File.Exists(path)) return;
        var states = Read();
        if (states.Any(state => state.Status == DurableWorkStatus.Running))
            Write(states.Select(state => state.Status == DurableWorkStatus.Running ? state with { Status = DurableWorkStatus.InterruptedBySystemOrCrash } : state).ToArray());
    }

    internal void Record(DurableWorkState state) => Write([state]);

    internal IReadOnlyList<DurableWorkState> Observe() => File.Exists(path) ? Read() : [];

    private DurableWorkState[] Read()
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Work journal is oversized.");
        return JsonSerializer.Deserialize<DurableWorkState[]>(bytes) ?? throw new InvalidDataException("Work journal is invalid.");
    }

    private void Write(DurableWorkState[] states)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, states);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
