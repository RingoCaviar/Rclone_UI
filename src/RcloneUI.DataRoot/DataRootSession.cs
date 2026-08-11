using System.Formats.Cbor;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RcloneUI.Contracts;

namespace RcloneUI.DataRoot;

public sealed class DataRootSession : IDataRootSession
{
    private const int SnapshotRetention = 3;
    private readonly WindowsDataRootAdmission admission;
    private readonly IVaultKeyDeriver keyDeriver;
    private readonly LibArgon2KeyDeriver? ownedKeyDeriver;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string generationDirectory;
    private VaultKeyEnvelope envelope;
    private VaultDatabase? database;
    private DataRootSessionState state;
    private ulong revision;
    private bool disposed;

    private DataRootSession(
        WindowsDataRootAdmission admission,
        IVaultKeyDeriver keyDeriver,
        LibArgon2KeyDeriver? ownedKeyDeriver,
        string generationDirectory,
        VaultKeyEnvelope envelope,
        VaultDatabase database)
    {
        this.admission = admission;
        this.keyDeriver = keyDeriver;
        this.ownedKeyDeriver = ownedKeyDeriver;
        this.generationDirectory = generationDirectory;
        this.envelope = envelope;
        this.database = database;
        revision = database.ReadRevision();
        state = DataRootSessionState.Unlocked;
    }

    public static ValueTask<DataRootOpenResult> OpenAsync(
        DataRootOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Argon2 is null)
        {
            return ValueTask.FromResult(new DataRootOpenResult(
                DataRootOpenStatus.Unavailable,
                null,
                null,
                "verified-libargon2-binding-required"));
        }

        LibArgon2KeyDeriver keyDeriver;
        try
        {
            keyDeriver = new(request.Argon2);
        }
        catch (Exception exception) when (exception is IOException or CryptographicException or FormatException or BadImageFormatException or DllNotFoundException or EntryPointNotFoundException)
        {
            return ValueTask.FromResult(new DataRootOpenResult(
                DataRootOpenStatus.Unavailable,
                null,
                null,
                "libargon2-verification-failed"));
        }

