using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RcloneUI.Contracts.HostProtocol.V1;
using RcloneUI.DataRoot;
using RcloneUI.Mounts;
using RcloneUI.Rclone;

namespace RcloneUI.Host;

internal sealed record HostCommandResult(string ResultType, JsonElement Body, StateCursor State, bool StateChanged = false);
internal sealed record HostBrowseItem(string Path, bool IsDirectory, long? Size);

internal sealed class HostStateAuthority : IDisposable
{
    private const int MaximumBrowseItems = 2_000;
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private readonly DurableIdempotencyStore idempotency;
    private readonly IRcloneRuntime? rclone;
    private readonly IHostRemoteProjection? remotes;
    private readonly LibArgon2Binding? argon2;
    private readonly HostMountCoordinator? mounts;
    private readonly IWinFspDetector winFsp;
    private readonly SemaphoreSlim dispatchGate = new(1, 1);
    private readonly Dictionary<Guid, CopyRunState> copyRuns = [];
    private readonly StateEpoch epoch = new(Guid.NewGuid().ToString("N"));
    private ulong revision;
    private int activationCount;
    internal event Action? ShutdownRequested;

    internal HostStateAuthority(string dataRootPath, IRcloneRuntime? rclone = null, IHostRemoteProjection? remotes = null, LibArgon2Binding? argon2 = null, IWinFspDetector? winFsp = null)
    {
        this.rclone = rclone;
        this.remotes = remotes;
        this.argon2 = argon2;
        this.winFsp = winFsp ?? new WindowsWinFspDetector();
        if (rclone is not null && remotes is IHostRemoteResolver resolver) mounts = new(rclone, resolver, winFspDetector: this.winFsp, journal: new HostMountLifecycleJournal(dataRootPath));
        idempotency = new(Path.Combine(dataRootPath, "runtime", "idempotency.json"));
        foreach (var record in idempotency.Records)
        {
            revision = Math.Max(revision, record.Revision);
            if (record.ResultType != "activated") continue;
            using var body = JsonDocument.Parse(record.ResultBody);
            if (body.RootElement.TryGetProperty("activationCount", out var count))
                activationCount = Math.Max(activationCount, count.GetInt32());
        }
    }

    internal StateCursor Cursor
    {
        get
        {
            lock (sync) return new(epoch, revision);
        }
    }

