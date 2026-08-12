using System.Collections.Immutable;
using System.Text.Json;
using RcloneUI.Rclone;

namespace RcloneUI.IntegrationTests;

public sealed class RcloneCapabilityDiscoveryTests
{
    [Fact]
    public void RcListCommandObjectsExposeMountEndpoints()
    {
        using var commands = JsonDocument.Parse("""{"commands":[{"Path":"mount/mount"},{"Path":"mount/unmount"}]}""");
        using var options = JsonDocument.Parse("{}");
        using var mountTypes = JsonDocument.Parse("""{"mountTypes":["cmount"]}""");

        var snapshot = RcloneCapabilityDiscovery.Create(new("v1.75.0", new string('A', 64), 1), commands.RootElement, options.RootElement, mountTypes.RootElement, DateTimeOffset.UtcNow);

        Assert.Contains("mount/mount", snapshot.Endpoints);
        Assert.Contains("mount/unmount", snapshot.Endpoints);
        Assert.Contains("cmount", snapshot.MountTypes);
    }
}
