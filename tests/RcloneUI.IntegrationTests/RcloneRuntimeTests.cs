using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using RcloneUI.Rclone;

namespace RcloneUI.IntegrationTests;

public sealed class RcloneRuntimeTests
{
    [Fact]
    public void DaemonArgumentsKeepTheVfsCacheInsideThePortableDataRoot()
    {
        var configuration = RcloneDaemonConfiguration.Create(5572);
        var dataRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "RcloneUI-Portable-Test", Guid.NewGuid().ToString("N"));

        var arguments = configuration.BuildArguments(Path.Combine(dataRoot, "runtime", "rclone.conf"), Path.Combine(dataRoot, "cache", "rclone"));

        Assert.Contains($"--cache-dir={Path.GetFullPath(Path.Combine(dataRoot, "cache", "rclone"))}", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--cache-dir=" + Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RuntimeConfiguresRemoteBeforeUsingItsNamedFileSystem()
    {
        var runtime = new ScriptedRcloneRuntime(CreateCapabilities("config/create", "config/delete", "operations/list"), []);
        var parameters = new Dictionary<string, string> { ["host"] = "files.example.test", ["port"] = "3587" };

        await runtime.ConfigureRemoteAsync("rcloneui_test", "ftp", parameters, passwordsAlreadyObscured: true, TestContext.Current.CancellationToken);

        Assert.Equal("ftp", runtime.ConfiguredRemotes["rcloneui_test"].ProviderType);
        Assert.Equal("3587", runtime.ConfiguredRemotes["rcloneui_test"].Parameters["port"]);
    }

    [Fact]
    public async Task RuntimeFailsClosedWhenVfsQueueObservationIsUnavailable()
    {
        var runtime = new ScriptedRcloneRuntime(CreateCapabilities("vfs/stats"), []);

        await Assert.ThrowsAsync<NotSupportedException>(() => runtime.GetVfsStatusAsync("remote:", TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task RuntimeReturnsTypedVfsUploadObservation()
    {
        var observed = new RcloneVfsStatus(1024, 1, 2, 3, false, 4, 5, DateTimeOffset.UtcNow);
        var runtime = new ScriptedRcloneRuntime(CreateCapabilities("vfs/stats", "vfs/queue"), []) { VfsStatus = observed };

        Assert.Equal(observed, await runtime.GetVfsStatusAsync("remote:", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void BinaryVerificationRejectsChangedBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rclone-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            var expected = Convert.ToHexString(SHA256.HashData([1, 2, 3]));
            Assert.Equal(RcloneComponentHealth.Healthy, BundledRcloneDiscovery.Verify(path, expected, "v1").Health);
            File.AppendAllText(path, "changed");
            Assert.Equal(RcloneComponentHealth.HashMismatch, BundledRcloneDiscovery.Verify(path, expected, "v1").Health);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CapabilityHashIsCanonicalAndSchemaSensitive()
    {
        using var firstList = JsonDocument.Parse("""{"commands":["sync/copy","rc/list"]}""");
        using var secondList = JsonDocument.Parse("""{"commands":["rc/list","sync/copy"]}""");
        using var firstOptions = JsonDocument.Parse("""{"main":{"Retries":1,"Checkers":8}}""");
        using var reorderedOptions = JsonDocument.Parse("""{"main":{"Checkers":8,"Retries":1}}""");
        using var changedOptions = JsonDocument.Parse("""{"main":{"Checkers":9,"Retries":1}}""");
        using var mounts = JsonDocument.Parse("""{"mountTypes":["mount"]}""");
        var binary = new RcloneBinaryIdentity("v1", new string('A', 64), 1);

        var first = RcloneCapabilityDiscovery.Create(binary, firstList.RootElement, firstOptions.RootElement, mounts.RootElement, DateTimeOffset.UtcNow);
        var reordered = RcloneCapabilityDiscovery.Create(binary, secondList.RootElement, reorderedOptions.RootElement, mounts.RootElement, DateTimeOffset.UtcNow);
        var changed = RcloneCapabilityDiscovery.Create(binary, secondList.RootElement, changedOptions.RootElement, mounts.RootElement, DateTimeOffset.UtcNow);

        Assert.Equal(first.Binding, reordered.Binding);
        Assert.NotEqual(first.Binding, changed.Binding);
    }

    [Fact]
    public void BackendFeatureSnapshotFailsWhenRcloneOmitsFeatureTruth()
    {
        using var missing = JsonDocument.Parse("{}");
        Assert.Throws<InvalidDataException>(() => RcloneCapabilityDiscovery.CreateBackend("remote:", missing.RootElement, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ScriptedAdapterFailsClosedOnCapabilityChange()
    {
        var capabilities = CreateCapabilities();
        var runtime = new ScriptedRcloneRuntime(capabilities, []);
        var request = new RcloneExecutionRequest(Guid.NewGuid(), capabilities.Binding + "-old", RclonePrimitive.Copy, new("source:", "a"), new("dest:", "b"), "group");
        await Assert.ThrowsAsync<RcloneCapabilityChangedException>(() => runtime.StartAsync(request, TestContext.Current.CancellationToken).AsTask());
        Assert.Empty(runtime.Requests);
    }

    [Fact]
    public async Task ScriptedAdapterExposesOnlyTypedExecution()
    {
        var capabilities = CreateCapabilities();
        var stats = new RcloneTransferStats(10, 20, 1, 0, 5, TimeSpan.FromSeconds(2), false);
        var runtime = new ScriptedRcloneRuntime(capabilities, [new(RclonePrimitive.Copy, stats, ScriptedRcloneRuntime.Success())]);
        var request = new RcloneExecutionRequest(Guid.NewGuid(), capabilities.Binding, RclonePrimitive.Copy, new("source:", "a"), new("dest:", "b"), "group");

        var handle = await runtime.StartAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(stats, await runtime.GetStatsAsync(handle, TestContext.Current.CancellationToken));
        Assert.True((await runtime.WaitAsync(handle, TestContext.Current.CancellationToken)).Success);
        Assert.Equal(request, Assert.Single(runtime.Requests));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ContainedLauncherRunsOnlyThroughVerifiedBinaryToken()
    {
        var command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(command)));
        var binary = BundledRcloneDiscovery.RequireVerified(command, digest, "test-command");
        using var job = new RcloneJob();
        using var process = job.Launch(binary, ["/d", "/c", "exit 0"]);
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(10)));
    }

    private static RcloneCapabilitySnapshot CreateCapabilities(params string[] endpoints)
    {
        var binary = new RcloneBinaryIdentity("v1", new string('A', 64), 1);
        return new(binary, new string('B', 64), new string('C', 64), ImmutableSortedSet.CreateRange(endpoints.Length == 0 ? ["sync/copy"] : endpoints), ImmutableSortedSet<string>.Empty, DateTimeOffset.UtcNow);
    }
}
