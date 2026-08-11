using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace RcloneUI.DataRoot;

internal sealed class VaultDatabase : IDisposable
{
    private const int ApplicationId = 0x52435549;
    private readonly SqliteConnection connection;
    private readonly Guid vaultId;
    private readonly ulong keyGeneration;
    private readonly byte[] vaultKey;

    private VaultDatabase(SqliteConnection connection, Guid vaultId, ulong keyGeneration, byte[] vaultKey)
    {
        this.connection = connection;
        this.vaultId = vaultId;
        this.keyGeneration = keyGeneration;
        this.vaultKey = vaultKey;
    }

    internal static VaultDatabase Create(string path, Guid dataRootId, Guid vaultId, ulong generation, byte[] vaultKey, int schemaVersion = 1)
    {
        if (schemaVersion is < 1 or > 2) throw new VaultFormatException("database-schema-unsupported");
        var connection = OpenConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA application_id={ApplicationId};
            PRAGMA user_version={schemaVersion};
            CREATE TABLE metadata(key TEXT PRIMARY KEY, value ANY NOT NULL) STRICT;
            CREATE TABLE encrypted_records(
                record_id BLOB PRIMARY KEY CHECK(length(record_id)=16),
                record_type INTEGER NOT NULL,
                schema_version INTEGER NOT NULL,
                key_generation INTEGER NOT NULL,
                nonce BLOB NOT NULL CHECK(length(nonce)=12),
                ciphertext BLOB NOT NULL CHECK(length(ciphertext)<=8388608),
                tag BLOB NOT NULL CHECK(length(tag)=16),
                revision INTEGER NOT NULL,
                UNIQUE(key_generation, nonce)
            ) STRICT;
            INSERT INTO metadata(key,value) VALUES
                ('data_root_id', $dataRootId),
                ('vault_id', $vaultId),
                ('generation', $generation),
                ('schema_version', $schemaVersion),
                ('revision', 0),
                ('authenticated_index', $authenticatedIndex);
            """;
        command.Parameters.AddWithValue("$dataRootId", dataRootId.ToByteArray());
        command.Parameters.AddWithValue("$vaultId", vaultId.ToByteArray());
        command.Parameters.AddWithValue("$generation", checked((long)generation));
        command.Parameters.AddWithValue("$schemaVersion", schemaVersion);
        command.Parameters.AddWithValue("$authenticatedIndex", ComputeAuthenticatedIndex(vaultKey, 0, []));
        command.ExecuteNonQuery();
        return new(connection, vaultId, generation, vaultKey);
    }

    internal static VaultDatabase Open(string path, VaultKeyEnvelope envelope, byte[] vaultKey)
    {
        var connection = OpenConnection(path);
        try
        {
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA application_id;";
            if (Convert.ToInt32(check.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != ApplicationId)
            {
                throw new VaultFormatException("sqlite-application-id-invalid");
            }

            VerifyScalarBlob(connection, "data_root_id", envelope.DataRootId.ToByteArray());
            VerifyScalarBlob(connection, "vault_id", envelope.VaultId.ToByteArray());
            using (var schemaCommand = connection.CreateCommand())
            {
                schemaCommand.CommandText = "SELECT CAST(value AS INTEGER) FROM metadata WHERE key='schema_version';";
                var schemaVersion = Convert.ToInt32(schemaCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                if (schemaVersion is < 1 or > 2) throw new VaultFormatException("database-schema-unsupported");
            }
            VerifyIntegrity(connection);
            var database = new VaultDatabase(connection, envelope.VaultId, envelope.Generation, vaultKey);
            database.VerifyAuthenticatedIndex();
            database.VerifyAllRecords();
            return database;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal ulong ReadRevision()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(value AS INTEGER) FROM metadata WHERE key='revision';";
        return checked((ulong)(long)command.ExecuteScalar()!);
    }

    internal ulong Upsert(UpsertVaultRecord record, ulong expectedRevision)
    {
        if (record.Plaintext.Length > 8 * 1024 * 1024 || record.SchemaVersion <= 0 || record.RecordId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(record));
        }

        using var transaction = connection.BeginTransaction();
        var current = ReadRevision(transaction);
        if (current != expectedRevision) return ulong.MaxValue;
        var revision = checked(current + 1);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[record.Plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(vaultKey, tag.Length))
        {
            aes.Encrypt(nonce, record.Plaintext.Span, ciphertext, tag, BuildAssociatedData(record.RecordId, record.RecordType, record.SchemaVersion, revision));
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO encrypted_records(record_id,record_type,schema_version,key_generation,nonce,ciphertext,tag,revision)
            VALUES($id,$type,$schema,$keyGeneration,$nonce,$ciphertext,$tag,$revision)
            ON CONFLICT(record_id) DO UPDATE SET
                record_type=excluded.record_type,
                schema_version=excluded.schema_version,
                key_generation=excluded.key_generation,
                nonce=excluded.nonce,
                ciphertext=excluded.ciphertext,
                tag=excluded.tag,
                revision=excluded.revision;
            UPDATE metadata SET value=$revision WHERE key='revision';
            """;
        command.Parameters.AddWithValue("$id", record.RecordId.ToByteArray());
        command.Parameters.AddWithValue("$type", (int)record.RecordType);
        command.Parameters.AddWithValue("$schema", record.SchemaVersion);
        command.Parameters.AddWithValue("$keyGeneration", checked((long)keyGeneration));
        command.Parameters.AddWithValue("$nonce", nonce);
        command.Parameters.AddWithValue("$ciphertext", ciphertext);
        command.Parameters.AddWithValue("$tag", tag);
        command.Parameters.AddWithValue("$revision", checked((long)revision));
        command.ExecuteNonQuery();
        UpdateAuthenticatedIndex(transaction, revision);
        transaction.Commit();
        return revision;
    }

