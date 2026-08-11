using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RcloneUI.Diagnostics;

public sealed partial class DiagnosticExporter : IDiagnosticExporter
{
    public const int MaximumItems = 100;
    public const int MaximumItemBytes = 1024 * 1024;
    public const long MaximumExportBytes = 10L * 1024 * 1024;
    private static readonly DateTimeOffset StableTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public DiagnosticPreview Preview(IReadOnlyCollection<DiagnosticItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var results = new List<DiagnosticPreviewItem>(Math.Min(items.Count, MaximumItems));
        long total = 0; var redactions = 0;
        foreach (var item in items.OrderBy(x => x.LogicalName, StringComparer.Ordinal).Take(MaximumItems))
        {
            var original = Encoding.UTF8.GetByteCount(item.Content);
            if (item.Sensitive) { results.Add(new(item.LogicalName, original, 0, 0, false, "sensitive-source-excluded")); continue; }
            if (!SafeName(item.LogicalName)) { results.Add(new(item.LogicalName, original, 0, 0, false, "unsafe-logical-name")); continue; }
            var (content, count) = Redact(item.Content);
            var bytes = Encoding.UTF8.GetByteCount(content);
            if (original > MaximumItemBytes || total + bytes > MaximumExportBytes) { results.Add(new(item.LogicalName, original, 0, count, false, "size-limit")); continue; }
            total += bytes; redactions += count; results.Add(new(item.LogicalName, original, bytes, count, true));
        }
        return new(results, total, redactions);
    }

    public async ValueTask<DiagnosticExportResult> ExportAsync(IReadOnlyCollection<DiagnosticItem> items, string destinationArchive, CancellationToken cancellationToken = default)
    {
        var preview = Preview(items);
        var selected = preview.Items.Where(x => x.Included).ToDictionary(x => x.LogicalName, StringComparer.Ordinal);
        var absolute = Path.GetFullPath(destinationArchive);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await using (var stream = new FileStream(absolute, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.WriteThrough))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in items.OrderBy(x => x.LogicalName, StringComparer.Ordinal))
            {
                if (!selected.ContainsKey(item.LogicalName)) continue;
                var entry = archive.CreateEntry(item.LogicalName.Replace('\\', '/'), CompressionLevel.SmallestSize); entry.LastWriteTime = StableTimestamp;
                await using var target = entry.Open();
                var (content, _) = Redact(item.Content); var bytes = Encoding.UTF8.GetBytes(content);
                try { await target.WriteAsync(bytes, cancellationToken).ConfigureAwait(false); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        }
        await using var completed = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(absolute, Convert.ToHexString(await SHA256.HashDataAsync(completed, cancellationToken).ConfigureAwait(false)), preview);
    }

    internal static (string Content, int Count) Redact(string value)
    {
        var count = 0;
        value = SecretPattern().Replace(value, match => { count++; return $"{match.Groups[1].Value}=<redacted>"; });
        value = AuthorizationPattern().Replace(value, _ => { count++; return "Authorization: <redacted>"; });
        value = WindowsPathPattern().Replace(value, match => { count++; return $"{match.Groups[1].Value}:\\<path-{ShortHash(match.Value)}>"; });
        value = UncPathPattern().Replace(value, match => { count++; return $"\\\\<network-path-{ShortHash(match.Value)}>"; });
        return (value, count);
    }

    private static string ShortHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();
    private static bool SafeName(string value) => !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) && !value.Split('/', '\\').Contains("..", StringComparer.Ordinal) && value.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
    [GeneratedRegex(@"(?im)\b(password|passwd|pass|token|secret|api[_-]?key|client[_-]?secret)\s*=\s*[^\s;,]+", RegexOptions.CultureInvariant)] private static partial Regex SecretPattern();
    [GeneratedRegex(@"(?im)Authorization\s*:\s*[^\r\n]+", RegexOptions.CultureInvariant)] private static partial Regex AuthorizationPattern();
    [GeneratedRegex(@"(?i)\b([a-z]):\\(?:[^\s<>:\""/|?*]+\\)*[^\s<>:\""/|?*]*", RegexOptions.CultureInvariant)] private static partial Regex WindowsPathPattern();
    [GeneratedRegex(@"\\\\[^\\\s]+\\[^\s]+", RegexOptions.CultureInvariant)] private static partial Regex UncPathPattern();
}
