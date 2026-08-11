using System.Collections.Immutable;

namespace RcloneUI.Remotes;

public readonly record struct RemoteId(Guid Value)
{
    public static RemoteId New() => new(Guid.NewGuid());
}

public enum RemoteSetupStage { ChooseProvider, Configure, TestAndSave, Completed, Failed }
public enum RemoteHealthKind { Unknown, Healthy, AuthenticationFailed, PermissionDenied, NetworkUnavailable, Throttled, InvalidOptions, UnsupportedCapability }
public enum RemoteQuestionKind { Text, Password, Boolean, Choice, OAuth, Confirmation }

public sealed record ProviderOption(
    string Name,
    string Label,
    RemoteQuestionKind Kind,
    bool Required,
    bool Advanced,
    bool Sensitive,
    bool Exclusive,
    string? DefaultValue,
    ImmutableArray<string> Examples);

public sealed record RemoteProvider(
    string BackendType,
    string DisplayName,
    string? Description,
    bool Curated,
    ImmutableArray<ProviderOption> Options);

public sealed record ProviderCatalog(ImmutableArray<RemoteProvider> Providers, string SchemaBinding);
public sealed record RemoteAnswer(string Name, string Value, string? AuthorizationState = null);
public sealed record RemoteQuestion(string State, ProviderOption Option, string? ErrorCode);
public sealed record SetupSnapshot(Guid SetupId, RemoteSetupStage Stage, string? ProviderType, string? DisplayName, RemoteQuestion? Question, string? AuthorizationState, RemoteFailure? Failure);
public sealed record RemoteFailure(RemoteHealthKind Kind, string Stage, string RecoveryAction, string DiagnosticCode);
public sealed record RemoteHealth(RemoteHealthKind Kind, DateTimeOffset TestedUtc, string? CapabilityBinding, RemoteFailure? Failure);
public sealed record RemoteSummary(RemoteId Id, string DisplayName, string ProviderType, ulong Revision, RemoteHealth Health);
public sealed record RemoteLocation(RemoteId RemoteId, string Path);
public sealed record PageRequest(int PageSize, string? ContinuationToken);
public sealed record BrowseItem(string Name, string Path, bool IsDirectory, long? Size, DateTimeOffset? ModifiedUtc, string? MimeType);
public sealed record BrowsePage(ImmutableArray<BrowseItem> Items, string? ContinuationToken);

public enum DeleteDependencyChoice { Cancel, DisableDependents, DeleteSelectedDependents }
public sealed record RemoteDependency(string Kind, Guid Id, string DisplayName, bool Active);
public sealed record RemoteDeleteRequest(RemoteId RemoteId, DeleteDependencyChoice Choice, ImmutableHashSet<Guid> SelectedDependents, ulong ExpectedRevision);
public sealed record RemoteDeleteResult(bool Applied, ImmutableArray<RemoteDependency> Dependencies, string? FailureCode);

public enum ImportConflictChoice { Rename, Replace, Skip }
public sealed record RemoteImportCandidate(string SourceName, string ProviderType, bool ContainsEncryptedFields, bool Supported, string? ConflictsWith);
public sealed record RemoteImportPreview(Guid ImportId, ImmutableArray<RemoteImportCandidate> Candidates);
public sealed record RemoteImportSelection(string SourceName, ImportConflictChoice Choice, string? NewDisplayName);
public sealed record RemoteImportResult(ImmutableArray<RemoteSummary> Imported, ImmutableArray<string> Skipped);

public interface IRemoteCatalog
{
    ValueTask<ProviderCatalog> DescribeProvidersAsync(CancellationToken cancellationToken = default);
    ValueTask<SetupSnapshot> BeginSetupAsync(string providerType, string displayName, CancellationToken cancellationToken = default);
    ValueTask<SetupSnapshot> BeginRepairAsync(RemoteId remoteId, CancellationToken cancellationToken = default);
    SetupSnapshot ObserveSetup(Guid setupId);
    void CancelSetup(Guid setupId);
    ValueTask<SetupSnapshot> AdvanceSetupAsync(Guid setupId, RemoteAnswer answer, CancellationToken cancellationToken = default);
    ValueTask<RemoteHealth> TestSetupAsync(Guid setupId, CancellationToken cancellationToken = default);
    ValueTask<RemoteSummary> SaveSetupAsync(Guid setupId, CancellationToken cancellationToken = default);
    ValueTask<RemoteHealth> TestAsync(RemoteId remoteId, CancellationToken cancellationToken = default);
    ValueTask<BrowsePage> BrowseAsync(RemoteLocation location, PageRequest page, CancellationToken cancellationToken = default);
    ValueTask<RemoteDeleteResult> DeleteAsync(RemoteDeleteRequest request, CancellationToken cancellationToken = default);
    ValueTask<RemoteImportPreview> PreviewImportAsync(ReadOnlyMemory<byte> rcloneConfiguration, CancellationToken cancellationToken = default);
    ValueTask<RemoteImportResult> ApplyImportAsync(Guid importId, ImmutableArray<RemoteImportSelection> selections, CancellationToken cancellationToken = default);
}