    internal ulong Delete(Guid recordId, ulong expectedRevision)
    {
        using var transaction = connection.BeginTransaction();
        var current = ReadRevision(transaction);
        if (current != expectedRevision) return ulong.MaxValue;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM encrypted_records WHERE record_id=$id;";
        command.Parameters.AddWithValue("$id", recordId.ToByteArray());
        if (command.ExecuteNonQuery() == 0) return 0;
        var revision = checked(current + 1);
        command.CommandText = "UPDATE metadata SET value=$revision WHERE key='revision';";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$revision", checked((long)revision));
        command.ExecuteNonQuery();
        UpdateAuthenticatedIndex(transaction, revision);
        transaction.Commit();
        return revision;
    }

    internal VaultRecord? Read(Guid recordId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT record_type,schema_version,key_generation,nonce,ciphertext,tag,revision FROM encrypted_records WHERE record_id=$id;";
        command.Parameters.AddWithValue("$id", recordId.ToByteArray());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var type = (VaultRecordType)reader.GetInt32(0);
        var schema = reader.GetInt32(1);
        var storedKeyGeneration = checked((ulong)reader.GetInt64(2));
        if (storedKeyGeneration != keyGeneration) throw new VaultFormatException("record-key-generation-invalid");
        var nonce = (byte[])reader[3];
        var ciphertext = (byte[])reader[4];
        var tag = (byte[])reader[5];
        var revision = checked((ulong)reader.GetInt64(6));
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(vaultKey, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, BuildAssociatedData(recordId, type, schema, revision));
            return new(recordId, type, schema, revision, plaintext);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    internal int ReadSchemaVersion()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(value AS INTEGER) FROM metadata WHERE key='schema_version';";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    internal List<VaultRecord> ReadAll()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT record_id FROM encrypted_records ORDER BY record_id;";
        using var reader = command.ExecuteReader();
        var identifiers = new List<Guid>();
        while (reader.Read()) identifiers.Add(new Guid((byte[])reader[0]));
        reader.Close();
        return identifiers.Select(identifier => Read(identifier)!).ToList();
    }