        return OpenCoreAsync(request, keyDeriver, keyDeriver, cancellationToken);
    }

    internal static ValueTask<DataRootOpenResult> OpenForTestingAsync(
        DataRootOpenRequest request,
        IVaultKeyDeriver keyDeriver,
        CancellationToken cancellationToken = default) =>
        OpenCoreAsync(request, keyDeriver, null, cancellationToken);

    public DataRootSnapshot Observe() => new(
        new DataRootId(envelope.DataRootId),
        envelope.VaultId,
        envelope.Generation,
        revision,
        state,
        admission.CanonicalPath);

    public async ValueTask<DataRootCommandResult> ExecuteAsync(
        DataRootCommand command,
        ulong expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state == DataRootSessionState.Locked) return new(DataRootCommandStatus.Locked, revision);
            if (state != DataRootSessionState.Unlocked || database is null) return new(DataRootCommandStatus.Unavailable, revision);
            if (revision != expectedRevision) return new(DataRootCommandStatus.RevisionConflict, revision);
            admission.VerifyIdentity();
            CreateDailySnapshotIfNeeded();
            var result = command switch
            {
                UpsertVaultRecord upsert => database.Upsert(upsert, expectedRevision),
                DeleteVaultRecord delete => database.Delete(delete.RecordId, expectedRevision),
                MigrateVault migration => Migrate(migration),
                _ => throw new ArgumentException("Unknown Data Root command.", nameof(command)),
            };
            if (result == ulong.MaxValue) return new(DataRootCommandStatus.RevisionConflict, revision);
            if (result == ulong.MaxValue - 1) return new(DataRootCommandStatus.AuthenticationFailed, revision);
            if (result == ulong.MaxValue - 2) return new(DataRootCommandStatus.Unsupported, revision);
            if (result == 0 && command is DeleteVaultRecord) return new(DataRootCommandStatus.NotFound, revision);
            revision = result;
            return new(DataRootCommandStatus.Applied, revision);
        }
        catch (DataRootAdmissionException)
        {
            state = DataRootSessionState.Unavailable;
            database?.Dispose();
            database = null;
            return new(DataRootCommandStatus.Unavailable, revision);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<VaultRecord?> ReadAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return state == DataRootSessionState.Unlocked ? database?.Read(recordId) : null;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Lock()
    {
        gate.Wait();
        try
        {
            if (state != DataRootSessionState.Unlocked) return;
            database?.Dispose();
            database = null;
            state = DataRootSessionState.Locked;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<bool> UnlockAsync(ReadOnlyMemory<byte> masterPasswordUtf8, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state == DataRootSessionState.Unlocked) return true;
            if (state != DataRootSessionState.Locked) return false;
            byte[] vaultKey;
            try
            {
                vaultKey = VaultEnvelopeCodec.Unwrap(envelope, masterPasswordUtf8.Span, keyDeriver);
            }
            catch (AuthenticationTagMismatchException)
            {
                return false;
            }

            try
            {
                VerifyManifest(generationDirectory, envelope, vaultKey);
                database = VaultDatabase.Open(Path.Combine(generationDirectory, "vault.db"), envelope, vaultKey);
                revision = database.ReadRevision();
                state = DataRootSessionState.Unlocked;
                return true;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(vaultKey);
                state = DataRootSessionState.ReadOnlyRecovery;
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask CloseAsync(string reason, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state == DataRootSessionState.Closed) return;
            WriteLifecycleJournal(admission.CanonicalPath, "closed", envelope.Generation, reason);
            database?.Dispose();
            database = null;
            state = DataRootSessionState.Closed;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await CloseAsync("disposed").ConfigureAwait(false);
        disposed = true;
        gate.Dispose();
        admission.Dispose();
        ownedKeyDeriver?.Dispose();
        GC.SuppressFinalize(this);
    }

    internal void CreateSnapshotForTesting()
    {
        gate.Wait();
        try
        {
            if (state != DataRootSessionState.Unlocked || database is null) throw new InvalidOperationException("Vault must be unlocked.");
            CreateSnapshot(DateTimeOffset.UtcNow);
        }
        finally
        {
            gate.Release();
        }
    }

    private static ValueTask<DataRootOpenResult> OpenCoreAsync(
        DataRootOpenRequest request,
        IVaultKeyDeriver keyDeriver,
        LibArgon2KeyDeriver? ownedKeyDeriver,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsDataRootAdmission? admission = null;
        try
        {
            admission = WindowsDataRootAdmission.Acquire(request.Path);
            var vaultDirectory = Path.Combine(admission.CanonicalPath, "vault");
            var selectorPath = Path.Combine(vaultDirectory, "CURRENT");
            if (!File.Exists(selectorPath))
            {
                if (request.Mode != DataRootOpenMode.CreateIfMissing)
                {
                    admission.Dispose();
                    ownedKeyDeriver?.Dispose();
                    return ValueTask.FromResult(new DataRootOpenResult(DataRootOpenStatus.NeedsRecovery, null, null, "selector-missing"));
                }

                return ValueTask.FromResult(CreateNew(request, admission, keyDeriver, ownedKeyDeriver));
            }

            return ValueTask.FromResult(OpenExisting(request, admission, keyDeriver, ownedKeyDeriver));
        }
        catch (DataRootAdmissionException exception)
        {
            admission?.Dispose();
            ownedKeyDeriver?.Dispose();
            return ValueTask.FromResult(new DataRootOpenResult(exception.Status, null, null, exception.Code));
        }
        catch (AuthenticationTagMismatchException)
        {
            admission?.Dispose();
            ownedKeyDeriver?.Dispose();
            return ValueTask.FromResult(new DataRootOpenResult(DataRootOpenStatus.AuthenticationFailed, null, null, "vault-unlock-failed"));
        }
        catch (VaultFormatException exception)
        {
            admission?.Dispose();
            ownedKeyDeriver?.Dispose();
            return ValueTask.FromResult(new DataRootOpenResult(DataRootOpenStatus.NeedsRecovery, null, null, exception.Code));
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            admission?.Dispose();
            ownedKeyDeriver?.Dispose();
            return ValueTask.FromResult(new DataRootOpenResult(DataRootOpenStatus.NeedsRecovery, null, null, $"sqlite-{exception.SqliteErrorCode}"));
        }
        catch (Exception exception) when (exception is IOException or CryptographicException)
        {
            admission?.Dispose();
            ownedKeyDeriver?.Dispose();
            return ValueTask.FromResult(new DataRootOpenResult(DataRootOpenStatus.NeedsRecovery, null, null, "vault-io-or-cryptographic-failure"));
        }
    }

    private static DataRootOpenResult CreateNew(
        DataRootOpenRequest request,
        WindowsDataRootAdmission admission,
        IVaultKeyDeriver keyDeriver,
        LibArgon2KeyDeriver? ownedKeyDeriver)
    {
        var vaultDirectory = Path.Combine(admission.CanonicalPath, "vault");
        var generations = Path.Combine(vaultDirectory, "generations");
        Directory.CreateDirectory(generations);
        if (Directory.EnumerateFileSystemEntries(generations).Any())
        {
            throw new VaultFormatException("unselected-generation-present");
        }

        const ulong generation = 1;
        var dataRootId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var staging = Path.Combine(generations, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        WriteLifecycleJournal(admission.CanonicalPath, "preparing", generation, "create");
        var vaultKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var envelope = VaultEnvelopeCodec.Create(dataRootId, vaultId, generation, request.MasterPasswordUtf8.Span, keyDeriver, vaultKey);
            WriteDurable(Path.Combine(staging, "key-envelope.cbor"), VaultEnvelopeCodec.Encode(envelope));
            using (VaultDatabase.Create(Path.Combine(staging, "vault.db"), dataRootId, vaultId, generation, vaultKey.ToArray())) { }
            WriteManifest(staging, envelope, vaultKey);
            var finalDirectory = Path.Combine(generations, generation.ToString("D20", CultureInfo.InvariantCulture));
            Directory.Move(staging, finalDirectory);
            WriteSelector(vaultDirectory, generation);
            admission.VerifyIdentity();
            WriteLifecycleJournal(admission.CanonicalPath, "committed", generation, "create");
            var database = VaultDatabase.Open(Path.Combine(finalDirectory, "vault.db"), envelope, vaultKey.ToArray());
            var session = new DataRootSession(admission, keyDeriver, ownedKeyDeriver, finalDirectory, envelope, database);
            return new(DataRootOpenStatus.Opened, session, session.Observe());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(vaultKey);
        }
    }

    private static DataRootOpenResult OpenExisting(
        DataRootOpenRequest request,
        WindowsDataRootAdmission admission,
        IVaultKeyDeriver keyDeriver,
        LibArgon2KeyDeriver? ownedKeyDeriver)
    {
        var vaultDirectory = Path.Combine(admission.CanonicalPath, "vault");
        var generation = ReadSelector(Path.Combine(vaultDirectory, "CURRENT"));
        var directory = Path.Combine(vaultDirectory, "generations", generation.ToString("D20", CultureInfo.InvariantCulture));
        if (!Directory.Exists(directory)) throw new VaultFormatException("selected-generation-missing");
        var envelopeBytes = File.ReadAllBytes(Path.Combine(directory, "key-envelope.cbor"));
        var envelope = VaultEnvelopeCodec.Decode(envelopeBytes);
        if (envelope.Generation != generation) throw new VaultFormatException("generation-identity-mismatch");
        var vaultKey = VaultEnvelopeCodec.Unwrap(envelope, request.MasterPasswordUtf8.Span, keyDeriver);
        try
        {
            VerifyManifest(directory, envelope, vaultKey);
            var database = VaultDatabase.Open(Path.Combine(directory, "vault.db"), envelope, vaultKey.ToArray());
            var session = new DataRootSession(admission, keyDeriver, ownedKeyDeriver, directory, envelope, database);
            return new(DataRootOpenStatus.Opened, session, session.Observe());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(vaultKey);
        }
    }

    private static void WriteSelector(string vaultDirectory, ulong generation)
    {
        var generationText = generation.ToString("D20", CultureInfo.InvariantCulture);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(generationText)));
        var bytes = Encoding.ASCII.GetBytes($"{generationText}\n{checksum}\n");
        var temporary = Path.Combine(vaultDirectory, "CURRENT.new");
        WriteDurable(temporary, bytes);
        File.Move(temporary, Path.Combine(vaultDirectory, "CURRENT"), overwrite: true);
    }

    private static ulong ReadSelector(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != 86 || bytes.Any(static value => value > 0x7F)) throw new VaultFormatException("selector-shape-invalid");
        var lines = Encoding.ASCII.GetString(bytes).Split('\n');
        if (lines.Length != 3 || lines[0].Length != 20 || lines[1].Length != 64 || lines[2].Length != 0)
            throw new VaultFormatException("selector-shape-invalid");
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(lines[0])));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(lines[1]), Convert.FromHexString(expected)))
            throw new VaultFormatException("selector-checksum-invalid");
        if (!ulong.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var generation) || generation == 0)
            throw new VaultFormatException("selector-generation-invalid");
        return generation;
    }

    private static void WriteManifest(string directory, VaultKeyEnvelope envelope, byte[] vaultKey)
    {
        var envelopeHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "key-envelope.cbor")));
        var body = new byte[1 + 16 + 16 + sizeof(ulong) + envelopeHash.Length];
        body[0] = 1;
        envelope.DataRootId.TryWriteBytes(body.AsSpan(1));
        envelope.VaultId.TryWriteBytes(body.AsSpan(17));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(33), envelope.Generation);
        envelopeHash.CopyTo(body, 41);
        var tag = HMACSHA256.HashData(vaultKey, body);
        WriteDurable(Path.Combine(directory, "manifest.bin"), [.. body, .. tag]);
    }

    private static void VerifyManifest(string directory, VaultKeyEnvelope envelope, byte[] vaultKey)
    {
        var manifest = File.ReadAllBytes(Path.Combine(directory, "manifest.bin"));
        if (manifest.Length != 105) throw new VaultFormatException("manifest-size-invalid");
        var body = manifest.AsSpan(0, 73);
        var expected = HMACSHA256.HashData(vaultKey, body);
        if (!CryptographicOperations.FixedTimeEquals(expected, manifest.AsSpan(73))) throw new VaultFormatException("manifest-authentication-failed");
        if (body[0] != 1 || new Guid(body.Slice(1, 16)) != envelope.DataRootId || new Guid(body.Slice(17, 16)) != envelope.VaultId
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(33, 8)) != envelope.Generation)
            throw new VaultFormatException("manifest-identity-invalid");
        var actualEnvelopeHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "key-envelope.cbor")));
        if (!CryptographicOperations.FixedTimeEquals(actualEnvelopeHash, body.Slice(41, 32))) throw new VaultFormatException("manifest-member-invalid");
    }

    private static void WriteLifecycleJournal(string dataRoot, string phase, ulong generation, string reason)
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(6);
        writer.WriteInt32(0); writer.WriteInt32(1);
        writer.WriteInt32(1); writer.WriteTextString("vault-session");
        writer.WriteInt32(2); writer.WriteUInt64(generation);
        writer.WriteInt32(3); writer.WriteTextString(phase);
        writer.WriteInt32(4); writer.WriteTextString(reason.Length <= 128 ? reason : reason[..128]);
        writer.WriteInt32(5); writer.WriteInt64(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        writer.WriteEndMap();
        WriteDurable(Path.Combine(dataRoot, "vault", "transaction.cbor"), writer.Encode());
    }

    private static void WriteDurable(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private ulong Migrate(MigrateVault migration)
    {
        var currentSchema = database!.ReadSchemaVersion();
        if (migration.TargetSchemaVersion == currentSchema) return revision;
        if (migration.TargetSchemaVersion != currentSchema + 1 || migration.TargetSchemaVersion > 2)
            return ulong.MaxValue - 2;

        byte[] currentVaultKey;
        try
        {
            currentVaultKey = VaultEnvelopeCodec.Unwrap(envelope, migration.MasterPasswordUtf8.Span, keyDeriver);
        }
        catch (AuthenticationTagMismatchException)
        {
            return ulong.MaxValue - 1;
        }

        var records = database.ReadAll();
        var nextGeneration = checked(envelope.Generation + 1);
        var nextVaultKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            CreateSnapshot(DateTimeOffset.UtcNow);
            var vaultDirectory = Path.Combine(admission.CanonicalPath, "vault");
            var generations = Path.Combine(vaultDirectory, "generations");
            var staging = Path.Combine(generations, $".staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            WriteLifecycleJournal(admission.CanonicalPath, "preparing", nextGeneration, "migration");
            var nextEnvelope = VaultEnvelopeCodec.Create(
                envelope.DataRootId,
                envelope.VaultId,
                nextGeneration,
                migration.MasterPasswordUtf8.Span,
                keyDeriver,
                nextVaultKey);
            WriteDurable(Path.Combine(staging, "key-envelope.cbor"), VaultEnvelopeCodec.Encode(nextEnvelope));
            using (var stagedDatabase = VaultDatabase.Create(
                       Path.Combine(staging, "vault.db"),
                       envelope.DataRootId,
                       envelope.VaultId,
                       nextGeneration,
                       nextVaultKey.ToArray(),
                       migration.TargetSchemaVersion))
            {
                ulong stagedRevision = 0;
                foreach (var record in records)
                {
                    stagedRevision = stagedDatabase.Upsert(
                        new(record.RecordId, record.RecordType, record.SchemaVersion, record.Plaintext),
                        stagedRevision);
                }

                stagedDatabase.SetRevision(checked(revision + 1));
            }

            WriteManifest(staging, nextEnvelope, nextVaultKey);
            var finalDirectory = Path.Combine(generations, nextGeneration.ToString("D20", CultureInfo.InvariantCulture));
            Directory.Move(staging, finalDirectory);
            WriteLifecycleJournal(admission.CanonicalPath, "verified", nextGeneration, "migration");
            admission.VerifyIdentity();
            database.Dispose();
            database = null;
            try
            {
                WriteSelector(vaultDirectory, nextGeneration);
                database = VaultDatabase.Open(Path.Combine(finalDirectory, "vault.db"), nextEnvelope, nextVaultKey.ToArray());
                envelope = nextEnvelope;
                generationDirectory = finalDirectory;
                revision = database.ReadRevision();
                WriteLifecycleJournal(admission.CanonicalPath, "committed", nextGeneration, "migration");
                return revision;
            }
            catch
            {
                WriteSelector(vaultDirectory, envelope.Generation);
                database = VaultDatabase.Open(Path.Combine(generationDirectory, "vault.db"), envelope, currentVaultKey.ToArray());
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentVaultKey);
            CryptographicOperations.ZeroMemory(nextVaultKey);
            foreach (var record in records)
            {
                if (MemoryMarshal.TryGetArray(record.Plaintext, out var segment) && segment.Array is not null)
                    CryptographicOperations.ZeroMemory(segment.Array.AsSpan(segment.Offset, segment.Count));
            }
        }
    }

    private void CreateDailySnapshotIfNeeded()
    {
        var snapshots = Path.Combine(admission.CanonicalPath, "snapshots");
        var prefix = DateTimeOffset.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (Directory.Exists(snapshots) && Directory.GetDirectories(snapshots, $"{prefix}*").Length != 0) return;
        CreateSnapshot(DateTimeOffset.UtcNow);
    }

    private void CreateSnapshot(DateTimeOffset timestamp)
    {
        var snapshots = Path.Combine(admission.CanonicalPath, "snapshots");
        Directory.CreateDirectory(snapshots);
        var staging = Path.Combine(snapshots, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        database!.BackupTo(Path.Combine(staging, "vault.db"));
        File.Copy(Path.Combine(generationDirectory, "key-envelope.cbor"), Path.Combine(staging, "key-envelope.cbor"));
        File.Copy(Path.Combine(generationDirectory, "manifest.bin"), Path.Combine(staging, "manifest.bin"));
        var destination = Path.Combine(snapshots, $"{timestamp:yyyyMMddHHmmssfff}-{envelope.Generation:D20}-{Guid.NewGuid():N}");
        Directory.Move(staging, destination);
        foreach (var stale in Directory.GetDirectories(snapshots).Where(path => !Path.GetFileName(path).StartsWith(".staging-", StringComparison.Ordinal)).OrderDescending().Skip(SnapshotRetention))
        {
            Directory.Delete(stale, recursive: true);
        }
    }
}