    internal async ValueTask<HostCommandResult> DispatchAsync(ProtocolEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (envelope.Request.IsExpired(DateTimeOffset.UtcNow))
            return CreateResult("deadline-expired", new { }, Cursor);
        var commandType = ReadCommandType(envelope.Body);
        var semanticHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Body.GetRawText())));
        await dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                var prior = idempotency.Find(envelope.Request.IdempotencyKey.Value);
                if (prior is not null)
                {
                    if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(prior.SemanticHash), Convert.FromHexString(semanticHash)))
                        return CreateResult("idempotency-conflict", new { }, new(epoch, revision));
                    using var priorBody = JsonDocument.Parse(prior.ResultBody);
                    return new(prior.ResultType, priorBody.RootElement.Clone(), new(epoch, prior.Revision));
                }
            }

            HostCommandResult result;
            if (commandType == "get-snapshot")
            {
                IReadOnlyList<HostRemoteSummary> summaries = [];
                IReadOnlyList<SavedMountProfile> mountProfiles = [];
                if (remotes is not null && remotes.SessionState == "operational")
                {
                    try { summaries = await remotes.ListAsync(cancellationToken).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is not OperationCanceledException) { return CreateResult("snapshot-unavailable", new { code = "remote-projection-unavailable" }, Cursor); }
                    if (remotes is IHostMountProfileManager profileManager)
                    {
                        try { mountProfiles = await profileManager.ListMountProfilesAsync(cancellationToken).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is not OperationCanceledException) { return CreateResult("snapshot-unavailable", new { code = "mount-profile-projection-unavailable" }, Cursor); }
                    }
                }
                var winFspStatus = winFsp.Inspect();
                var rcloneMountAvailable = rclone is not null && rclone.Capabilities.Endpoints.Contains("mount/mount") && rclone.Capabilities.Endpoints.Contains("mount/unmount") && (rclone.Capabilities.MountTypes.Contains("mount") || rclone.Capabilities.MountTypes.Contains("cmount"));
                var mountSnapshots = mounts?.Snapshots ?? [];
                var mountVfs = new List<object>();
                if (mounts is not null && rclone is not null)
                {
                    foreach (var mount in mountSnapshots.Where(item => item.State == "ready" && item.RequiresVfsDrain))
                    {
                        var (observationType, status) = await mounts.ObserveVfsAsync(mount.InstanceId, rclone.Capabilities.Binding, cancellationToken).ConfigureAwait(false);
                        mountVfs.Add(new { mount.InstanceId, available = observationType == "mount-vfs-observed" && status is not null, bytesUsed = status?.BytesUsed, erroredFiles = status?.ErroredFiles, uploadsInProgress = status?.UploadsInProgress, uploadsQueued = status?.UploadsQueued, outOfSpace = status?.OutOfSpace, queueItems = status?.QueueItems, observedUtc = status?.ObservedUtc });
                    }
                }
                lock (sync) result = CreateResult("snapshot", new { session = remotes?.SessionState ?? "locked", activationCount, remotes = summaries, mountProfiles, copyRuns = copyRuns.Values.OrderBy(x => x.CreatedUtc).ToArray(), mounts = mountSnapshots, mountVfs, rclone = new { status = rclone is null ? "unavailable" : "ready", version = rclone?.Capabilities.Binary.Version, capabilityBinding = rclone?.Capabilities.Binding, mountAvailable = rcloneMountAvailable }, winFsp = winFspStatus, vault = new { kdfStatus = argon2 is null ? "unavailable" : "ready" } }, new(epoch, revision));
            }
            else if (commandType == "activate-ui")
            {
                lock (sync) { activationCount++; revision = checked(revision + 1); result = CreateResult("activated", new { activationCount }, new(epoch, revision), stateChanged: true); }
            }
            else if (commandType == "unlock-vault")
            {
                result = await UnlockVaultAsync(envelope.Body, cancellationToken).ConfigureAwait(false);
            }
            else if (commandType == "lock-vault")
            {
                result = await LockVaultAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (commandType == "shutdown-host")
            {
                if (mounts?.Snapshots.Any(snapshot => snapshot.State == "ready") == true) result = CreateResult("shutdown-blocked-active-mount", new { }, Cursor);
                else { result = CreateResult("shutdown-accepted", new { }, Cursor); ShutdownRequested?.Invoke(); }
            }
            else if (commandType == "start-copy")
            {
                result = await StartCopyAsync(envelope.Body, cancellationToken).ConfigureAwait(false);
            }
            else if (commandType == "browse-remote")
            {
                result = await BrowseRemoteAsync(envelope.Body, cancellationToken).ConfigureAwait(false);
            }
            else if (commandType == "create-remote-folder")
            {
                result = await CreateRemoteFolderAsync(envelope.Body, cancellationToken).ConfigureAwait(false);
            }
            else if (commandType == "start-read-only-mount")
            {
                result = await StartMountAsync(envelope.Body, cancellationToken).ConfigureAwait(false);
            }
            else if (commandType == "start-mount-profile")
            {
                result = await StartMountProfileAsync(envelope.Body, cancellationToken).ConfigureAwait(false);
            }
            else if (commandType == "save-mount-profile")
            {
                result = await SaveMountProfileAsync(envelope.Body, cancellationToken).ConfigureAwait(false);
            }
            else if (commandType == "delete-mount-profile")
            {
                result = await DeleteMountProfileAsync(envelope.Body, cancellationToken).ConfigureAwait(false);
            }
            else if (commandType == "stop-mount")
            {
                result = await StopMountAsync(envelope.Body, cancellationToken).ConfigureAwait(false);
            }
            else if (commandType == "add-token-remote")
            {
                result = await AddTokenRemoteAsync(envelope.Body, cancellationToken).ConfigureAwait(false);
            }
            else if (commandType == "add-connection-remote")
            {
                result = await AddConnectionRemoteAsync(envelope.Body, cancellationToken).ConfigureAwait(false);
            }
            else if (commandType == "delete-remote")
            {
                result = await DeleteRemoteAsync(envelope.Body, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                lock (sync) result = CreateResult("unknown-command", new { }, new(epoch, revision));
            }

            if (commandType is not ("get-snapshot" or "unlock-vault" or "lock-vault" or "add-token-remote" or "add-connection-remote"))
                lock (sync) idempotency.Record(new(envelope.Request.IdempotencyKey.Value, semanticHash, result.ResultType, result.Body.GetRawText(), result.State.Revision));
            return result;
        }
        finally { dispatchGate.Release(); }
    }

    private async ValueTask<HostCommandResult> StartCopyAsync(JsonElement body, CancellationToken cancellationToken)
    {
        if (remotes?.SessionState != "operational") return CreateResult("vault-locked", new { }, Cursor);
        if (rclone is null) return CreateResult("rclone-unavailable", new { recoveryAction = "Install or repair the managed rclone component." }, Cursor);
        if (!body.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object) return CreateResult("copy-invalid", new { code = "arguments-missing" }, Cursor);
        if (remotes is not IHostRemoteResolver resolver) return CreateResult("copy-invalid", new { code = "remote-resolver-unavailable" }, Cursor);
        var sourceRemoteId = ReadGuidArgument(arguments, "sourceRemoteId"); var sourcePath = ReadPathArgument(arguments, "sourcePath");
        var sourceLocalPath = ReadArgument(arguments, "sourceLocalPath", 32767);
        var destinationRemoteId = ReadGuidArgument(arguments, "destinationRemoteId"); var destinationPath = ReadArgument(arguments, "destinationPath");
        var destinationLocalPath = ReadArgument(arguments, "destinationLocalPath", 32767);
        var maximumTransferBytes = ReadOptionalPositiveInt64(arguments, "maximumTransferBytes", 1_099_511_627_776L);
        var maximumDurationMinutes = ReadOptionalPositiveInt64(arguments, "maximumDurationMinutes", 10_080);
        var binding = ReadArgument(arguments, "capabilityBinding");
        var localDownload = destinationLocalPath is not null;
        var localUpload = sourceLocalPath is not null;
        if (binding is null || maximumTransferBytes.Invalid || maximumDurationMinutes.Invalid || localDownload && localUpload || (localDownload ? sourceRemoteId is null || sourcePath is null || destinationRemoteId is not null || destinationPath is not null : localUpload ? destinationRemoteId is null || destinationPath is null || sourceRemoteId is not null || sourcePath is not null : sourceRemoteId is null || sourcePath is null || destinationRemoteId is null || destinationPath is null)) return CreateResult("copy-invalid", new { code = "arguments-invalid" }, Cursor);
        string? sourceFs; string? destinationFs;
        try
        {
            if (localUpload)
            {
                if (!Path.IsPathFullyQualified(sourceLocalPath!) || !Directory.Exists(Path.GetFullPath(sourceLocalPath!))) return CreateResult("copy-invalid", new { code = "local-directory-not-found" }, Cursor);
                sourceFs = Path.GetFullPath(sourceLocalPath!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/') + "/";
                sourcePath = string.Empty;
            }
            else sourceFs = await resolver.ResolveFileSystemAsync(sourceRemoteId!.Value, cancellationToken).ConfigureAwait(false);
            if (localDownload)
            {
                if (!Path.IsPathFullyQualified(destinationLocalPath!)) return CreateResult("copy-invalid", new { code = "local-path-not-absolute" }, Cursor);
                var fullPath = Path.GetFullPath(destinationLocalPath!);
                if (!Directory.Exists(fullPath)) return CreateResult("copy-invalid", new { code = "local-directory-not-found" }, Cursor);
                destinationFs = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/') + "/";
                destinationPath = string.Empty;
            }
            else destinationFs = await resolver.ResolveFileSystemAsync(destinationRemoteId!.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { return CreateResult("copy-invalid", new { code = "remote-configuration-invalid" }, Cursor); }
        if (sourceFs is null || destinationFs is null) return CreateResult("copy-invalid", new { code = "remote-not-found" }, Cursor);
        var id = Guid.NewGuid();
        RcloneExecutionHandle handle;
        try
        {
            handle = await rclone.StartAsync(new(id, binding, RclonePrimitive.Copy, new(sourceFs, sourcePath ?? string.Empty), new(destinationFs, destinationPath!), $"copy/{id:N}", MaximumTransferBytes: maximumTransferBytes.Value, MaximumDuration: maximumDurationMinutes.Value is { } minutes ? TimeSpan.FromMinutes(minutes) : null), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CreateResult("copy-not-started", new { code = exception.GetType().Name.ToLowerInvariant() }, Cursor);
        }
        CopyRunState state;
        lock (sync)
        {
            revision = checked(revision + 1);
            state = new(id, "running", 0, 0, 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            copyRuns.Add(id, state);
        }
        _ = ObserveCopyAsync(handle);
        return CreateResult("copy-accepted", new { runId = id }, Cursor, stateChanged: true);
    }

    private async ValueTask<HostCommandResult> BrowseRemoteAsync(JsonElement body, CancellationToken cancellationToken)
    {
        if (remotes?.SessionState != "operational") return CreateResult("vault-locked", new { }, Cursor);
        if (rclone is null || remotes is not IHostRemoteResolver resolver) return CreateResult("browse-unavailable", new { }, Cursor);
        if (!body.TryGetProperty("arguments", out var arguments) || ReadGuidArgument(arguments, "remoteId") is not { } remoteId || ReadPathArgument(arguments, "path") is not { } path || ReadArgument(arguments, "capabilityBinding") is not { } binding) return CreateResult("browse-invalid", new { code = "arguments-invalid" }, Cursor);
        string? fileSystem;
        try { fileSystem = await resolver.ResolveFileSystemAsync(remoteId, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException) { return CreateResult("browse-invalid", new { code = "remote-configuration-invalid" }, Cursor); }
        if (fileSystem is null) return CreateResult("browse-invalid", new { code = "remote-not-found" }, Cursor);
        try
        {
            var id = Guid.NewGuid();
            var handle = await rclone.StartAsync(new(id, binding, RclonePrimitive.List, new(fileSystem, path), null, $"browse/{id:N}"), cancellationToken).ConfigureAwait(false);
            var listed = await rclone.WaitAsync(handle, cancellationToken).ConfigureAwait(false);
            if (!listed.Success) return CreateResult("browse-failed", new { code = listed.ErrorCode ?? "rclone-list-failed" }, Cursor);
            var items = ProjectBrowseItems(listed.Body, out var truncated);
            return CreateResult("browse-completed", new { items, truncated }, Cursor);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { return CreateResult("browse-failed", new { code = exception.GetType().Name.ToLowerInvariant() }, Cursor); }
    }

    private async ValueTask<HostCommandResult> CreateRemoteFolderAsync(JsonElement body, CancellationToken cancellationToken)
    {
        if (remotes?.SessionState != "operational") return CreateResult("vault-locked", new { }, Cursor);
        if (rclone is null || remotes is not IHostRemoteResolver resolver) return CreateResult("folder-create-unavailable", new { }, Cursor);
        if (!body.TryGetProperty("arguments", out var arguments) || ReadGuidArgument(arguments, "remoteId") is not { } remoteId || ReadPathArgument(arguments, "path") is not { } path || ReadArgument(arguments, "name", 255) is not { } name || ReadArgument(arguments, "capabilityBinding") is not { } binding) return CreateResult("folder-create-invalid", new { code = "arguments-invalid" }, Cursor);
        if (!IsNewFolderNameValid(name)) return CreateResult("folder-create-invalid", new { code = "name-invalid" }, Cursor);
        string? fileSystem;
        try { fileSystem = await resolver.ResolveFileSystemAsync(remoteId, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException) { return CreateResult("folder-create-invalid", new { code = "remote-configuration-invalid" }, Cursor); }
        if (fileSystem is null) return CreateResult("folder-create-invalid", new { code = "remote-not-found" }, Cursor);
        var remotePath = string.Join('/', new[] { path.Trim('/'), name.Trim() }.Where(segment => segment.Length > 0));
        try
        {
            var id = Guid.NewGuid();
            var handle = await rclone.StartAsync(new(id, binding, RclonePrimitive.MakeDirectory, new(fileSystem, remotePath), null, $"mkdir/{id:N}"), cancellationToken).ConfigureAwait(false);
            var made = await rclone.WaitAsync(handle, cancellationToken).ConfigureAwait(false);
            return made.Success ? CreateResult("folder-created", new { }, Cursor) : CreateResult("folder-create-failed", new { code = made.ErrorCode ?? "rclone-mkdir-failed" }, Cursor);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { return CreateResult("folder-create-failed", new { code = exception.GetType().Name.ToLowerInvariant() }, Cursor); }
    }

    private static bool IsNewFolderNameValid(string name) => name == name.Trim() && name.Length is > 0 and <= 255 && name is not "." and not ".." && name.IndexOfAny(['/', '\\', '\0']) < 0;

    private static List<HostBrowseItem> ProjectBrowseItems(JsonElement response, out bool truncated)
    {
        var output = response.TryGetProperty("output", out var nestedOutput) ? nestedOutput : response;
        var list = output.TryGetProperty("list", out var nestedList) ? nestedList : output;
        if (list.ValueKind != JsonValueKind.Array) { truncated = false; return []; }

        var items = new List<HostBrowseItem>();
        truncated = false;
        foreach (var entry in list.EnumerateArray())
        {
            if (items.Count == MaximumBrowseItems) { truncated = true; break; }
            var path = ReadBrowseString(entry, "Path") ?? ReadBrowseString(entry, "path") ?? ReadBrowseString(entry, "Name") ?? ReadBrowseString(entry, "name");
            if (string.IsNullOrWhiteSpace(path)) continue;
            var isDirectory = ReadBrowseBoolean(entry, "IsDir") || ReadBrowseBoolean(entry, "isDir");
            var size = ReadBrowseInt64(entry, "Size") ?? ReadBrowseInt64(entry, "size");
            items.Add(new(path, isDirectory, size));
        }
        return items;
    }

    private static string? ReadBrowseString(JsonElement entry, string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool ReadBrowseBoolean(JsonElement entry, string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static long? ReadBrowseInt64(JsonElement entry, string name) => entry.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;

    private async ValueTask<HostCommandResult> StartMountAsync(JsonElement body, CancellationToken cancellationToken)
    {
        if (remotes?.SessionState != "operational") return CreateResult("vault-locked", new { }, Cursor);
        if (mounts is null) return CreateResult("mount-unavailable", new { code = "mount-engine-unavailable" }, Cursor);
        if (!body.TryGetProperty("arguments", out var arguments)
            || ReadGuidArgument(arguments, "remoteId") is not { } remoteId
            || ReadPathArgument(arguments, "subpath") is not { } subpath
            || ReadArgument(arguments, "presentationMode") is not { } presentationValue
            || ReadArgument(arguments, "driveSelection") is not { } driveSelectionValue
            || ReadArgument(arguments, "driveLetter", 1) is not { Length: 1 } driveLetter
            || ReadArgument(arguments, "volumeName", 64) is not { } volumeName
            || ReadArgument(arguments, "capabilityBinding") is not { } binding)
            return CreateResult("mount-invalid", new { code = "arguments-invalid" }, Cursor);
        var presentationMode = presentationValue switch { "network-drive" => MountPresentationMode.NetworkDrive, "fixed-drive" => MountPresentationMode.FixedDrive, "fixed-directory" => MountPresentationMode.FixedDirectory, _ => (MountPresentationMode)(-1) };
        var driveSelection = driveSelectionValue switch { "preferred" => DriveLetterSelection.Preferred, "automatic" => DriveLetterSelection.Automatic, _ => (DriveLetterSelection)(-1) };
        var fixedDirectoryPath = ReadArgument(arguments, "fixedDirectoryPath", 32767);
        var (resultType, snapshot) = await mounts.StartReadOnlyAsync(remoteId, subpath, presentationMode, driveSelection, driveLetter[0], fixedDirectoryPath, volumeName, binding, cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            if (resultType == "mount-ready") revision = checked(revision + 1);
            return CreateResult(resultType, (object?)snapshot ?? new { }, new(epoch, revision), resultType == "mount-ready");
        }
    }

    private async ValueTask<HostCommandResult> StopMountAsync(JsonElement body, CancellationToken cancellationToken)
    {
        if (mounts is null) return CreateResult("mount-unavailable", new { code = "mount-engine-unavailable" }, Cursor);
        if (!body.TryGetProperty("arguments", out var arguments)
            || ReadGuidArgument(arguments, "instanceId") is not { } instanceId
            || ReadArgument(arguments, "capabilityBinding") is not { } binding)
            return CreateResult("mount-invalid", new { code = "arguments-invalid" }, Cursor);
        var (resultType, snapshot) = await mounts.StopAsync(instanceId, binding, cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            if (resultType == "mount-stopped") revision = checked(revision + 1);
            return CreateResult(resultType, (object?)snapshot ?? new { }, new(epoch, revision), resultType == "mount-stopped");
        }
    }

    private async ValueTask<HostCommandResult> StartMountProfileAsync(JsonElement body, CancellationToken cancellationToken)
    {
        if (remotes?.SessionState != "operational") return CreateResult("vault-locked", new { }, Cursor);
        if (mounts is null || remotes is not IHostMountProfileManager profiles) return CreateResult("mount-unavailable", new { code = "mount-profile-engine-unavailable" }, Cursor);
        if (!body.TryGetProperty("arguments", out var arguments) || ReadGuidArgument(arguments, "profileId") is not { } profileId || ReadArgument(arguments, "capabilityBinding") is not { } binding) return CreateResult("mount-invalid", new { code = "arguments-invalid" }, Cursor);
        var profile = await profiles.ReadMountProfileAsync(new(profileId), cancellationToken).ConfigureAwait(false);
        if (profile is null) return CreateResult("mount-profile-not-found", new { }, Cursor);
        var (resultType, snapshot) = profile.CachePreset == MountCachePreset.StandardReadWrite
            ? await mounts.StartReadWriteAsync(profile.RemoteId, profile.Subpath, profile.PresentationMode, profile.DriveLetterSelection, profile.PreferredDriveLetter, profile.FixedDirectoryPath, profile.VolumeName, binding, cancellationToken, profile.Id.Value).ConfigureAwait(false)
            : await mounts.StartReadOnlyAsync(profile.RemoteId, profile.Subpath, profile.PresentationMode, profile.DriveLetterSelection, profile.PreferredDriveLetter, profile.FixedDirectoryPath, profile.VolumeName, binding, cancellationToken, profile.Id.Value).ConfigureAwait(false);
        lock (sync)
        {
            if (resultType == "mount-ready") revision = checked(revision + 1);
            return CreateResult(resultType, (object?)snapshot ?? new { }, new(epoch, revision), resultType == "mount-ready");
        }
    }

    private async ValueTask<HostCommandResult> SaveMountProfileAsync(JsonElement body, CancellationToken cancellationToken)
    {
        if (remotes is not IHostMountProfileManager profiles) return CreateResult("mount-profile-unavailable", new { }, Cursor);
        if (!body.TryGetProperty("arguments", out var arguments)
            || ReadGuidArgument(arguments, "profileId") is not { } profileId
            || ReadArgument(arguments, "displayName", 80) is not { } displayName
            || ReadGuidArgument(arguments, "remoteId") is not { } remoteId
            || ReadPathArgument(arguments, "subpath") is not { } subpath
            || ReadArgument(arguments, "presentationMode") is not { } presentationValue
            || ReadArgument(arguments, "driveSelection") is not { } driveSelectionValue
            || ReadArgument(arguments, "cachePreset") is not { } cachePresetValue
            || ReadArgument(arguments, "driveLetter", 1) is not { Length: 1 } driveLetter
            || ReadArgument(arguments, "volumeName", 64) is not { } volumeName
            || !arguments.TryGetProperty("expectedRevision", out var revisionValue) || !revisionValue.TryGetUInt64(out var expectedRevision))
            return CreateResult("mount-profile-invalid", new { code = "arguments-invalid" }, Cursor);
        var presentation = presentationValue switch { "network-drive" => MountPresentationMode.NetworkDrive, "fixed-drive" => MountPresentationMode.FixedDrive, "fixed-directory" => MountPresentationMode.FixedDirectory, _ => (MountPresentationMode)(-1) };
        var driveSelection = driveSelectionValue switch { "preferred" => DriveLetterSelection.Preferred, "automatic" => DriveLetterSelection.Automatic, _ => (DriveLetterSelection)(-1) };
        var cachePreset = cachePresetValue switch { "read-only" => MountCachePreset.ReadOnlyBrowsing, "standard-read-write" => MountCachePreset.StandardReadWrite, _ => (MountCachePreset)(-1) };
        if (mounts?.Snapshots.Any(snapshot => snapshot.ProfileId == profileId) == true) return CreateResult("mount-profile-active", new { }, Cursor);
        var profile = new SavedMountProfile(new(profileId), expectedRevision, displayName, remoteId, subpath, presentation, driveSelection, char.ToUpperInvariant(driveLetter[0]), ReadArgument(arguments, "fixedDirectoryPath", 32767), volumeName, cachePreset, false);
        var (resultType, saved) = await profiles.UpsertMountProfileAsync(profile, expectedRevision, cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            if (resultType == "mount-profile-saved") revision = checked(revision + 1);
            return CreateResult(resultType, (object?)saved ?? new { }, new(epoch, revision), resultType == "mount-profile-saved");
        }
    }

    private async ValueTask<HostCommandResult> DeleteMountProfileAsync(JsonElement body, CancellationToken cancellationToken)
    {
        if (remotes is not IHostMountProfileManager profiles) return CreateResult("mount-profile-unavailable", new { }, Cursor);
        if (!body.TryGetProperty("arguments", out var arguments) || ReadGuidArgument(arguments, "profileId") is not { } profileId || !arguments.TryGetProperty("expectedRevision", out var revisionValue) || !revisionValue.TryGetUInt64(out var expectedRevision)) return CreateResult("mount-profile-invalid", new { code = "arguments-invalid" }, Cursor);
        if (mounts?.Snapshots.Any(snapshot => snapshot.ProfileId == profileId) == true) return CreateResult("mount-profile-active", new { }, Cursor);
        var resultType = await profiles.DeleteMountProfileAsync(new(profileId), expectedRevision, cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            if (resultType == "mount-profile-deleted") revision = checked(revision + 1);
            return CreateResult(resultType, new { }, new(epoch, revision), resultType == "mount-profile-deleted");
        }
    }

    private async ValueTask<HostCommandResult> UnlockVaultAsync(JsonElement body, CancellationToken cancellationToken)
    {
        if (remotes is not IHostVaultSession vault) return CreateResult("vault-unavailable", new { }, Cursor);
        if (!body.TryGetProperty("arguments", out var arguments)
            || !arguments.TryGetProperty("passwordUtf8", out var encoded)
            || encoded.ValueKind != JsonValueKind.String
            || encoded.GetString() is not { Length: > 0 and <= 2048 } value)
            return CreateResult("vault-password-invalid", new { }, Cursor);
        byte[] password;
        try { password = Convert.FromBase64String(value); }
        catch (FormatException) { return CreateResult("vault-password-invalid", new { }, Cursor); }
        try
        {
            var resultType = await vault.UnlockAsync(password, cancellationToken).ConfigureAwait(false);
            lock (sync)
            {
                if (resultType == "vault-unlocked") revision = checked(revision + 1);
                return CreateResult(resultType, new { }, new(epoch, revision), resultType == "vault-unlocked");
            }
        }
        finally { CryptographicOperations.ZeroMemory(password); }
    }

    private async ValueTask<HostCommandResult> LockVaultAsync(CancellationToken cancellationToken)
    {
        if (remotes is not IHostVaultSession vault) return CreateResult("vault-unavailable", new { }, Cursor);
        var resultType = await vault.LockAsync(cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            if (resultType == "vault-locked") revision = checked(revision + 1);
            return CreateResult(resultType, new { }, new(epoch, revision), resultType == "vault-locked");
        }
    }

    private async ValueTask<HostCommandResult> AddTokenRemoteAsync(JsonElement body, CancellationToken cancellationToken)
    {
        if (remotes is not IHostRemoteManager manager || rclone is null) return CreateResult("remote-engine-unavailable", new { }, Cursor);
        if (!body.TryGetProperty("arguments", out var arguments)
            || ReadArgument(arguments, "displayName") is not { } displayName
            || ReadArgument(arguments, "providerType") is not { } providerType
            || ReadArgument(arguments, "tokenUtf8", 24 * 1024) is not { } encoded)
            return CreateResult("remote-input-invalid", new { }, Cursor);
        byte[] tokenBytes;
        try { tokenBytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { return CreateResult("remote-input-invalid", new { }, Cursor); }
        try
        {
            var resultType = await manager.AddTokenRemoteAsync(displayName, providerType, Encoding.UTF8.GetString(tokenBytes), rclone, cancellationToken).ConfigureAwait(false);
            lock (sync)
            {
                if (resultType == "remote-added") revision = checked(revision + 1);
                return CreateResult(resultType, new { }, new(epoch, revision), resultType == "remote-added");
            }
        }
        finally { CryptographicOperations.ZeroMemory(tokenBytes); }
    }

    private async ValueTask<HostCommandResult> AddConnectionRemoteAsync(JsonElement body, CancellationToken cancellationToken)
    {
        if (remotes is not IHostRemoteManager manager || rclone is null || !body.TryGetProperty("arguments", out var arguments)
            || ReadArgument(arguments, "displayName") is not { } name || ReadArgument(arguments, "providerType") is not { } provider
            || !arguments.TryGetProperty("configuration", out var config) || config.ValueKind != JsonValueKind.Object) return CreateResult("remote-input-invalid", new { }, Cursor);
        var values = config.EnumerateObject().Where(item => item.Value.ValueKind == JsonValueKind.String).ToDictionary(item => item.Name, item => item.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
        var resultType = await manager.AddConnectionRemoteAsync(name, provider, values, rclone, cancellationToken).ConfigureAwait(false);
        lock (sync) { if (resultType == "remote-added") revision = checked(revision + 1); return CreateResult(resultType, new { }, new(epoch, revision), resultType == "remote-added"); }
    }

    private async ValueTask<HostCommandResult> DeleteRemoteAsync(JsonElement body, CancellationToken cancellationToken)
    {
        if (remotes is not IHostRemoteManager manager || remotes is not IHostMountProfileManager profiles) return CreateResult("remote-delete-unavailable", new { }, Cursor);
        if (!body.TryGetProperty("arguments", out var arguments) || ReadGuidArgument(arguments, "remoteId") is not { } remoteId || !arguments.TryGetProperty("expectedRevision", out var revisionValue) || !revisionValue.TryGetUInt64(out var expectedRevision)) return CreateResult("remote-delete-invalid", new { }, Cursor);
        if ((await profiles.ListMountProfilesAsync(cancellationToken).ConfigureAwait(false)).Any(profile => profile.RemoteId == remoteId)) return CreateResult("remote-delete-blocked-profile", new { }, Cursor);
        var resultType = await manager.DeleteRemoteAsync(remoteId, expectedRevision, cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            if (resultType == "remote-deleted") revision = checked(revision + 1);
            return CreateResult(resultType, new { }, new(epoch, revision), resultType == "remote-deleted");
        }
    }

    private async Task ObserveCopyAsync(RcloneExecutionHandle handle)
    {
        try
        {
            var stats = await rclone!.GetStatsAsync(handle, CancellationToken.None).ConfigureAwait(false);
            lock (sync) copyRuns[handle.ExecutionId] = copyRuns[handle.ExecutionId] with { Bytes = stats.Bytes, TotalBytes = stats.TotalBytes, BytesPerSecond = stats.BytesPerSecond, UpdatedUtc = DateTimeOffset.UtcNow };
            var result = await rclone.WaitAsync(handle, CancellationToken.None).ConfigureAwait(false);
            lock (sync)
            {
                revision = checked(revision + 1);
                copyRuns[handle.ExecutionId] = copyRuns[handle.ExecutionId] with { State = result.Success ? "succeeded" : result.Cancelled ? "cancelled" : "failed", ErrorCode = result.ErrorCode, UpdatedUtc = DateTimeOffset.UtcNow };
            }
        }
        catch (Exception exception)
        {
            lock (sync)
            {
                revision = checked(revision + 1);
                copyRuns[handle.ExecutionId] = copyRuns[handle.ExecutionId] with { State = "failed", ErrorCode = exception.GetType().Name.ToLowerInvariant(), UpdatedUtc = DateTimeOffset.UtcNow };
            }
        }
    }

    private static string? ReadArgument(JsonElement arguments, string name, int maximumLength = 2048)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return text is not null && text.Length > 0 && text.Length <= maximumLength ? text : null;
    }
    private static string? ReadPathArgument(JsonElement arguments, string name, int maximumLength = 2048)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return text is not null && text.Length <= maximumLength ? text : null;
    }
    private static (long? Value, bool Invalid) ReadOptionalPositiveInt64(JsonElement arguments, string name, long maximum)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return (null, false);
        return value.TryGetInt64(out var number) && number is > 0 && number <= maximum ? (number, false) : (null, true);
    }
    private static Guid? ReadGuidArgument(JsonElement arguments, string name) => arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var parsed) && parsed != Guid.Empty ? parsed : null;

    private static string ReadCommandType(JsonElement body)
    {
        if (!body.TryGetProperty("commandType", out var value) || value.ValueKind != JsonValueKind.String)
            return string.Empty;
        var commandType = value.GetString()!;
        return commandType.Length <= 64 ? commandType : string.Empty;
    }

    public void Dispose() => dispatchGate.Dispose();

    private static HostCommandResult CreateResult(string resultType, object body, StateCursor state, bool stateChanged = false)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(body, WireJson));
        return new(resultType, document.RootElement.Clone(), state, stateChanged);
    }
}

internal sealed record CopyRunState(Guid RunId, string State, long Bytes, long TotalBytes, double BytesPerSecond, string? ErrorCode, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);

internal sealed record IdempotencyRecord(string Key, string SemanticHash, string ResultType, string ResultBody, ulong Revision);

internal sealed class DurableIdempotencyStore
{
    private const int MaximumRecords = 4096;
    private readonly string path;
    private readonly Dictionary<string, IdempotencyRecord> records = new(StringComparer.Ordinal);

    internal DurableIdempotencyStore(string path)
    {
        this.path = path;
        if (!File.Exists(path)) return;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Idempotency store exceeds its resource limit.");
        var loaded = JsonSerializer.Deserialize<List<IdempotencyRecord>>(bytes) ?? throw new InvalidDataException("Idempotency store is invalid.");
        if (loaded.Count > MaximumRecords) throw new InvalidDataException("Idempotency store has too many records.");
        foreach (var record in loaded) records.Add(record.Key, record);
    }

    internal IdempotencyRecord? Find(string key) => records.GetValueOrDefault(key);

    internal IEnumerable<IdempotencyRecord> Records => records.Values;

    internal void Record(IdempotencyRecord record)
    {
        if (records.Count >= MaximumRecords) throw new InvalidOperationException("Idempotency retention is full.");
        records.Add(record.Key, record);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".new";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(records.Values.OrderBy(value => value.Key, StringComparer.Ordinal));
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
