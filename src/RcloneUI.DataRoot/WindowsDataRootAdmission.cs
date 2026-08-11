using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RcloneUI.DataRoot;

internal sealed partial class WindowsDataRootAdmission : IDisposable
{
    private readonly SafeFileHandle rootHandle;
    private readonly FileStream writerLease;

    private WindowsDataRootAdmission(
        string canonicalPath,
        RootIdentity identity,
        SafeFileHandle rootHandle,
        FileStream writerLease)
    {
        CanonicalPath = canonicalPath;
        Identity = identity;
        this.rootHandle = rootHandle;
        this.writerLease = writerLease;
    }

    internal string CanonicalPath { get; }
    internal RootIdentity Identity { get; }

    internal static WindowsDataRootAdmission Acquire(string requestedPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new DataRootAdmissionException(DataRootOpenStatus.UnsupportedLocation, "windows-local-volume-required");
        }

        var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath));
        if (new Uri(canonicalPath).IsUnc)
        {
            throw new DataRootAdmissionException(DataRootOpenStatus.UnsupportedLocation, "network-root-rejected");
        }

        Directory.CreateDirectory(canonicalPath);
        RejectReparseTraversal(canonicalPath);
        var drive = new DriveInfo(Path.GetPathRoot(canonicalPath)!);
        if (drive.DriveType is DriveType.Network or DriveType.CDRom || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new DataRootAdmissionException(DataRootOpenStatus.UnsupportedLocation, "ntfs-write-mode-required");
        }

        var rootHandle = NativeMethods.CreateFile(
            canonicalPath,
            0,
            FileShare.ReadWrite | FileShare.Delete,
            0,
            FileMode.Open,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            0);
        if (rootHandle.IsInvalid)
        {
            rootHandle.Dispose();
            throw new DataRootAdmissionException(DataRootOpenStatus.Unavailable, "root-handle-open-failed");
        }

        try
        {
            if (!NativeMethods.GetFileInformationByHandle(rootHandle, out var information))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var identity = new RootIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
            var vaultDirectory = Path.Combine(canonicalPath, "vault");
            Directory.CreateDirectory(vaultDirectory);
            RejectReparseTraversal(vaultDirectory);
            var leasePath = Path.Combine(vaultDirectory, "writer.lock");
            FileStream writerLease;
            try
            {
                writerLease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception)
            {
                throw new DataRootAdmissionException(DataRootOpenStatus.AlreadyOwned, "writer-lease-held", exception);
            }

            return new(canonicalPath, identity, rootHandle, writerLease);
        }
        catch
        {
            rootHandle.Dispose();
            throw;
        }
    }

    internal void VerifyIdentity()
    {
        if (!NativeMethods.GetFileInformationByHandle(rootHandle, out var information))
        {
            throw new DataRootAdmissionException(DataRootOpenStatus.Unavailable, "root-identity-unavailable");
        }

        var observed = new RootIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        if (observed != Identity)
        {
            throw new DataRootAdmissionException(DataRootOpenStatus.Unavailable, "root-identity-changed");
        }
    }

    public void Dispose()
    {
        writerLease.Dispose();
        rootHandle.Dispose();
    }

    private static void RejectReparseTraversal(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new DataRootAdmissionException(DataRootOpenStatus.UnsupportedLocation, "reparse-root-rejected");
            }

            current = current.Parent;
        }
    }

    internal readonly record struct RootIdentity(uint VolumeSerialNumber, ulong DirectoryFileId);

    private static partial class NativeMethods
    {
        internal const uint FileFlagBackupSemantics = 0x02000000;
        internal const uint FileFlagOpenReparsePoint = 0x00200000;

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            nint securityAttributes,
            FileMode creationDisposition,
            uint flagsAndAttributes,
            nint templateFile);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }
}

internal sealed class DataRootAdmissionException : Exception
{
    internal DataRootAdmissionException(DataRootOpenStatus status, string code, Exception? innerException = null)
        : base(code, innerException)
    {
        Status = status;
        Code = code;
    }

    internal DataRootOpenStatus Status { get; }
    internal string Code { get; }
}
