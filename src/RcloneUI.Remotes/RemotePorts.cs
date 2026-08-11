using System.Collections.Immutable;

namespace RcloneUI.Remotes;

public sealed record RemoteEngineQuestion(string State, ProviderOption Option, string? ErrorCode);
public sealed record RemoteEngineProgress(bool Completed, string State, RemoteEngineQuestion? Question, IReadOnlyDictionary<string, string> Configuration);
public sealed record RemoteTestResult(bool Success, RemoteHealthKind FailureKind, string? DiagnosticCode, string? CapabilityBinding);
public sealed record ImportedRemoteConfiguration(string SourceName, string ProviderType, IReadOnlyDictionary<string, string> Configuration);

public interface IRemoteEngine
{
    ValueTask<ProviderCatalog> DescribeProvidersAsync(CancellationToken cancellationToken);
    ValueTask<RemoteEngineProgress> BeginConfigurationAsync(string providerType, CancellationToken cancellationToken);
    ValueTask<RemoteEngineProgress> BeginRepairAsync(string providerType, IReadOnlyDictionary<string, string> current, CancellationToken cancellationToken);
    ValueTask<RemoteEngineProgress> ContinueConfigurationAsync(string providerType, string state, RemoteAnswer answer, IReadOnlyDictionary<string, string> current, CancellationToken cancellationToken);
    ValueTask<RemoteTestResult> TestAsync(string providerType, IReadOnlyDictionary<string, string> configuration, CancellationToken cancellationToken);
    ValueTask<BrowsePage> BrowseAsync(string providerType, IReadOnlyDictionary<string, string> configuration, string path, PageRequest page, CancellationToken cancellationToken);
    ValueTask<RemoteImportPreview> PreviewImportAsync(ReadOnlyMemory<byte> rcloneConfiguration, CancellationToken cancellationToken);
    ValueTask<ImportedRemoteConfiguration> MaterializeImportAsync(ReadOnlyMemory<byte> rcloneConfiguration, string sourceName, CancellationToken cancellationToken);
}

public sealed record StoredRemote(RemoteId Id, string DisplayName, string ProviderType, ulong Revision, IReadOnlyDictionary<string, string> Configuration, RemoteHealth Health);

public interface IRemoteStore
{
    ValueTask<IReadOnlyList<RemoteSummary>> ListAsync(CancellationToken cancellationToken);
    ValueTask<StoredRemote?> ReadAsync(RemoteId id, CancellationToken cancellationToken);
    ValueTask<StoredRemote> UpsertAsync(StoredRemote remote, ulong expectedRevision, CancellationToken cancellationToken);
    ValueTask<bool> DeleteAsync(RemoteId id, ulong expectedRevision, CancellationToken cancellationToken);
}

public interface IRemoteDependencyPolicy
{
    ValueTask<ImmutableArray<RemoteDependency>> FindAsync(RemoteId remoteId, CancellationToken cancellationToken);
    ValueTask ApplyDeleteChoiceAsync(RemoteDeleteRequest request, ImmutableArray<RemoteDependency> dependencies, CancellationToken cancellationToken);
}
