namespace RcloneUI.Contracts.Tests;

public sealed class ModuleTests
{
    [Fact]
    public void ContractsModuleHasAStableName() => Assert.Equal("Contracts", ModuleIdentity.Name);
}
