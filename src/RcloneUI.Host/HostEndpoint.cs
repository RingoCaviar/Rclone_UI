using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RcloneUI.Contracts.HostProtocol.V1;

namespace RcloneUI.Host;

internal sealed record HostEndpoint(
    int Format,
    Guid DataRootId,
    string PipeName,
    int HostProcessId,
    DateTimeOffset HostStartTimeUtc,
    int SessionId,
    Guid Incarnation,
    string ChallengeKey,
    string HostBuild,
    int ProtocolMajor,
    int ProtocolMinor);

internal static class HostEndpointNaming
{
    internal static (string MutexName, string PipeName) Derive(Guid dataRootId, string logonSid)
    {
        var input = Encoding.UTF8.GetBytes($"RcloneUI\0{HostProtocolVersion.Family}\0{logonSid}\0{dataRootId:D}");
        var suffix = Convert.ToHexString(SHA256.HashData(input))[..40];
        return ($"Local\\RcloneUI.Host.{suffix}", $"RcloneUI\\host\\{suffix}");
    }
}

internal sealed class HostOwnership : IDisposable
{
    private readonly MutexLease mutex;
    private readonly FileStream lockFile;

    private HostOwnership(MutexLease mutex, FileStream lockFile)
    {
        this.mutex = mutex;
        this.lockFile = lockFile;
    }

    internal static HostOwnership? TryAcquire(string dataRootPath, string mutexName)
    {
        var mutex = MutexLease.TryAcquire(mutexName);
        if (mutex is null) return null;
        try
        {
            var runtime = Path.Combine(Path.GetFullPath(dataRootPath), "runtime");
            Directory.CreateDirectory(runtime);
            FileStream lockFile;
            try
            {
                lockFile = new FileStream(Path.Combine(runtime, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                mutex.Dispose();
                return null;
            }

            return new(mutex, lockFile);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lockFile.Dispose();
        mutex.Dispose();
    }

    private sealed class MutexLease : IDisposable
    {
        private readonly ManualResetEventSlim stop = new(false);
        private readonly Thread owner;

        private MutexLease(string name)
        {
            using var started = new ManualResetEventSlim(false);
            owner = new Thread(() => Own(name, started))
            {
                IsBackground = true,
                Name = "RcloneUI Host mutex owner",
            };
            owner.Start();
            started.Wait();
        }

        internal bool Acquired { get; private set; }

        internal static MutexLease? TryAcquire(string name)
        {
            var lease = new MutexLease(name);
            if (lease.Acquired) return lease;
            lease.Dispose();
            return null;
        }

        public void Dispose()
        {
            stop.Set();
            owner.Join();
            stop.Dispose();
        }

        private void Own(string name, ManualResetEventSlim started)
        {
            var signaled = false;
            try
            {
                using var mutex = new Mutex(initiallyOwned: false, name);
                try
                {
                    Acquired = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    Acquired = true;
                }

                started.Set();
                signaled = true;
                if (!Acquired) return;
                stop.Wait();
                mutex.ReleaseMutex();
            }
            catch
            {
                if (!signaled) started.Set();
            }
        }
    }
}

internal static class HostEndpointPublisher
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static HostEndpoint Create(Guid dataRootId, string pipeName)
    {
        using var process = Process.GetCurrentProcess();
        return new(
            1,
            dataRootId,
            pipeName,
            Environment.ProcessId,
            process.StartTime.ToUniversalTime(),
            process.SessionId,
            Guid.NewGuid(),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            typeof(HostEndpointPublisher).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            HostProtocolVersion.Major,
            HostProtocolVersion.CurrentMinor);
    }

    internal static void Publish(string dataRootPath, HostEndpoint endpoint)
    {
        var runtime = Path.Combine(Path.GetFullPath(dataRootPath), "runtime");
        Directory.CreateDirectory(runtime);
        var destination = Path.Combine(runtime, "endpoint.json");
        var temporary = destination + ".new";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(endpoint, Options);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, destination, overwrite: true);
    }

    internal static HostEndpoint Read(string dataRootPath)
    {
        var path = Path.Combine(Path.GetFullPath(dataRootPath), "runtime", "endpoint.json");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is 0 or > 4096) throw new InvalidDataException("Endpoint record size is invalid.");
        var endpoint = JsonSerializer.Deserialize<HostEndpoint>(bytes, Options) ?? throw new InvalidDataException("Endpoint record is invalid.");
        if (endpoint.Format != 1 || endpoint.DataRootId == Guid.Empty || endpoint.Incarnation == Guid.Empty
            || endpoint.ProtocolMajor != HostProtocolVersion.Major || Convert.FromBase64String(endpoint.ChallengeKey).Length != 32)
            throw new InvalidDataException("Endpoint record fields are invalid.");
        return endpoint;
    }
}
