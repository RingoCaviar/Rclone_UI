using System.Text.Json;
using RcloneUI.Mounts;

namespace RcloneUI.Host;

internal sealed record HostMountLifecycleRecord(Guid InstanceId, Guid? ProfileId, string MountPoint, MountPresentationMode PresentationMode, string State, DateTimeOffset StartedUtc, DateTimeOffset UpdatedUtc, string? DiagnosticCode);

internal interface IHostMountLifecycleJournal
{
    IReadOnlyList<HostMountLifecycleRecord> Read();
    void Write(IReadOnlyList<HostMountLifecycleRecord> records);
}

internal sealed class HostMountLifecycleJournal(string dataRootPath) : IHostMountLifecycleJournal
{
    private const long MaximumBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string path = Path.Combine(dataRootPath, "runtime", "mount-lifecycle.json");

    public IReadOnlyList<HostMountLifecycleRecord> Read()
    {
        if (!File.Exists(path)) return [];
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Mount lifecycle journal has an invalid size.");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            var document = JsonSerializer.Deserialize<JournalDocument>(stream, Json);
            if (document is null || document.Format != 1 || document.Records is null) throw new InvalidDataException("Mount lifecycle journal format is invalid.");
            if (document.Records.Any(record => record.InstanceId == Guid.Empty || string.IsNullOrWhiteSpace(record.MountPoint) || record.StartedUtc == default || record.UpdatedUtc < record.StartedUtc))
                throw new InvalidDataException("Mount lifecycle journal contains invalid records.");
            return document.Records;
        }
        catch (JsonException exception) { throw new InvalidDataException("Mount lifecycle journal is corrupt.", exception); }
    }

    public void Write(IReadOnlyList<HostMountLifecycleRecord> records)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, new JournalDocument(1, records), Json);
            stream.Flush(flushToDisk: true);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("Mount lifecycle journal exceeds its size limit.");
        }
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record JournalDocument(int Format, IReadOnlyList<HostMountLifecycleRecord> Records);
}
