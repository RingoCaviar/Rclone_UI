using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();
        using var scratch = new ScratchDirectory();
        using var window = new LifecycleWindow(scratch.Path);
        var ran = false;
        Application.Idle += (_, _) =>
        {
            if (ran) return;
            ran = true;
            window.RunSelfTest();
            Application.ExitThread();
        };
        Application.Run();
        Console.WriteLine(JsonSerializer.Serialize(window.Report(), new JsonSerializerOptions { WriteIndented = true }));
        return window.Passed ? 0 : 2;
    }
}

internal sealed class LifecycleWindow : NativeWindow, IDisposable
{
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;
    private const int WmPowerBroadcast = 0x0218;
    private const int WmWtsSessionChange = 0x02B1;
    private const int WtsSessionLock = 0x7;
    private const int WtsSessionUnlock = 0x8;
    private const int PbtApmSuspend = 0x4;
    private const int PbtApmResumeAutomatic = 0x12;
    private const int EndSessionCloseApp = 0x1;
    private const int NotifyForThisSession = 0;

    private readonly string journalPath;
    private readonly List<EventRecord> events = [];
    private bool registered;
    private bool queryAccepted;
    private double maxHandlerMs;

    internal LifecycleWindow(string scratch)
    {
        journalPath = Path.Combine(scratch, "lifecycle-journal.jsonl");
        CreateHandle(new CreateParams
        {
            Caption = "RcloneUI PROTOTYPE Hidden Host Window",
            ClassName = null,
            Style = 0,
            ExStyle = 0,
            X = -32000,
            Y = -32000,
            Width = 1,
            Height = 1
        });
        registered = Native.WTSRegisterSessionNotification(Handle, NotifyForThisSession);
    }

    internal bool Passed => registered && queryAccepted && maxHandlerMs < 2000 &&
        new[] { "SessionLocked", "SessionUnlocked", "Suspending", "ResumedAutomatic", "QueryEndSession", "EndSession", "RestartManagerQuery" }
            .All(name => events.Any(item => item.Name == name));

    internal void RunSelfTest()
    {
        Native.SendMessage(Handle, WmWtsSessionChange, new IntPtr(WtsSessionLock), IntPtr.Zero);
        Native.SendMessage(Handle, WmWtsSessionChange, new IntPtr(WtsSessionUnlock), IntPtr.Zero);
        Native.SendMessage(Handle, WmPowerBroadcast, new IntPtr(PbtApmSuspend), IntPtr.Zero);
        Native.SendMessage(Handle, WmPowerBroadcast, new IntPtr(PbtApmResumeAutomatic), IntPtr.Zero);
        var query = Native.SendMessage(Handle, WmQueryEndSession, IntPtr.Zero, IntPtr.Zero);
        queryAccepted = query != IntPtr.Zero;
        Native.SendMessage(Handle, WmEndSession, new IntPtr(1), IntPtr.Zero);
        Native.SendMessage(Handle, WmQueryEndSession, IntPtr.Zero, new IntPtr(EndSessionCloseApp));
    }

    protected override void WndProc(ref Message message)
    {
        var sw = Stopwatch.StartNew();
        switch (message.Msg)
        {
            case WmWtsSessionChange when message.WParam.ToInt32() == WtsSessionLock:
                Record("SessionLocked", durable: true); break;
            case WmWtsSessionChange when message.WParam.ToInt32() == WtsSessionUnlock:
                Record("SessionUnlocked", durable: true); break;
            case WmPowerBroadcast when message.WParam.ToInt32() == PbtApmSuspend:
                Record("Suspending", durable: true); message.Result = new IntPtr(1); break;
            case WmPowerBroadcast when message.WParam.ToInt32() == PbtApmResumeAutomatic:
                Record("ResumedAutomatic", durable: true); message.Result = new IntPtr(1); break;
            case WmQueryEndSession:
                Record((message.LParam.ToInt64() & EndSessionCloseApp) != 0 ? "RestartManagerQuery" : "QueryEndSession", durable: true);
                message.Result = new IntPtr(1);
                sw.Stop();
                maxHandlerMs = Math.Max(maxHandlerMs, sw.Elapsed.TotalMilliseconds);
                return;
            case WmEndSession when message.WParam != IntPtr.Zero:
                Record("EndSession", durable: true); break;
            default:
                base.WndProc(ref message); break;
        }
        sw.Stop();
        maxHandlerMs = Math.Max(maxHandlerMs, sw.Elapsed.TotalMilliseconds);
    }

    private void Record(string name, bool durable)
    {
        var record = new EventRecord(name, DateTimeOffset.UtcNow, "checkpoint-intent-only");
        events.Add(record);
        if (!durable) return;
        using var stream = new FileStream(journalPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.WriteLine(JsonSerializer.Serialize(record));
        writer.Flush();
        stream.Flush(true);
    }

    internal object Report() => new
    {
        prototype = true,
        os = Environment.OSVersion.VersionString,
        framework = RuntimeInformation.FrameworkDescription,
        hiddenWindowHandle = Handle.ToInt64(),
        wtsRegistrationSucceeded = registered,
        queryEndSessionAccepted = queryAccepted,
        maxHandlerMs = Math.Round(maxHandlerMs, 3),
        journalLines = File.ReadLines(journalPath).Count(),
        events,
        passed = Passed,
        untested = new[] { "real lock/unlock", "real suspend/hibernate/resume", "Restart Manager initiated close", "real logoff", "real shutdown", "forced shutdown", "Windows 10", ".NET 10", "actual rclone cancellation", "actual Mount drain" }
    };

    public void Dispose()
    {
        if (registered) Native.WTSUnRegisterSessionNotification(Handle);
        DestroyHandle();
    }
}

internal sealed record EventRecord(string Name, DateTimeOffset AtUtc, string Action);

internal sealed class ScratchDirectory : IDisposable
{
    internal string Path { get; } = Directory.CreateTempSubdirectory("RcloneUI-PROTOTYPE-lifecycle-").FullName;
    public void Dispose() => Directory.Delete(Path, true);
}

internal static partial class Native
{
    [LibraryImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool WTSRegisterSessionNotification(IntPtr window, int flags);
    [LibraryImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool WTSUnRegisterSessionNotification(IntPtr window);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")] internal static partial IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
