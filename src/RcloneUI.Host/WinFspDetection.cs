using System.Diagnostics;
using Microsoft.Win32;

namespace RcloneUI.Host;

internal sealed record WinFspSnapshot(string Status, string? Version, string DiagnosticCode);

internal interface IWinFspDetector
{
    WinFspSnapshot Inspect();
}

internal sealed record WinFspEvidence(bool RegistryFound, bool InstallDirectoryExists, bool SideBySideDirectoryExists, bool LibraryExists, bool DriverExists, string? Version, string? FailureCode = null);

internal interface IWinFspEvidenceSource
{
    WinFspEvidence Read();
}

internal sealed class WindowsWinFspDetector(IWinFspEvidenceSource? source = null) : IWinFspDetector
{
    private readonly IWinFspEvidenceSource source = source ?? new RegistryWinFspEvidenceSource();

    public WinFspSnapshot Inspect()
    {
        var evidence = source.Read();
        if (evidence.FailureCode is not null) return new("unavailable", null, evidence.FailureCode);
        if (!evidence.RegistryFound) return new("missing", null, "winfsp-not-installed");
        if (!evidence.InstallDirectoryExists || !evidence.SideBySideDirectoryExists) return new("incomplete", null, "winfsp-registry-incomplete");
        if (!evidence.LibraryExists || !evidence.DriverExists) return new("incomplete", null, "winfsp-x64-files-missing");
        if (string.IsNullOrWhiteSpace(evidence.Version)) return new("incomplete", null, "winfsp-version-unavailable");
        return new("ready", evidence.Version, "winfsp-ready");
    }
}

internal sealed class RegistryWinFspEvidenceSource : IWinFspEvidenceSource
{
    public WinFspEvidence Read()
    {
        if (!OperatingSystem.IsWindows()) return new(false, false, false, false, false, null, "windows-required");
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\WinFsp", writable: false);
            if (key is null) return new(false, false, false, false, false, null);
            var installDirectory = key.GetValue("InstallDir") as string;
            var sideBySideDirectory = key.GetValue("SxsDir") as string;
            var installExists = ValidAbsoluteDirectory(installDirectory);
            var sideBySideExists = ValidAbsoluteDirectory(sideBySideDirectory);
            var library = sideBySideExists ? Path.Combine(sideBySideDirectory!, "bin", "winfsp-x64.dll") : string.Empty;
            var driver = sideBySideExists ? Path.Combine(sideBySideDirectory!, "bin", "winfsp-x64.sys") : string.Empty;
            var libraryExists = File.Exists(library); var driverExists = File.Exists(driver);
            var version = libraryExists ? FileVersionInfo.GetVersionInfo(library).FileVersion : null;
            return new(true, installExists, sideBySideExists, libraryExists, driverExists, version);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException or ArgumentException)
        {
            return new(false, false, false, false, false, null, "winfsp-detection-failed");
        }
    }

    private static bool ValidAbsoluteDirectory(string? value) => !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) && Directory.Exists(value);
}