    internal void SetRevision(ulong newRevision)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE metadata SET value=$revision WHERE key='revision';";
        command.Parameters.AddWithValue("$revision", checked((long)newRevision));
        command.ExecuteNonQuery();
        UpdateAuthenticatedIndex(transaction, newRevision);
        transaction.Commit();
    }

    internal void BackupTo(string destinationPath)
    {
        var destinationString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
        using var destination = new SqliteConnection(destinationString);
        destination.Open();
        connection.BackupDatabase(destination);
    }

    public void Dispose()
    {
        connection.Dispose();
        CryptographicOperations.ZeroMemory(vaultKey);
    }

    private static SqliteConnection OpenConnection(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=EXTRA; PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF; PRAGMA busy_timeout=5000; PRAGMA mmap_size=0;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static ulong ReadRevision(SqliteTransaction transaction)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CAST(value AS INTEGER) FROM metadata WHERE key='revision';";
        return checked((ulong)(long)command.ExecuteScalar()!);
    }

    private byte[] BuildAssociatedData(Guid recordId, VaultRecordType type, int schemaVersion, ulong revision)
    {
        var result = new byte[16 + 16 + sizeof(int) + sizeof(int) + sizeof(ulong) + sizeof(ulong)];
        vaultId.TryWriteBytes(result);
        recordId.TryWriteBytes(result.AsSpan(16));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(32), (int)type);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(36), schemaVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(40), keyGeneration);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(48), revision);
        return result;
    }

    private static void VerifyScalarBlob(SqliteConnection connection, string key, byte[] expected)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key);
        if (command.ExecuteScalar() is not byte[] actual || !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new VaultFormatException($"metadata-{key}-invalid");
        }
    }

    private static void VerifyIntegrity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals((string?)command.ExecuteScalar(), "ok", StringComparison.Ordinal))
        {
            throw new VaultFormatException("sqlite-integrity-failed");
        }

        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        if (reader.Read()) throw new VaultFormatException("sqlite-foreign-key-failed");
    }

    private void UpdateAuthenticatedIndex(SqliteTransaction transaction, ulong revision)
    {
        var entries = ReadIndexEntries(transaction);
        var authentication = ComputeAuthenticatedIndex(vaultKey, revision, entries);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE metadata SET value=$value WHERE key='authenticated_index';";
        command.Parameters.AddWithValue("$value", authentication);
        command.ExecuteNonQuery();
    }

    private void VerifyAuthenticatedIndex()
    {
        var revision = ReadRevision();
        var entries = ReadIndexEntries(null);
        var expected = ComputeAuthenticatedIndex(vaultKey, revision, entries);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key='authenticated_index';";
        if (command.ExecuteScalar() is not byte[] actual || !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new VaultFormatException("authenticated-index-invalid");
        }
    }

    private List<IndexEntry> ReadIndexEntries(SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT record_id,revision,key_generation,nonce,tag FROM encrypted_records ORDER BY record_id;";
        using var reader = command.ExecuteReader();
        var entries = new List<IndexEntry>();
        while (reader.Read())
        {
            entries.Add(new(
                (byte[])reader[0],
                checked((ulong)reader.GetInt64(1)),
                checked((ulong)reader.GetInt64(2)),
                (byte[])reader[3],
                (byte[])reader[4]));
        }

        return entries;
    }

    private static byte[] ComputeAuthenticatedIndex(byte[] key, ulong revision, List<IndexEntry> entries)
    {
        const int entrySize = 16 + sizeof(ulong) + sizeof(ulong) + 12 + 16;
        var input = new byte[sizeof(ulong) + sizeof(int) + (entries.Count * entrySize)];
        BinaryPrimitives.WriteUInt64LittleEndian(input, revision);
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(sizeof(ulong)), entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var destination = input.AsSpan(sizeof(ulong) + sizeof(int) + (index * entrySize), entrySize);
            entries[index].RecordId.CopyTo(destination);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], entries[index].Revision);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], entries[index].KeyGeneration);
            entries[index].Nonce.CopyTo(destination[32..]);
            entries[index].Tag.CopyTo(destination[44..]);
        }

        return HMACSHA256.HashData(key, input);
    }

    private void VerifyAllRecords()
    {
        foreach (var record in ReadAll())
        {
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(record.Plaintext, out var segment) && segment.Array is not null)
            {
                CryptographicOperations.ZeroMemory(segment.Array.AsSpan(segment.Offset, segment.Count));
            }
        }
    }

    private sealed record IndexEntry(byte[] RecordId, ulong Revision, ulong KeyGeneration, byte[] Nonce, byte[] Tag);
}
