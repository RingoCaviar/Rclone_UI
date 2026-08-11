using System.Diagnostics;
using System.Security.Cryptography;
using RcloneUI.DataRoot;

namespace RcloneUI.IntegrationTests;

public sealed class NativeArgon2CandidateTests
{
    [Fact]
    public async Task PinnedCandidateMeetsCorrectnessLatencyAndAdmissionGates()
    {
        var path = Environment.GetEnvironmentVariable("RCLONEUI_ARGON2_DLL");
        var digest = Environment.GetEnvironmentVariable("RCLONEUI_ARGON2_SHA256");
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(digest)) Assert.Skip("Exact native candidate is supplied by the dedicated Windows validation workflow.");
        var password = Convert.FromHexString("00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF");
        var salt = Convert.FromHexString("FFEEDDCCBBAA99887766554433221100");
        try
        {
            using var deriver = new LibArgon2KeyDeriver(new(path, digest));
            var baseline = new byte[32]; deriver.Derive(password, salt, Argon2Parameters.Default, baseline);
            var samples = new List<double>();
            for (var index = 0; index < 7; index++)
            {
                var output = new byte[32]; var timer = Stopwatch.StartNew();
                deriver.Derive(password, salt, Argon2Parameters.Default, output); timer.Stop();
                Assert.True(CryptographicOperations.FixedTimeEquals(baseline, output));
                samples.Add(timer.Elapsed.TotalMilliseconds); CryptographicOperations.ZeroMemory(output);
            }
            Assert.True(samples.Order().ElementAt(6) <= 1_500, $"p95 sample exceeded 1.5 seconds: {string.Join(", ", samples)}");
            var concurrent = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => { var output = new byte[32]; try { deriver.Derive(password, salt, Argon2Parameters.Default, output); } finally { CryptographicOperations.ZeroMemory(output); } })));
            concurrent.Stop();
            Assert.True(concurrent.Elapsed < TimeSpan.FromSeconds(5), $"Queued derivations exceeded the bounded gate: {concurrent.Elapsed}.");
            CryptographicOperations.ZeroMemory(baseline);
        }
        finally { CryptographicOperations.ZeroMemory(password); CryptographicOperations.ZeroMemory(salt); }
    }
}
