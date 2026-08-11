using RcloneUI.Contracts;

namespace RcloneUI.DataRoot;

public enum DataRootOpenMode
{
    OpenExisting,
    CreateIfMissing,
}

public enum DataRootOpenStatus
{
    Opened,
    AlreadyOwned,
    UnsupportedLocation,
    AuthenticationFailed,
    NeedsRecovery,
    Unavailable,
}

public enum DataRootSessionState
{
    Unlocked,
    Locked,
    ReadOnlyRecovery,
    Unavailable,
    Closed,
}

public enum VaultRecordType
{
    Remote = 1,
    TransferTask = 2,
    MountProfile = 3,
    Schedule = 4,
    Activity = 5,
    Index = 6,
}

public sealed record DataRootOpenRequest(
    string Path,
    DataRootOpenMode Mode,
    ReadOnlyMemory<byte> MasterPasswordUtf8,
    LibArgon2Binding? Argon2 = null);

public sealed record LibArgon2Binding(string AbsoluteLibraryPath, string Sha256Digest);

public sealed record DataRootSnapshot(
    DataRootId DataRootId,
    Guid VaultId,
    ulong Generation,
    ulong Revision,
    DataRootSessionState State,
    string CanonicalPath);

public sealed record DataRootOpenResult(
    DataRootOpenStatus Status,
    IDataRootSession? Session,
    DataRootSnapshot? Snapshot,
    string? DiagnosticCode = null);

public abstract record DataRootCommand;

public sealed record UpsertVaultRecord(
    Guid RecordId,
    VaultRecordType RecordType,
    int SchemaVersion,
    ReadOnlyMemory<byte> Plaintext) : DataRootCommand;

public sealed record DeleteVaultRecord(Guid RecordId) : DataRootCommand;

public sealed record MigrateVault(int TargetSchemaVersion, ReadOnlyMemory<byte> MasterPasswordUtf8) : DataRootCommand;

public enum DataRootCommandStatus
{
    Applied,
    RevisionConflict,
    Locked,
    Unavailable,
    NotFound,
    Unsupported,
    AuthenticationFailed,
    NeedsRecovery,
}

public sealed record DataRootCommandResult(DataRootCommandStatus Status, ulong Revision);

public sealed record VaultRecord(
    Guid RecordId,
    VaultRecordType RecordType,
    int SchemaVersion,
    ulong Revision,
    ReadOnlyMemory<byte> Plaintext);

public interface IDataRootSession : IAsyncDisposable
{
    DataRootSnapshot Observe();

    ValueTask<DataRootCommandResult> ExecuteAsync(
        DataRootCommand command,
        ulong expectedRevision,
        CancellationToken cancellationToken = default);

    ValueTask<VaultRecord?> ReadAsync(Guid recordId, CancellationToken cancellationToken = default);

    void Lock();

    ValueTask<bool> UnlockAsync(ReadOnlyMemory<byte> masterPasswordUtf8, CancellationToken cancellationToken = default);

    ValueTask CloseAsync(string reason, CancellationToken cancellationToken = default);
}
