using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace RcloneUI.Remotes;

public sealed class RemoteCatalog(IRemoteEngine engine, IRemoteStore store, IRemoteDependencyPolicy dependencies) : IRemoteCatalog
{
    private static readonly Dictionary<string, (string Name, string Description)> Curated =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["onedrive"] = ("Microsoft OneDrive", "Microsoft cloud storage"),
            ["drive"] = ("Google Drive", "Google cloud storage"),
            ["dropbox"] = ("Dropbox", "Dropbox cloud storage"),
            ["s3"] = ("Amazon S3 / S3-compatible", "Object storage using the S3 API"),
            ["sftp"] = ("SFTP", "Secure file transfer over SSH"),
            ["webdav"] = ("WebDAV", "WebDAV-compatible storage"),
        };
    private readonly ConcurrentDictionary<Guid, SetupSession> sessions = new();
    private readonly ConcurrentDictionary<Guid, ImportSession> imports = new();

    public async ValueTask<ProviderCatalog> DescribeProvidersAsync(CancellationToken cancellationToken = default)
    {
        var live = await engine.DescribeProvidersAsync(cancellationToken).ConfigureAwait(false);
        var providers = live.Providers.Select(provider =>
            Curated.TryGetValue(provider.BackendType, out var presentation)
                ? provider with { DisplayName = presentation.Name, Description = presentation.Description, Curated = true }
                : provider with { Curated = false }).ToImmutableArray();
        return live with { Providers = providers };
    }

    public async ValueTask<SetupSnapshot> BeginSetupAsync(string providerType, string displayName, CancellationToken cancellationToken = default)
    {
        ValidateDisplayName(displayName);
        var catalog = await DescribeProvidersAsync(cancellationToken).ConfigureAwait(false);
        if (!catalog.Providers.Any(provider => provider.BackendType == providerType)) throw new ArgumentException("The provider is not in the live rclone schema.", nameof(providerType));
        var progress = await engine.BeginConfigurationAsync(providerType, cancellationToken).ConfigureAwait(false);
        var session = new SetupSession(Guid.NewGuid(), providerType, displayName, progress, null, 0);
        if (!sessions.TryAdd(session.Id, session)) throw new InvalidOperationException("Could not allocate a setup session.");
        return session.Snapshot();
    }

    public async ValueTask<SetupSnapshot> BeginRepairAsync(RemoteId remoteId, CancellationToken cancellationToken = default)
    {
        var remote = await RequireRemoteAsync(remoteId, cancellationToken).ConfigureAwait(false);
        var progress = await engine.BeginRepairAsync(remote.ProviderType, remote.Configuration, cancellationToken).ConfigureAwait(false);
        var session = new SetupSession(Guid.NewGuid(), remote.ProviderType, remote.DisplayName, progress, remote.Id, remote.Revision);
        if (!sessions.TryAdd(session.Id, session)) throw new InvalidOperationException("Could not allocate a repair session.");
        return session.Snapshot();
    }

    public SetupSnapshot ObserveSetup(Guid setupId) => GetSession(setupId).Snapshot();

    public void CancelSetup(Guid setupId)
    {
        if (!sessions.TryRemove(setupId, out var session)) return;
        session.ClearSecrets();
        session.Gate.Dispose();
    }

    public async ValueTask<SetupSnapshot> AdvanceSetupAsync(Guid setupId, RemoteAnswer answer, CancellationToken cancellationToken = default)
    {
        var session = GetSession(setupId);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.Progress.Completed) return session.Snapshot();
            var question = session.Progress.Question ?? throw new InvalidOperationException("The setup has no pending question.");
            if (!StringComparer.Ordinal.Equals(question.Option.Name, answer.Name)) throw new InvalidOperationException("The answer does not match the pending live-schema question.");
            if (question.Option.Kind == RemoteQuestionKind.OAuth && !StringComparer.Ordinal.Equals(session.AuthorizationState, answer.AuthorizationState))
                throw new InvalidOperationException("The OAuth callback state did not match this setup session.");
            session.Progress = await engine.ContinueConfigurationAsync(session.ProviderType, session.Progress.State, answer, session.Progress.Configuration, cancellationToken).ConfigureAwait(false);
            session.AuthorizationState = session.Progress.Question?.Option.Kind == RemoteQuestionKind.OAuth ? Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) : null;
            session.Tested = null;
            return session.Snapshot();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            session.Failure = RemoteFailureFactory.Create("authorization", RemoteHealthKind.InvalidOptions, exception);
            return session.Snapshot();
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async ValueTask<RemoteHealth> TestSetupAsync(Guid setupId, CancellationToken cancellationToken = default)
    {
        var session = GetSession(setupId);
        if (!session.Progress.Completed) return Failure(RemoteHealthKind.InvalidOptions, "configuration", "setup-incomplete");
        var tested = await TestConfigurationAsync(session.ProviderType, session.Progress.Configuration, cancellationToken).ConfigureAwait(false);
        session.Tested = tested;
        return tested;
    }

    public async ValueTask<RemoteSummary> SaveSetupAsync(Guid setupId, CancellationToken cancellationToken = default)
    {
        var session = GetSession(setupId);
        var health = session.Tested;
        if (health?.Kind != RemoteHealthKind.Healthy) throw new InvalidOperationException("The Remote must pass Test and Save before credentials are persisted.");
        var existing = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Any(remote => remote.Id != session.ExistingRemoteId && StringComparer.OrdinalIgnoreCase.Equals(remote.DisplayName, session.DisplayName)))
            throw new InvalidOperationException("A Remote already uses this display name.");
        var stored = new StoredRemote(session.ExistingRemoteId ?? RemoteId.New(), session.DisplayName, session.ProviderType, session.ExistingRevision, new Dictionary<string, string>(session.Progress.Configuration, StringComparer.Ordinal), health);
        var saved = await store.UpsertAsync(stored, session.ExistingRevision, cancellationToken).ConfigureAwait(false);
        sessions.TryRemove(setupId, out _);
        session.ClearSecrets();
        session.Gate.Dispose();
        return ToSummary(saved);
    }

    public async ValueTask<RemoteHealth> TestAsync(RemoteId remoteId, CancellationToken cancellationToken = default)
    {
        var remote = await RequireRemoteAsync(remoteId, cancellationToken).ConfigureAwait(false);
        return await TestConfigurationAsync(remote.ProviderType, remote.Configuration, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<BrowsePage> BrowseAsync(RemoteLocation location, PageRequest page, CancellationToken cancellationToken = default)
    {
        if (page.PageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(page));
        var remote = await RequireRemoteAsync(location.RemoteId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await engine.BrowseAsync(remote.ProviderType, remote.Configuration, location.Path, page, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new RemoteOperationException(RemoteFailureFactory.Create("root-listing", RemoteHealthKind.NetworkUnavailable, exception));
        }
    }

    public async ValueTask<RemoteDeleteResult> DeleteAsync(RemoteDeleteRequest request, CancellationToken cancellationToken = default)
    {
        var current = await store.ReadAsync(request.RemoteId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Revision != request.ExpectedRevision)
            return new(false, [], "revision-conflict-or-missing");
        var linked = await dependencies.FindAsync(request.RemoteId, cancellationToken).ConfigureAwait(false);
        if (linked.Any(item => item.Active)) return new(false, linked, "active-dependency");
        if (linked.Length > 0 && request.Choice == DeleteDependencyChoice.Cancel) return new(false, linked, "dependency-choice-required");
        await dependencies.ApplyDeleteChoiceAsync(request, linked, cancellationToken).ConfigureAwait(false);
        var deleted = await store.DeleteAsync(request.RemoteId, request.ExpectedRevision, cancellationToken).ConfigureAwait(false);
        return new(deleted, linked, deleted ? null : "revision-conflict-or-missing");
    }

    public async ValueTask<RemoteImportPreview> PreviewImportAsync(ReadOnlyMemory<byte> rcloneConfiguration, CancellationToken cancellationToken = default)
    {
        if (rcloneConfiguration.Length is 0 or > 8 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(rcloneConfiguration));
        var bytes = rcloneConfiguration.ToArray();
        try
        {
            var preview = await engine.PreviewImportAsync(bytes, cancellationToken).ConfigureAwait(false);
            var id = Guid.NewGuid();
            imports[id] = new(bytes, preview.Candidates);
            return preview with { ImportId = id };
        }
        catch
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    public async ValueTask<RemoteImportResult> ApplyImportAsync(Guid importId, ImmutableArray<RemoteImportSelection> selections, CancellationToken cancellationToken = default)
    {
        if (!imports.TryRemove(importId, out var session)) throw new KeyNotFoundException("Import preview expired.");
        var imported = ImmutableArray.CreateBuilder<RemoteSummary>();
        var skipped = ImmutableArray.CreateBuilder<string>();
        try
        {
            foreach (var selection in selections)
            {
                var candidate = session.Candidates.SingleOrDefault(item => item.SourceName == selection.SourceName) ?? throw new InvalidOperationException("Import selection was not previewed.");
                if (!candidate.Supported || selection.Choice == ImportConflictChoice.Skip) { skipped.Add(candidate.SourceName); continue; }
                var materialized = await engine.MaterializeImportAsync(session.Bytes, candidate.SourceName, cancellationToken).ConfigureAwait(false);
                var existing = (await store.ListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.DisplayName, candidate.ConflictsWith));
                var displayName = selection.Choice == ImportConflictChoice.Rename ? selection.NewDisplayName : candidate.SourceName;
                ValidateDisplayName(displayName ?? string.Empty);
                if (candidate.ConflictsWith is not null && selection.Choice == ImportConflictChoice.Replace && existing is null) throw new InvalidOperationException("The import conflict changed.");
                var test = await TestConfigurationAsync(materialized.ProviderType, materialized.Configuration, cancellationToken).ConfigureAwait(false);
                if (test.Kind != RemoteHealthKind.Healthy) { skipped.Add(candidate.SourceName); continue; }
                var remote = new StoredRemote(existing?.Id ?? RemoteId.New(), displayName!, materialized.ProviderType, existing?.Revision ?? 0, materialized.Configuration, test);
                imported.Add(ToSummary(await store.UpsertAsync(remote, remote.Revision, cancellationToken).ConfigureAwait(false)));
            }
            return new(imported.ToImmutable(), skipped.ToImmutable());
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(session.Bytes);
        }
    }

    private async ValueTask<RemoteHealth> TestConfigurationAsync(string providerType, IReadOnlyDictionary<string, string> configuration, CancellationToken cancellationToken)
    {
        try
        {
            var result = await engine.TestAsync(providerType, configuration, cancellationToken).ConfigureAwait(false);
            return result.Success
                ? new(RemoteHealthKind.Healthy, DateTimeOffset.UtcNow, result.CapabilityBinding, null)
                : Failure(result.FailureKind, "connection", result.DiagnosticCode ?? "remote-test-failed");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(RemoteHealthKind.NetworkUnavailable, DateTimeOffset.UtcNow, null, RemoteFailureFactory.Create("connection", RemoteHealthKind.NetworkUnavailable, exception));
        }
    }

    private static RemoteHealth Failure(RemoteHealthKind kind, string stage, string code) =>
        new(kind, DateTimeOffset.UtcNow, null, new(kind, stage, RemoteFailureFactory.Recovery(kind), RemoteFailureFactory.RedactCode(code)));

    private async ValueTask<StoredRemote> RequireRemoteAsync(RemoteId id, CancellationToken cancellationToken) =>
        await store.ReadAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("Remote not found.");

    private SetupSession GetSession(Guid id) => sessions.GetValueOrDefault(id) ?? throw new KeyNotFoundException("Setup session not found or already completed.");
    private static RemoteSummary ToSummary(StoredRemote remote) => new(remote.Id, remote.DisplayName, remote.ProviderType, remote.Revision, remote.Health);

    private static void ValidateDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || value.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException("Display name is invalid.", nameof(value));
    }

    private sealed class SetupSession(Guid id, string providerType, string displayName, RemoteEngineProgress progress, RemoteId? existingRemoteId, ulong existingRevision)
    {
        internal Guid Id { get; } = id;
        internal string ProviderType { get; } = providerType;
        internal string DisplayName { get; } = displayName;
        internal RemoteEngineProgress Progress { get; set; } = progress;
        internal RemoteId? ExistingRemoteId { get; } = existingRemoteId;
        internal ulong ExistingRevision { get; } = existingRevision;
        internal string? AuthorizationState { get; set; } = progress.Question?.Option.Kind == RemoteQuestionKind.OAuth ? Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) : null;
        internal RemoteHealth? Tested { get; set; }
        internal RemoteFailure? Failure { get; set; }
        internal SemaphoreSlim Gate { get; } = new(1, 1);

        internal SetupSnapshot Snapshot() => new(Id, Progress.Completed ? RemoteSetupStage.TestAndSave : RemoteSetupStage.Configure, ProviderType, DisplayName, Progress.Question is null ? null : new(Progress.Question.State, Progress.Question.Option, Progress.Question.ErrorCode), AuthorizationState, Failure);
        internal void ClearSecrets() => Progress = Progress with { Configuration = new Dictionary<string, string>() };
    }

    private sealed record ImportSession(byte[] Bytes, ImmutableArray<RemoteImportCandidate> Candidates);
}

public sealed class RemoteOperationException(RemoteFailure failure) : Exception(failure.RecoveryAction)
{
    public RemoteFailure Failure { get; } = failure;
}

internal static class RemoteFailureFactory
{
    internal static RemoteFailure Create(string stage, RemoteHealthKind kind, Exception exception) =>
        new(kind, stage, Recovery(kind), RedactCode(exception.GetType().Name));

    internal static string RedactCode(string value)
    {
        var safe = new string(value.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').Take(64).ToArray());
        return string.IsNullOrEmpty(safe) ? "remote-error" : safe.ToLowerInvariant();
    }

    internal static string Recovery(RemoteHealthKind kind) => kind switch
    {
        RemoteHealthKind.AuthenticationFailed => "Sign in again and retest the Remote.",
        RemoteHealthKind.PermissionDenied => "Check the account and folder permissions, then retry.",
        RemoteHealthKind.Throttled => "Wait for the provider limit to reset, then retry.",
        RemoteHealthKind.InvalidOptions => "Review the highlighted provider options.",
        RemoteHealthKind.UnsupportedCapability => "Choose an operation supported by this Remote.",
        _ => "Check the network connection and retry.",
    };
}
