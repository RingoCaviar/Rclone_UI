using System.Text.Json;
using System.Text.Json.Nodes;
using RcloneUI.Rclone;

namespace RcloneUI.IntegrationTests;

public sealed class MountCompatibilityFixtureTests
{
    [Theory]
    [InlineData("rclone-v1.74.4.json", "v1.74.4")]
    [InlineData("rclone-v1.75.0.json", "v1.75.0")]
    public void RealGoldenFixturesMapToTypedCapabilities(string fileName, string version)
    {
        using var document = Load(fileName);
        var fixture = MountCompatibilityFixture.Parse(document.RootElement);

        Assert.Equal(version, fixture.RcloneVersion);
        Assert.True(fixture.Features.CanMount);
        Assert.True(fixture.Features.CanObserveStats);
        Assert.True(fixture.Features.CanObserveQueue);
        Assert.True(fixture.Features.HasMountOptionSchema);
        Assert.True(fixture.Features.HasVfsOptionSchema);
        Assert.Equal(RcloneVfsCacheMode.Minimal, fixture.Stats.CacheMode);
        Assert.Equal(0, fixture.Stats.UploadsQueued);
    }

    [Fact]
    public void MissingStatsFieldIsUnknownAndDoesNotDisableMountOrQueue()
    {
        var root = JsonNode.Parse(File.ReadAllText(PathFor("rclone-v1.75.0.json")))!.AsObject();
        root["vfsStats"]!["diskCache"]!.AsObject().Remove("uploadsQueued");
        using var document = JsonDocument.Parse(root.ToJsonString());

        var fixture = MountCompatibilityFixture.Parse(document.RootElement);

        Assert.Null(fixture.Stats.UploadsQueued);
        Assert.True(fixture.Features.CanMount);
        Assert.True(fixture.Features.CanObserveQueue);
    }

    [Fact]
    public void MissingQueueDisablesOnlyQueueObservation()
    {
        var root = JsonNode.Parse(File.ReadAllText(PathFor("rclone-v1.75.0.json")))!.AsObject();
        root.Remove("vfsQueue");
        using var document = JsonDocument.Parse(root.ToJsonString());

        var fixture = MountCompatibilityFixture.Parse(document.RootElement);

        Assert.False(fixture.Features.CanObserveQueue);
        Assert.True(fixture.Features.CanMount);
        Assert.True(fixture.Features.CanObserveStats);
    }

    [Fact]
    public void UnknownCacheModeDoesNotGuessAnEnumValue()
    {
        var root = JsonNode.Parse(File.ReadAllText(PathFor("rclone-v1.75.0.json")))!.AsObject();
        root["vfsStats"]!["opt"]!["CacheMode"] = 99;
        using var document = JsonDocument.Parse(root.ToJsonString());

        Assert.Null(MountCompatibilityFixture.Parse(document.RootElement).Stats.CacheMode);
    }

    private static JsonDocument Load(string fileName) => JsonDocument.Parse(File.ReadAllText(PathFor(fileName)));
    private static string PathFor(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "MountCompatibility", fileName);
}
