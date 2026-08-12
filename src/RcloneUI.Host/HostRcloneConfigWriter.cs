using System.Text;
using RcloneUI.Remotes;

namespace RcloneUI.Host;

internal sealed class HostRcloneConfigWriter(string dataRootPath) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, StoredRemote> bound = [];
    private readonly string path = Path.Combine(Path.GetFullPath(dataRootPath), "runtime", "rclone.conf");

    internal async ValueTask<string> BindAsync(StoredRemote remote, CancellationToken cancellationToken)
    {
        Validate(remote);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bound[remote.Id.Value] = remote;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".new";
            await File.WriteAllTextAsync(temporary, Serialize(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            return Name(remote.Id.Value) + ":";
        }
        finally { gate.Release(); }
    }

    internal static string FileSystem(Guid remoteId) => Name(remoteId) + ":";

    internal async ValueTask UnbindAsync(Guid remoteId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!bound.Remove(remoteId)) return;
            var temporary = path + ".new";
            await File.WriteAllTextAsync(temporary, Serialize(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { gate.Release(); }
    }

    internal async ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bound.Clear();
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        finally { gate.Release(); }
    }

    private string Serialize()
    {
        var text = new StringBuilder();
        foreach (var remote in bound.Values.OrderBy(item => item.Id.Value))
        {
            text.Append('[').Append(Name(remote.Id.Value)).AppendLine("]");
            text.Append("type = ").AppendLine(remote.ProviderType);
            foreach (var option in remote.Configuration.OrderBy(item => item.Key, StringComparer.Ordinal))
                text.Append(option.Key).Append(" = ").AppendLine(option.Value);
            text.AppendLine();
        }
        return text.ToString();
    }

    private static string Name(Guid id) => $"rcloneui_{id:N}";

    private static void Validate(StoredRemote remote)
    {
        if (!IsIdentifier(remote.ProviderType)) throw new InvalidDataException("Remote provider type is invalid.");
        if (remote.Configuration.Count > 256) throw new InvalidDataException("Remote configuration has too many options.");
        foreach (var option in remote.Configuration)
            if (!IsIdentifier(option.Key) || option.Value.Length > 16 * 1024 || option.Value.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new InvalidDataException("Remote configuration cannot be represented safely.");
    }

    private static bool IsIdentifier(string value) => value.Length is > 0 and <= 128 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    public void Dispose()
    {
        gate.Dispose();
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
