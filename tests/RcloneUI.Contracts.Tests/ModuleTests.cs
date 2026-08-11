namespace RcloneUI.Contracts.Tests;

public sealed class ModuleTests
{
    [Fact]
    public void ContractsModuleHasAStableName() => Assert.Equal("Contracts", ModuleIdentity.Name);

    [Fact]
    public void DomainIdentifiersAndUnitsRetainExactValues()
    {
        var remoteId = new RemoteId(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var bytes = new ByteCount(8_388_608);
        var milliseconds = new MillisecondCount(15_000);

        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), remoteId.Value);
        Assert.Equal(8_388_608UL, bytes.Value);
        Assert.Equal(15_000UL, milliseconds.Value);
    }
}
