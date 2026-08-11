using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

const int memoryKiB = 64 * 1024;
const int iterations = 3;
const int parallelism = 4;

var vectorOutput = Derive(
    Enumerable.Repeat((byte)0x01, 32).ToArray(),
    Enumerable.Repeat((byte)0x02, 16).ToArray(),
    32,
    3,
    4,
    Enumerable.Repeat((byte)0x03, 8).ToArray(),
    Enumerable.Repeat((byte)0x04, 12).ToArray());
var expected = Convert.FromHexString("0D640DF58D78766C08C037A34A8B53C9D01EF0452D75B65EB52520E96B01E659");
var vectorPass = CryptographicOperations.FixedTimeEquals(vectorOutput, expected);

var password = Convert.FromHexString("00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF");
var salt = Convert.FromHexString("FFEEDDCCBBAA99887766554433221100");

// Warm the JIT before measuring.
CryptographicOperations.ZeroMemory(Derive(password, salt, memoryKiB, iterations, parallelism));

var sequential = new List<double>();
for (var i = 0; i < 7; i++)
{
    var sw = Stopwatch.StartNew();
    var key = Derive(password, salt, memoryKiB, iterations, parallelism);
    sw.Stop();
    sequential.Add(sw.Elapsed.TotalMilliseconds);
    CryptographicOperations.ZeroMemory(key);
}

var concurrency = new List<object>();
foreach (var workers in new[] { 1, 2, 4 })
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    var process = Process.GetCurrentProcess();
    var beforePrivate = process.PrivateMemorySize64;
    var sw = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, workers).Select(async worker =>
    {
        await Task.Yield();
        var workerSalt = (byte[])salt.Clone();
        workerSalt[^1] ^= (byte)worker;
        var key = Derive(password, workerSalt, memoryKiB, iterations, parallelism);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(workerSalt);
    }));
    sw.Stop();
    process.Refresh();
    concurrency.Add(new
    {
        workers,
        elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
        privateMemoryDeltaMiB = Math.Round((process.PrivateMemorySize64 - beforePrivate) / 1048576d, 2),
        peakWorkingSetMiB = Math.Round(process.PeakWorkingSet64 / 1048576d, 2),
        theoreticalArgonMemoryMiB = workers * memoryKiB / 1024
    });
}

CryptographicOperations.ZeroMemory(password);
CryptographicOperations.ZeroMemory(salt);
CryptographicOperations.ZeroMemory(vectorOutput);
CryptographicOperations.ZeroMemory(expected);

var sorted = sequential.Order().ToArray();
var result = new
{
    prototype = true,
    timestampUtc = DateTimeOffset.UtcNow,
    os = Environment.OSVersion.VersionString,
    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    processorCount = Environment.ProcessorCount,
    package = new
    {
        name = "BouncyCastle.Cryptography",
        version = typeof(Argon2BytesGenerator).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    },
    parameters = new { variant = "Argon2id", version = 19, memoryKiB, iterations, parallelism, outputBytes = 32 },
    rfc9106Vector = new { pass = vectorPass, actual = Convert.ToHexString(Derive(Enumerable.Repeat((byte)1, 32).ToArray(), Enumerable.Repeat((byte)2, 16).ToArray(), 32, 3, 4, Enumerable.Repeat((byte)3, 8).ToArray(), Enumerable.Repeat((byte)4, 12).ToArray())) },
    sequentialMs = sequential.Select(x => Math.Round(x, 2)),
    medianMs = Math.Round(sorted[sorted.Length / 2], 2),
    p95SampleMs = Math.Round(sorted[^1], 2),
    concurrency
};

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
return vectorPass ? 0 : 2;

static byte[] Derive(byte[] password, byte[] salt, int memoryKiB, int iterations, int parallelism, byte[]? secret = null, byte[]? additional = null)
{
    var builder = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
        .WithVersion(Argon2Parameters.Version13)
        .WithMemoryAsKB(memoryKiB)
        .WithIterations(iterations)
        .WithParallelism(parallelism)
        .WithSalt(salt);
    if (secret is not null) builder.WithSecret(secret);
    if (additional is not null) builder.WithAdditional(additional);
    var parameters = builder.Build();
    var generator = new Argon2BytesGenerator();
    generator.Init(parameters);
    var output = new byte[32];
    generator.GenerateBytes(password, output);
    return output;
}
