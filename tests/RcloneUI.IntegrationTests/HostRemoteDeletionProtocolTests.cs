using System.Text.Json;
using RcloneUI.Contracts.HostProtocol.V1;
using RcloneUI.Host;
using RcloneUI.Mounts;
using RcloneUI.Rclone;

namespace RcloneUI.IntegrationTests;

public sealed class HostRemoteDeletionProtocolTests
{
    [Fact]
    public async Task DeleteRemoteRequiresTheSnapshotRevisionAndRemovesOnlyAnUnreferencedRemote()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rcloneui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projection = new DeleteProjection();
            using var authority = new HostStateAuthority(root, remotes: projection);

            var deleted = await authority.DispatchAsync(Command(projection.Remote.Id, projection.Remote.Revision), TestContext.Current.CancellationToken);

            Assert.Equal("remote-deleted", deleted.ResultType);
            Assert.True(projection.Deleted);
            projection = new();
            projection.Profiles.Add(new(new(Guid.NewGuid()), 1, "Uses remote", projection.Remote.Id, "", MountPresentationMode.NetworkDrive, DriveLetterSelection.Automatic, 'R', null, "Cloud", MountCachePreset.ReadOnlyBrowsing, false));
            using var blockedAuthority = new HostStateAuthority(root, remotes: projection);
            var blocked = await blockedAuthority.DispatchAsync(Command(projection.Remote.Id, projection.Remote.Revision), TestContext.Current.CancellationToken);
            Assert.Equal("remote-delete-blocked-profile", blocked.ResultType);
            Assert.False(projection.Deleted);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ProtocolEnvelope Command(Guid remoteId, ulong expectedRevision) => ProtocolEnvelope.CreateRequest(
        MessageType.Command, new("remote-delete-request"), 1, new(new("client"), 0),
        new(new($"remote-delete-{Guid.NewGuid():N}"), new("remote-delete-cancel"), DateTimeOffset.UtcNow.AddMinutes(1)),
        JsonSerializer.SerializeToUtf8Bytes(new { commandType = "delete-remote", arguments = new { remoteId, expectedRevision } }));

    private sealed class DeleteProjection : IHostRemoteManager, IHostMountProfileManager
    {
        internal HostRemoteSummary Remote { get; } = new(Guid.NewGuid(), "Personal Drive", "drive", 3, "Healthy", null);
        internal List<SavedMountProfile> Profiles { get; } = [];
        internal bool Deleted { get; private set; }
        public string SessionState => "operational";
        public ValueTask<IReadOnlyList<HostRemoteSummary>> ListAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<HostRemoteSummary>>(Deleted ? [] : [Remote]);
        public ValueTask<string> AddTokenRemoteAsync(string displayName, string providerType, string token, IRcloneRuntime rclone, CancellationToken cancellationToken) => ValueTask.FromResult("remote-input-invalid");
        public ValueTask<string> AddConnectionRemoteAsync(string displayName, string providerType, IReadOnlyDictionary<string, string> configuration, IRcloneRuntime rclone, CancellationToken cancellationToken) => ValueTask.FromResult("remote-input-invalid");
        public ValueTask<string> DeleteRemoteAsync(Guid remoteId, ulong expectedRevision, CancellationToken cancellationToken)
        {
            if (remoteId != Remote.Id || expectedRevision != Remote.Revision) return ValueTask.FromResult("remote-delete-conflict");
            Deleted = true;
            return ValueTask.FromResult("remote-deleted");
        }
        public ValueTask<IReadOnlyList<SavedMountProfile>> ListMountProfilesAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<SavedMountProfile>>(Profiles);
        public ValueTask<SavedMountProfile?> ReadMountProfileAsync(MountProfileId id, CancellationToken cancellationToken) => ValueTask.FromResult<SavedMountProfile?>(null);
        public ValueTask<(string ResultType, SavedMountProfile? Profile)> UpsertMountProfileAsync(SavedMountProfile profile, ulong expectedRevision, CancellationToken cancellationToken) => ValueTask.FromResult<(string, SavedMountProfile?)>(("mount-profile-invalid", null));
        public ValueTask<string> DeleteMountProfileAsync(MountProfileId id, ulong expectedRevision, CancellationToken cancellationToken) => ValueTask.FromResult("mount-profile-conflict");
    }
}
