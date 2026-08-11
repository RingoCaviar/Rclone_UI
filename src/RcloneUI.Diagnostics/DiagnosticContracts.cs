namespace RcloneUI.Diagnostics;

public sealed record DiagnosticItem(string LogicalName, string Content, bool Sensitive = false);
public sealed record DiagnosticPreviewItem(string LogicalName, long OriginalBytes, long ExportBytes, int RedactionCount, bool Included, string? ExclusionReason = null);
public sealed record DiagnosticPreview(IReadOnlyList<DiagnosticPreviewItem> Items, long TotalExportBytes, int TotalRedactions);
public sealed record DiagnosticExportResult(string ArchivePath, string Sha256, DiagnosticPreview Preview);

public interface IDiagnosticExporter
{
    DiagnosticPreview Preview(IReadOnlyCollection<DiagnosticItem> items);
    ValueTask<DiagnosticExportResult> ExportAsync(IReadOnlyCollection<DiagnosticItem> items, string destinationArchive, CancellationToken cancellationToken = default);
}
