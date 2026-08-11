using System.Text.Json;
using System.Runtime.InteropServices;
using System.Diagnostics;

if (args is ["--try-lock", var competingLockPath])
{
    try
    {
        using var competing = new FileStream(competingLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        return 10;
    }
    catch (IOException)
    {
        return 0;
    }
}

var scratch = Directory.CreateTempSubdirectory("RcloneUI-PROTOTYPE-data-root-");
var root = Directory.CreateDirectory(Path.Combine(scratch.FullName, "DataRoot"));
var results = new List<object>();

try
{
    var canonical = Canonical(root.FullName);
    results.Add(Result("canonical-dot-alias", canonical == Canonical(Path.Combine(root.FullName, ".")), new { canonical }));
    results.Add(Result("canonical-case-alias", string.Equals(canonical, Canonical(root.FullName.ToUpperInvariant()), StringComparison.OrdinalIgnoreCase), null));

    var lockPath = Path.Combine(root.FullName, "writer.lock");
    using (var owner = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    {
        var competingRejected = false;
        try { using var competing = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { competingRejected = true; }
        results.Add(Result("live-competing-writer", competingRejected, null));

        var child = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            ArgumentList = { "--try-lock", lockPath },
            UseShellExecute = false
        })!;
        child.WaitForExit();
        results.Add(Result("cross-process-competing-writer", child.ExitCode == 0, new { child.ExitCode }));

        var aliasRejected = false;
        try { using var alias = new FileStream(Path.Combine(root.FullName, ".", "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { aliasRejected = true; }
        results.Add(Result("same-file-path-alias", aliasRejected, null));

        var deleteRejected = false;
        try { File.Delete(lockPath); }
        catch (IOException) { deleteRejected = true; }
        catch (UnauthorizedAccessException) { deleteRejected = true; }
        results.Add(Result("delete-live-lock", deleteRejected, null));
    }

    using (var recovered = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        results.Add(Result("stale-payload-reacquire", recovered.CanWrite, null));

    var outside = Directory.CreateDirectory(Path.Combine(scratch.FullName, "Outside"));
    var linkPath = Path.Combine(root.FullName, "escape-link");
    try
    {
        Directory.CreateSymbolicLink(linkPath, outside.FullName);
        var link = new DirectoryInfo(linkPath);
        var target = link.ResolveLinkTarget(true)?.FullName;
        results.Add(Result("reparse-escape-detected", target is not null && !IsBeneath(canonical, Canonical(target)), new { target }));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
    {
        results.Add(Result("reparse-escape-detected", false, new { unsupported = ex.GetType().Name }));
    }

    var original = Path.Combine(root.FullName, "original.bin");
    var hardLink = Path.Combine(root.FullName, "hard-link.bin");
    File.WriteAllText(original, "prototype");
    try
    {
        if (!NativeMethods.CreateHardLink(hardLink, original, IntPtr.Zero))
            throw new IOException($"CreateHardLinkW failed: {Marshal.GetLastWin32Error()}");
        File.AppendAllText(hardLink, "-changed");
        results.Add(Result("hard-link-alias-demonstrated", File.ReadAllText(original).EndsWith("-changed"), null));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
    {
        results.Add(Result("hard-link-alias-demonstrated", false, new { unsupported = ex.GetType().Name }));
    }

    foreach (var crashPoint in new[] { "before-selector-write", "after-new-flush", "after-selector-replace" })
    {
        var scenario = Directory.CreateDirectory(Path.Combine(root.FullName, crashPoint));
        Directory.CreateDirectory(Path.Combine(scenario.FullName, "generations", "1"));
        Directory.CreateDirectory(Path.Combine(scenario.FullName, "generations", "2"));
        File.WriteAllText(Path.Combine(scenario.FullName, "generations", "1", "manifest.ok"), "old-valid");
        File.WriteAllText(Path.Combine(scenario.FullName, "generations", "2", "manifest.ok"), "new-valid");
        var current = Path.Combine(scenario.FullName, "CURRENT");
        File.WriteAllText(current, "1\n");
        var next = Path.Combine(scenario.FullName, "CURRENT.new");
        if (crashPoint != "before-selector-write")
        {
            using var stream = new FileStream(next, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write("2\n");
            writer.Flush();
            stream.Flush(true);
        }
        if (crashPoint == "after-selector-replace") File.Move(next, current, true);
        var selected = File.ReadAllText(current).Trim();
        var valid = File.Exists(Path.Combine(scenario.FullName, "generations", selected, "manifest.ok"));
        results.Add(Result($"generation-{crashPoint}", valid && selected == (crashPoint == "after-selector-replace" ? "2" : "1"), new { selected }));
    }

    var drive = new DriveInfo(Path.GetPathRoot(root.FullName)!);
    var report = new
    {
        prototype = true,
        scratch = root.FullName,
        os = Environment.OSVersion.VersionString,
        fileSystem = drive.DriveFormat,
        driveType = drive.DriveType.ToString(),
        tests = results
    };
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return results.All(x => (bool)x.GetType().GetProperty("pass")!.GetValue(x)!) ? 0 : 2;
}
finally
{
    scratch.Delete(true);
}

static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

static bool IsBeneath(string root, string candidate) =>
    candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

static object Result(string test, bool pass, object? evidence) => new { test, pass, evidence };

static partial class NativeMethods
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);
}
