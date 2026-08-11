using RcloneUI.Host;

namespace RcloneUI.IntegrationTests;

public sealed class WinFspDetectionTests
{
    [Theory]
    [InlineData(false, false, false, false, false, null, "missing", "winfsp-not-installed")]
    [InlineData(true, false, true, true, true, "2.1", "incomplete", "winfsp-registry-incomplete")]
    [InlineData(true, true, true, false, true, "2.1", "incomplete", "winfsp-x64-files-missing")]
    [InlineData(true, true, true, true, true, null, "incomplete", "winfsp-version-unavailable")]
    [InlineData(true, true, true, true, true, "2.1", "ready", "winfsp-ready")]
    public void DetectionRequiresCorroboratedRegistryDirectoriesFilesAndVersion(bool registry, bool install, bool sideBySide, bool library, bool driver, string? version, string status, string code)
    {
        var detector = new WindowsWinFspDetector(new FakeEvidenceSource(new(registry, install, sideBySide, library, driver, version)));

        var result = detector.Inspect();

        Assert.Equal(status, result.Status);
        Assert.Equal(code, result.DiagnosticCode);
    }

    private sealed class FakeEvidenceSource(WinFspEvidence evidence) : IWinFspEvidenceSource
    {
        public WinFspEvidence Read() => evidence;
    }
}
