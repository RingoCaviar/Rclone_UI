using System.Text.Json;
using System.Runtime.InteropServices;

var stepNames = new[]
{
    "PlanWritten", "VersionVerified", "HandoffIssued", "OldHostStopped",
    "VersionPointerSwitched", "NewHostBaseHealthy", "VaultStaged", "VaultVerified",
    "VaultPointerSwitched", "NewHealthPassed", "Committed"
};
var results = new List<object>();

foreach (var step in stepNames)
{
    foreach (var boundary in new[] { "before-action", "after-action", "after-journal" })
    {
        using var fixture = Fixture.Create();
        try { fixture.RunUpdate(step, boundary); } catch (InjectedFailure) { }
        var recovered = fixture.Recover(mediaAvailable: true);
        results.Add(new { fault = $"{step}:{boundary}", recovered, pass = IsValid(recovered) });
    }

    using (var fixture = Fixture.Create())
    {
        try { fixture.RunUpdate(step, "after-journal"); } catch (InjectedFailure) { }
        var absent = fixture.Recover(mediaAvailable: false);
        var returned = fixture.Recover(mediaAvailable: true);
        results.Add(new { fault = $"{step}:media-missing", absent, returned, pass = absent.State == "DataRootUnavailable" && IsValid(returned) });
    }
}

using (var fixture = Fixture.Create())
{
    fixture.RunUpdate(null, null, healthFails: true);
    var recovered = fixture.Recover(mediaAvailable: true);
    results.Add(new { fault = "new-health-failed", recovered, pass = recovered.State == "HealthyOld" });
}

var failed = results.Where(item => !(bool)item.GetType().GetProperty("pass")!.GetValue(item)!).ToArray();
Console.WriteLine(JsonSerializer.Serialize(new
{
    prototype = true,
    cases = results.Count,
    passed = results.Count - failed.Length,
    failed = failed.Length,
    results
}, new JsonSerializerOptions { WriteIndented = true }));
return failed.Length == 0 ? 0 : 2;

static bool IsValid(RecoveryResult result) =>
    result is { HostCount: 1 } &&
    (result is { State: "HealthyOld", ActiveVersion: "v1", VaultGeneration: "1" } ||
     result is { State: "HealthyNew", ActiveVersion: "v2", VaultGeneration: "2" });

internal sealed class Fixture : IDisposable
{
    private readonly string root;
    private readonly string journal;
    private static readonly string[] Steps =
    {
        "PlanWritten", "VersionVerified", "HandoffIssued", "OldHostStopped",
        "VersionPointerSwitched", "NewHostBaseHealthy", "VaultStaged", "VaultVerified",
        "VaultPointerSwitched", "NewHealthPassed", "Committed"
    };

    private Fixture(string root)
    {
        this.root = root;
        journal = Path.Combine(root, "update-journal.json");
    }

