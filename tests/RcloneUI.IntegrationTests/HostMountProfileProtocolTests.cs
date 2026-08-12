using System.Text.Json;
using RcloneUI.Contracts.HostProtocol.V1;
using RcloneUI.Host;
using RcloneUI.Mounts;

namespace RcloneUI.IntegrationTests;

public sealed class HostMountProfileProtocolTests
{
    [Fact]
    public async Task SavingNetworkDriveProfileAcceptsAnEmptyRemoteSubpath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rcloneui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projection = new ProfilesProjection();
            using var authority = new HostStateAuthority(root, remotes: projection);
            var profileId = Guid.NewGuid();
            var remoteId = Guid.NewGuid();
            var envelope = Command("save-mount-profile", new
            {
                profileId,
                expectedRevision = 0UL,
                displayName = "FTPS drive",
                remoteId,
                subpath = "",
                presentationMode = "network-drive",
                driveSelection = "automatic",
                cachePreset = "read-only",
                driveLetter = "R",
                fixedDirectoryPath = (string?)null,
                volumeName = "FTPS drive"
            });

            var result = await authority.DispatchAsync(envelope, TestContext.Current.CancellationToken);

            Assert.Equal("mount-profile-saved", result.ResultType);
            Assert.Equal(string.Empty, Assert.Single(projection.Profiles).Subpath);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SavingStandardReadWriteProfilePreservesItsPreset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rcloneui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projection = new ProfilesProjection();
            using var authority = new HostStateAuthority(root, remotes: projection);
            var result = await authority.DispatchAsync(Command("save-mount-profile", new
            {
                profileId = Guid.NewGuid(),
                expectedRevision = 0UL,
                displayName = "Writable FTPS drive",
                remoteId = Guid.NewGuid(),
                subpath = "",
                presentationMode = "network-drive",
                driveSelection = "automatic",
                cachePreset = "standard-read-write",
                driveLetter = "R",
                fixedDirectoryPath = (string?)null,
                volumeName = "FTPS drive"
            }), TestContext.Current.CancellationToken);

            Assert.Equal("mount-profile-saved", result.ResultType);
            Assert.Equal(MountCachePreset.StandardReadWrite, Assert.Single(projection.Profiles).CachePreset);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ProtocolEnvelope Command(string commandType, object arguments) => ProtocolEnvelope.CreateRequest(
        MessageType.Command, new("mount-profile-request"), 1, new(new("client"), 0),
        new(new("mount-profile-key"), new("mount-profile-cancel"), DateTimeOffset.UtcNow.AddMinutes(1)),
        JsonSerializer.SerializeToUtf8Bytes(new { commandType, arguments }));

    private sealed class ProfilesProjection : IHostRemoteProjection, IHostMountProfileManager
    {
        public List<SavedMountProfile> Profiles { get; } = [];
        public string SessionState => "operational";
        public ValueTask<IReadOnlyList<HostRemoteSummary>> ListAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<HostRemoteSummary>>([]);
        public ValueTask<IReadOnlyList<SavedMountProfile>> ListMountProfilesAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<SavedMountProfile>>(Profiles);
        public ValueTask<SavedMountProfile?> ReadMountProfileAsync(MountProfileId id, CancellationToken cancellationToken) => ValueTask.FromResult<SavedMountProfile?>(Profiles.SingleOrDefault(profile => profile.Id == id));
        public ValueTask<(string ResultType, SavedMountProfile? Profile)> UpsertMountProfileAsync(SavedMountProfile profile, ulong expectedRevision, CancellationToken cancellationToken)
        {
            var saved = profile with { Revision = expectedRevision + 1 };
            Profiles.RemoveAll(item => item.Id == profile.Id);
            Profiles.Add(saved);
            return ValueTask.FromResult<(string, SavedMountProfile?)>(("mount-profile-saved", saved));
        }
        public ValueTask<string> DeleteMountProfileAsync(MountProfileId id, ulong expectedRevision, CancellationToken cancellationToken) => ValueTask.FromResult("mount-profile-deleted");
    }
}
