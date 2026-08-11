using System.Collections.Concurrent;
using System.Text.Json;
using RcloneUI.Rclone;

namespace RcloneUI.Host;

internal sealed record HostMountSnapshot(Guid InstanceId, Guid RemoteId, string Subpath, string MountPoint, string VolumeName, string State, string? DiagnosticCode, DateTimeOffset UpdatedUtc);

internal interface IWindowsMountNamespace
{
    bool IsPresented(string mountPoint);
    ValueTask<bool> WaitForAsync(string mountPoint, bool presented, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class WindowsMountNamespace : IWindowsMountNamespace
{
    public bool IsPresented(string mountPoint) => Directory.Exists(Root(mountPoint));

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

internal sealed class HostMountCoordinator(IRcloneRuntime rclone, IHostRemoteResolver remotes, IWindowsMountNamespace? windowsNamespace = null)
{
    private readonly IWindowsMountNamespace windowsNamespace = windowsNamespace ?? new WindowsMountNamespace();
    private readonly ConcurrentDictionary<Guid, HostMountSnapshot> mounts = [];

    internal IReadOnlyList<HostMountSnapshot> Snapshots => [.. mounts.Values.OrderBy(value => value.UpdatedUtc)];

    internal async ValueTask<(string ResultType, HostMountSnapshot? Snapshot)> StartReadOnlyAsync(Guid remoteId, string subpath, char driveLetter, string volumeName, string capabilityBinding, CancellationToken cancellationToken)
    {
        var mountPoint = $"{char.ToUpperInvariant(driveLetter)}:";
        if (driveLetter is < 'D' or > 'Z' && driveLetter is < 'd' or > 'z') return ("mount-invalid", Failed(remoteId, subpath, mountPoint, volumeName, "drive-letter-invalid"));
        if (string.IsNullOrWhiteSpace(volumeName) || volumeName.Length > 64 || volumeName.IndexOfAny(['\r', '\n', '\0']) >= 0) return ("mount-invalid", Failed(remoteId, subpath, mountPoint, volumeName, "volume-name-invalid"));
        if (!StringComparer.Ordinal.Equals(capabilityBinding, rclone.Capabilities.Binding)) return ("mount-invalid", Failed(remoteId, subpath, mountPoint, volumeName, "capability-binding-changed"));
        if (!rclone.Capabilities.Endpoints.Contains("mount/mount") || !rclone.Capabilities.Endpoints.Contains("mount/unmount")) return ("mount-unavailable", Failed(remoteId, subpath, mountPoint, volumeName, "mount-rc-unavailable"));
        var mountType = rclone.Capabilities.MountTypes.Contains("mount") ? "mount" : rclone.Capabilities.MountTypes.Contains("cmount") ? "cmount" : null;
        if (mountType is null) return ("mount-unavailable", Failed(remoteId, subpath, mountPoint, volumeName, "winfsp-incompatible"));
        if (windowsNamespace.IsPresented(mountPoint)) return ("mount-conflict", Failed(remoteId, subpath, mountPoint, volumeName, "drive-letter-conflict"));
        var fileSystem = await remotes.ResolveFileSystemAsync(remoteId, cancellationToken).ConfigureAwait(false);
        if (fileSystem is null) return ("mount-invalid", Failed(remoteId, subpath, mountPoint, volumeName, "remote-not-found"));
        var id = Guid.NewGuid();
        try
        {
            var handle = await rclone.StartAsync(new(id, capabilityBinding, RclonePrimitive.Mount, new(fileSystem, subpath), new(string.Empty, mountPoint), $"mount/{id:N}", MountOptions: new(mountType, true, volumeName.Trim())), cancellationToken).ConfigureAwait(false);
            var result = await rclone.WaitAsync(handle, cancellationToken).ConfigureAwait(false);
            if (!result.Success) return ("mount-not-started", Failed(remoteId, subpath, mountPoint, volumeName, result.ErrorCode ?? "mount-rc-failed"));
            mountPoint = ReadMountPoint(result.Body) ?? mountPoint;
            if (!await windowsNamespace.WaitForAsync(mountPoint, true, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
            {
                await TryUnmountAsync(mountPoint, capabilityBinding, cancellationToken).ConfigureAwait(false);
                return ("mount-not-ready", Failed(remoteId, subpath, mountPoint, volumeName, "mount-namespace-not-presented"));
            }
            var snapshot = new HostMountSnapshot(id, remoteId, subpath, mountPoint, volumeName.Trim(), "ready", null, DateTimeOffset.UtcNow);
            mounts[id] = snapshot;
            return ("mount-ready", snapshot);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ("mount-not-started", Failed(remoteId, subpath, mountPoint, volumeName, exception.GetType().Name.ToLowerInvariant()));
        }
    }

    internal async ValueTask<(string ResultType, HostMountSnapshot? Snapshot)> StopAsync(Guid instanceId, string capabilityBinding, CancellationToken cancellationToken)
    {
        if (!mounts.TryGetValue(instanceId, out var current)) return ("mount-not-found", null);
        if (!StringComparer.Ordinal.Equals(capabilityBinding, rclone.Capabilities.Binding)) return ("mount-unmount-failed", current with { DiagnosticCode = "capability-binding-changed" });
        if (!await TryUnmountAsync(current.MountPoint, capabilityBinding, cancellationToken).ConfigureAwait(false)) return ("mount-unmount-failed", current with { DiagnosticCode = "unmount-rc-failed", UpdatedUtc = DateTimeOffset.UtcNow });
        if (!await windowsNamespace.WaitForAsync(current.MountPoint, false, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false)) return ("mount-unmount-failed", current with { DiagnosticCode = "mount-namespace-still-present", UpdatedUtc = DateTimeOffset.UtcNow });
        mounts.TryRemove(instanceId, out _);
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
    private static HostMountSnapshot Failed(Guid remoteId, string subpath, string mountPoint, string volumeName, string code) => new(Guid.Empty, remoteId, subpath, mountPoint, volumeName, "failed", code, DateTimeOffset.UtcNow);
}