    internal static Fixture Create()
    {
        var root = Directory.CreateTempSubdirectory("RcloneUI-PROTOTYPE-update-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "versions", "v1"));
        Directory.CreateDirectory(Path.Combine(root, "versions", "v2"));
        Directory.CreateDirectory(Path.Combine(root, "vault", "1"));
        File.WriteAllText(Path.Combine(root, "versions", "v1", "verified"), "old");
        File.WriteAllText(Path.Combine(root, "vault", "1", "verified"), "old");
        AtomicWrite(Path.Combine(root, "active-version"), "v1");
        AtomicWrite(Path.Combine(root, "vault", "CURRENT"), "1");
        File.WriteAllText(Path.Combine(root, "host-v1.running"), "old-host");
        return new Fixture(root);
    }

    internal void RunUpdate(string? faultStep, string? boundary, bool healthFails = false)
    {
        foreach (var step in Steps)
        {
            if (step == faultStep && boundary == "before-action") throw new InjectedFailure();
            Apply(step, healthFails);
            if (step == faultStep && boundary == "after-action") throw new InjectedFailure();
            AtomicWrite(journal, JsonSerializer.Serialize(new Journal(step, "v1", "v2", "1", "2")));
            if (step == faultStep && boundary == "after-journal") throw new InjectedFailure();
            if (healthFails && step == "NewHealthPassed") return;
        }
    }

    private void Apply(string step, bool healthFails)
    {
        switch (step)
        {
            case "PlanWritten": break;
            case "VersionVerified": File.WriteAllText(Path.Combine(root, "versions", "v2", "verified"), "new"); break;
            case "HandoffIssued": File.WriteAllText(Path.Combine(root, "handoff.token"), Guid.NewGuid().ToString("N")); break;
            case "OldHostStopped": DeleteIfExists(Path.Combine(root, "host-v1.running")); break;
            case "VersionPointerSwitched": AtomicWrite(Path.Combine(root, "active-version"), "v2"); break;
            case "NewHostBaseHealthy": File.WriteAllText(Path.Combine(root, "host-v2.running"), "new-host-base"); break;
            case "VaultStaged": Directory.CreateDirectory(Path.Combine(root, "vault", "2")); File.WriteAllText(Path.Combine(root, "vault", "2", "staged"), "new"); break;
            case "VaultVerified": File.WriteAllText(Path.Combine(root, "vault", "2", "verified"), "new"); break;
            case "VaultPointerSwitched": AtomicWrite(Path.Combine(root, "vault", "CURRENT"), "2"); break;
            case "NewHealthPassed":
                if (healthFails) { DeleteIfExists(Path.Combine(root, "host-v2.running")); return; }
                File.WriteAllText(Path.Combine(root, "host-v2.healthy"), "healthy"); break;
            case "Committed": DeleteIfExists(Path.Combine(root, "handoff.token")); break;
        }
    }

    internal RecoveryResult Recover(bool mediaAvailable)
    {
        if (!mediaAvailable) return new("DataRootUnavailable", null, null, CountHosts());
        Journal? state = null;
        try { if (File.Exists(journal)) state = JsonSerializer.Deserialize<Journal>(File.ReadAllText(journal)); } catch { }
        var canUseNew = state is not null && Array.IndexOf(Steps, state.Phase) >= Array.IndexOf(Steps, "NewHealthPassed") &&
            File.Exists(Path.Combine(root, "versions", "v2", "verified")) &&
            File.Exists(Path.Combine(root, "vault", "2", "verified")) &&
            File.Exists(Path.Combine(root, "host-v2.healthy"));

        DeleteIfExists(Path.Combine(root, "host-v1.running"));
        DeleteIfExists(Path.Combine(root, "host-v2.running"));
        if (canUseNew)
        {
            AtomicWrite(Path.Combine(root, "active-version"), "v2");
            AtomicWrite(Path.Combine(root, "vault", "CURRENT"), "2");
            File.WriteAllText(Path.Combine(root, "host-v2.running"), "recovered-new");
            return new("HealthyNew", "v2", "2", CountHosts());
        }
        AtomicWrite(Path.Combine(root, "active-version"), "v1");
        AtomicWrite(Path.Combine(root, "vault", "CURRENT"), "1");
        File.WriteAllText(Path.Combine(root, "host-v1.running"), "recovered-old");
        return new("HealthyOld", "v1", "1", CountHosts());
    }

    private int CountHosts() => Directory.GetFiles(root, "host-*.running").Length;
    private static void DeleteIfExists(string path) { if (File.Exists(path)) File.Delete(path); }
    private static void AtomicWrite(string path, string content)
    {
        var next = path + ".new";
        using (var stream = new FileStream(next, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(true);
        }
        if (!Native.MoveFileEx(next, path, Native.ReplaceExisting | Native.WriteThrough))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Atomic replacement failed: {next} -> {path}");
    }

    public void Dispose() => Directory.Delete(root, true);
}

internal sealed record Journal(string Phase, string OldVersion, string NewVersion, string OldGeneration, string NewGeneration);
internal sealed record RecoveryResult(string State, string? ActiveVersion, string? VaultGeneration, int HostCount);
internal sealed class InjectedFailure : Exception;

internal static partial class Native
{
    internal const uint ReplaceExisting = 0x1;
    internal const uint WriteThrough = 0x8;
    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFileEx(string existing, string replacement, uint flags);
}
