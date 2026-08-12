using System.Collections.Concurrent;
using System.Text.Json;
using RcloneUI.Mounts;
using RcloneUI.Rclone;

namespace RcloneUI.Host;

internal sealed record HostMountSnapshot(Guid InstanceId, Guid? ProfileId, Guid RemoteId, string Subpath, string MountPoint, string VolumeName, MountPresentationMode PresentationMode, string State, string? DiagnosticCode, DateTimeOffset StartedUtc, DateTimeOffset UpdatedUtc, string? VfsFileSystem = null);

internal interface IWindowsMountNamespace
{
    bool IsPresented(string mountPoint);
    ValueTask<bool> WaitForAsync(string mountPoint, bool presented, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class WindowsMountNamespace : IWindowsMountNamespace
{
    public bool IsPresented(string mountPoint)
    {
        try
        {
            if (mountPoint.EndsWith(':')) return Directory.Exists(Root(mountPoint));
            return Directory.Exists(mountPoint) && File.GetAttributes(mountPoint).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public async ValueTask<bool> WaitForAsync(string mountPoint, bool presented, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPresented(mountPoint) == presented) return true;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return IsPresented(mountPoint) == presented;
    }

    private static string Root(string mountPoint) => mountPoint.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
}

internal sealed class HostMountCoordinator
{
    private readonly IRcloneRuntime rclone;
    private readonly IHostRemoteResolver remotes;
    private readonly IWindowsMountNamespace windowsNamespace;
    private readonly IWinFspDetector winFspDetector;
    private readonly IHostMountLifecycleJournal? journal;
    private readonly ConcurrentDictionary<Guid, HostMountSnapshot> mounts = [];
    private bool journalCorrupt;

    internal HostMountCoordinator(IRcloneRuntime rclone, IHostRemoteResolver remotes, IWindowsMountNamespace? windowsNamespace = null, IWinFspDetector? winFspDetector = null, IHostMountLifecycleJournal? journal = null)
    {
        this.rclone = rclone;
        this.remotes = remotes;
        this.windowsNamespace = windowsNamespace ?? new WindowsMountNamespace();
        this.winFspDetector = winFspDetector ?? new WindowsWinFspDetector();
        this.journal = journal;
        ReconcileJournal();
    }

    internal IReadOnlyList<HostMountSnapshot> Snapshots => [.. mounts.Values.OrderBy(value => value.UpdatedUtc)];

    internal async ValueTask<(string ResultType, RcloneVfsStatus? Status)> ObserveVfsAsync(Guid instanceId, string capabilityBinding, CancellationToken cancellationToken)
    {
        if (!mounts.TryGetValue(instanceId, out var current)) return ("mount-not-found", null);
        if (current.State != "ready" || string.IsNullOrWhiteSpace(current.VfsFileSystem)) return ("mount-vfs-unavailable", null);
        if (!StringComparer.Ordinal.Equals(capabilityBinding, rclone.Capabilities.Binding)) return ("mount-vfs-unavailable", null);
        try { return ("mount-vfs-observed", await rclone.GetVfsStatusAsync(current.VfsFileSystem, cancellationToken).ConfigureAwait(false)); }
        catch (Exception exception) when (exception is NotSupportedException or InvalidDataException or HttpRequestException) { return ("mount-vfs-unavailable", null); }
    }

    internal async ValueTask<(string ResultType, HostMountSnapshot? Snapshot)> StartReadOnlyAsync(Guid remoteId, string subpath, MountPresentationMode presentationMode, DriveLetterSelection driveSelection, char driveLetter, string? fixedDirectoryPath, string volumeName, string capabilityBinding, CancellationToken cancellationToken, Guid? profileId = null)
    {
        if (journalCorrupt) return ("mount-recovery-required", Failed(remoteId, subpath, string.Empty, volumeName, presentationMode, "mount-lifecycle-journal-corrupt"));
        if (profileId is not null)
        {
            var previous = mounts.Values.FirstOrDefault(value => value.ProfileId == profileId);
            if (previous is not null && previous.State == "needs-remount" && !windowsNamespace.IsPresented(previous.MountPoint))
            {
                mounts.TryRemove(previous.InstanceId, out _);
                Persist();
            }
            else if (previous is not null) return ("mount-already-active", previous);
        }
        var mountPoint = presentationMode == MountPresentationMode.FixedDirectory ? fixedDirectoryPath?.Trim() ?? string.Empty : driveSelection == DriveLetterSelection.Automatic ? "*" : $"{char.ToUpperInvariant(driveLetter)}:";
        if (!Enum.IsDefined(presentationMode) || presentationMode == MountPresentationMode.FixedDirectory && driveSelection != DriveLetterSelection.Preferred) return ("mount-invalid", Failed(remoteId, subpath, mountPoint, volumeName, presentationMode, "presentation-invalid"));
        if (presentationMode != MountPresentationMode.FixedDirectory && driveSelection == DriveLetterSelection.Preferred && driveLetter is < 'D' or > 'Z' && driveLetter is < 'd' or > 'z') return ("mount-invalid", Failed(remoteId, subpath, mountPoint, volumeName, presentationMode, "drive-letter-invalid"));
        if (presentationMode == MountPresentationMode.FixedDirectory && !ValidEmptyDirectory(mountPoint)) return ("mount-invalid", Failed(remoteId, subpath, mountPoint, volumeName, presentationMode, "fixed-directory-invalid-or-not-empty"));
        if (string.IsNullOrWhiteSpace(volumeName) || volumeName.Length > 64 || volumeName.IndexOfAny(['\r', '\n', '\0']) >= 0) return ("mount-invalid", Failed(remoteId, subpath, mountPoint, volumeName, presentationMode, "volume-name-invalid"));
        if (!StringComparer.Ordinal.Equals(capabilityBinding, rclone.Capabilities.Binding)) return ("mount-invalid", Failed(remoteId, subpath, mountPoint, volumeName, presentationMode, "capability-binding-changed"));
        var winFsp = winFspDetector.Inspect();
        if (winFsp.Status != "ready") return ("mount-unavailable", Failed(remoteId, subpath, mountPoint, volumeName, presentationMode, winFsp.DiagnosticCode));
        if (!rclone.Capabilities.Endpoints.Contains("mount/mount") || !rclone.Capabilities.Endpoints.Contains("mount/unmount")) return ("mount-unavailable", Failed(remoteId, subpath, mountPoint, volumeName, presentationMode, "mount-rc-unavailable"));
        var mountType = rclone.Capabilities.MountTypes.Contains("mount") ? "mount" : rclone.Capabilities.MountTypes.Contains("cmount") ? "cmount" : null;
        if (mountType is null) return ("mount-unavailable", Failed(remoteId, subpath, mountPoint, volumeName, presentationMode, "winfsp-incompatible"));
        if (mountPoint != "*" && windowsNamespace.IsPresented(mountPoint)) return ("mount-conflict", Failed(remoteId, subpath, mountPoint, volumeName, presentationMode, presentationMode == MountPresentationMode.FixedDirectory ? "fixed-directory-conflict" : "drive-letter-conflict"));
        var fileSystem = await remotes.ResolveFileSystemAsync(remoteId, cancellationToken).ConfigureAwait(false);
        if (fileSystem is null) return ("mount-invalid", Failed(remoteId, subpath, mountPoint, volumeName, presentationMode, "remote-not-found"));
        var id = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        try
        {
            mounts[id] = new(id, profileId, remoteId, subpath, mountPoint, volumeName.Trim(), presentationMode, "starting", null, started, started);
            Persist();
            var handle = await rclone.StartAsync(new(id, capabilityBinding, RclonePrimitive.Mount, new(fileSystem, subpath), new(string.Empty, mountPoint), $"mount/{id:N}", MountOptions: new(mountType, true, volumeName.Trim(), presentationMode == MountPresentationMode.NetworkDrive)), cancellationToken).ConfigureAwait(false);
            var result = await rclone.WaitAsync(handle, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                RemoveAndPersist(id);
                return ("mount-not-started", Failed(remoteId, subpath, mountPoint, volumeName, presentationMode, result.ErrorCode ?? "mount-rc-failed"));
            }
            var resolvedMountPoint = ReadMountPoint(result.Body);
            if (mountPoint == "*" && string.IsNullOrWhiteSpace(resolvedMountPoint))
            {
                RemoveAndPersist(id);
                return ("mount-not-ready", Failed(remoteId, subpath, mountPoint, volumeName, presentationMode, "automatic-mount-point-not-returned"));
            }
            mountPoint = resolvedMountPoint ?? mountPoint;
            if (!await windowsNamespace.WaitForAsync(mountPoint, true, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
            {
                await TryUnmountAsync(mountPoint, capabilityBinding, cancellationToken).ConfigureAwait(false);
                RemoveAndPersist(id);
                return ("mount-not-ready", Failed(remoteId, subpath, mountPoint, volumeName, presentationMode, "mount-namespace-not-presented"));
            }
            var now = DateTimeOffset.UtcNow;
            var snapshot = new HostMountSnapshot(id, profileId, remoteId, subpath, mountPoint, volumeName.Trim(), presentationMode, "ready", null, started, now, fileSystem + subpath);
            mounts[id] = snapshot;
            Persist();
            return ("mount-ready", snapshot);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            mounts.TryRemove(id, out _);
            try { Persist(); } catch (Exception journalException) when (journalException is IOException or UnauthorizedAccessException or InvalidDataException) { journalCorrupt = true; }
            if (windowsNamespace.IsPresented(mountPoint)) await TryUnmountAsync(mountPoint, capabilityBinding, cancellationToken).ConfigureAwait(false);
            return ("mount-not-started", Failed(remoteId, subpath, mountPoint, volumeName, presentationMode, exception.GetType().Name.ToLowerInvariant()));
        }
    }

    internal async ValueTask<(string ResultType, HostMountSnapshot? Snapshot)> StopAsync(Guid instanceId, string capabilityBinding, CancellationToken cancellationToken)
    {
        if (!mounts.TryGetValue(instanceId, out var current)) return ("mount-not-found", null);
        if (current.State != "ready") return ("mount-recovery-required", current);
        if (!StringComparer.Ordinal.Equals(capabilityBinding, rclone.Capabilities.Binding)) return ("mount-unmount-failed", current with { DiagnosticCode = "capability-binding-changed" });
        if (!await TryUnmountAsync(current.MountPoint, capabilityBinding, cancellationToken).ConfigureAwait(false)) return ("mount-unmount-failed", current with { DiagnosticCode = "unmount-rc-failed", UpdatedUtc = DateTimeOffset.UtcNow });
        if (!await windowsNamespace.WaitForAsync(current.MountPoint, false, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false)) return ("mount-unmount-failed", current with { DiagnosticCode = "mount-namespace-still-present", UpdatedUtc = DateTimeOffset.UtcNow });
        mounts.TryRemove(instanceId, out _);
        Persist();
        return ("mount-stopped", current with { State = "stopped", DiagnosticCode = null, UpdatedUtc = DateTimeOffset.UtcNow });
    }

    private async ValueTask<bool> TryUnmountAsync(string mountPoint, string capabilityBinding, CancellationToken cancellationToken)
    {
        try
        {
            var id = Guid.NewGuid();
            var handle = await rclone.StartAsync(new(id, capabilityBinding, RclonePrimitive.Unmount, new(string.Empty, mountPoint), null, $"unmount/{id:N}"), cancellationToken).ConfigureAwait(false);
            return (await rclone.WaitAsync(handle, cancellationToken).ConfigureAwait(false)).Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { return false; }
    }

    private static string? ReadMountPoint(JsonElement body) => body.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object && output.TryGetProperty("mountPoint", out var value) ? value.GetString() : body.TryGetProperty("mountPoint", out value) ? value.GetString() : null;
    private static bool ValidEmptyDirectory(string path)
    {
        try { return Path.IsPathFullyQualified(path) && Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private void ReconcileJournal()
    {
        if (journal is null) return;
        try
        {
            foreach (var record in journal.Read())
            {
                var presented = windowsNamespace.IsPresented(record.MountPoint);
                var state = presented ? "recovery-required" : "needs-remount";
                var code = presented ? "mount-namespace-ownership-unknown" : "mount-process-interrupted";
                mounts[record.InstanceId] = new(record.InstanceId, record.ProfileId, Guid.Empty, string.Empty, record.MountPoint, string.Empty, record.PresentationMode, state, code, record.StartedUtc, DateTimeOffset.UtcNow);
            }
            if (!mounts.IsEmpty) Persist();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            journalCorrupt = true;
            var now = DateTimeOffset.UtcNow;
            mounts[Guid.Empty] = new(Guid.Empty, null, Guid.Empty, string.Empty, string.Empty, string.Empty, MountPresentationMode.NetworkDrive, "recovery-required", "mount-lifecycle-journal-corrupt", now, now);
        }
    }

    private void Persist()
    {
        if (journal is null || journalCorrupt) return;
        journal.Write(mounts.Values.Where(value => value.InstanceId != Guid.Empty).Select(value => new HostMountLifecycleRecord(value.InstanceId, value.ProfileId, value.MountPoint, value.PresentationMode, value.State, value.StartedUtc, value.UpdatedUtc, value.DiagnosticCode)).ToArray());
    }

    private void RemoveAndPersist(Guid instanceId)
    {
        mounts.TryRemove(instanceId, out _);
        Persist();
    }

    private static HostMountSnapshot Failed(Guid remoteId, string subpath, string mountPoint, string volumeName, MountPresentationMode presentationMode, string code)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.Empty, null, remoteId, subpath, mountPoint, volumeName, presentationMode, "failed", code, now, now);
    }
}
