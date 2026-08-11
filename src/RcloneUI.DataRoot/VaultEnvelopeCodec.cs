using System.Formats.Cbor;
using System.Security.Cryptography;

namespace RcloneUI.DataRoot;

internal sealed record VaultKeyEnvelope(
    Guid DataRootId,
    Guid VaultId,
    ulong Generation,
    Argon2Parameters Parameters,
    byte[] Salt,
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] Tag);

internal static class VaultEnvelopeCodec
{
    internal const int MaximumEnvelopeBytes = 4096;

    internal static VaultKeyEnvelope Create(
        Guid dataRootId,
        Guid vaultId,
        ulong generation,
        ReadOnlySpan<byte> password,
        IVaultKeyDeriver keyDeriver,
        ReadOnlySpan<byte> vaultKey)
    {
        var parameters = Argon2Parameters.Default;
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[32];
        var tag = new byte[16];
        Span<byte> wrappingKey = stackalloc byte[32];
        keyDeriver.Derive(password, salt, parameters, wrappingKey);
        try
        {
            using var aes = new AesGcm(wrappingKey, tag.Length);
            aes.Encrypt(nonce, vaultKey, ciphertext, tag, BuildAssociatedData(dataRootId, vaultId, generation, parameters));
            return new(dataRootId, vaultId, generation, parameters, salt, nonce, ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    internal static byte[] Unwrap(VaultKeyEnvelope envelope, ReadOnlySpan<byte> password, IVaultKeyDeriver keyDeriver)
    {
        Validate(envelope);
        Span<byte> wrappingKey = stackalloc byte[32];
        keyDeriver.Derive(password, envelope.Salt, envelope.Parameters, wrappingKey);
        var vaultKey = new byte[32];
        try
        {
            using var aes = new AesGcm(wrappingKey, envelope.Tag.Length);
            aes.Decrypt(
                envelope.Nonce,
                envelope.Ciphertext,
                envelope.Tag,
                vaultKey,
                BuildAssociatedData(envelope.DataRootId, envelope.VaultId, envelope.Generation, envelope.Parameters));
            return vaultKey;
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(vaultKey);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    internal static byte[] Encode(VaultKeyEnvelope envelope)
    {
        Validate(envelope);
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(12);
        writer.WriteInt32(0); writer.WriteInt32(1);
        writer.WriteInt32(1); writer.WriteByteString(envelope.DataRootId.ToByteArray());
        writer.WriteInt32(2); writer.WriteByteString(envelope.VaultId.ToByteArray());
        writer.WriteInt32(3); writer.WriteUInt64(envelope.Generation);
        writer.WriteInt32(4); writer.WriteTextString("argon2id");
        writer.WriteInt32(5); writer.WriteInt32(0x13);
        writer.WriteInt32(6); writer.WriteInt32(envelope.Parameters.MemoryKiB);
        writer.WriteInt32(7); writer.WriteInt32(envelope.Parameters.Iterations);
        writer.WriteInt32(8); writer.WriteInt32(envelope.Parameters.Lanes);
        writer.WriteInt32(9); writer.WriteByteString(envelope.Salt);
        writer.WriteInt32(10); writer.WriteByteString(envelope.Nonce);
        writer.WriteInt32(11);
        writer.WriteStartArray(2);
        writer.WriteByteString(envelope.Ciphertext);
        writer.WriteByteString(envelope.Tag);
        writer.WriteEndArray();
        writer.WriteEndMap();
        return writer.Encode();
    }

    internal static VaultKeyEnvelope Decode(ReadOnlyMemory<byte> encoded)
    {
        if (encoded.Length is 0 or > MaximumEnvelopeBytes)
        {
            throw new VaultFormatException("key-envelope-size-invalid");
        }

        try
        {
            var reader = new CborReader(encoded, CborConformanceMode.Canonical, allowMultipleRootLevelValues: false);
            if (reader.ReadStartMap() != 12 || reader.ReadInt32() != 0 || reader.ReadInt32() != 1)
            {
                throw new VaultFormatException("key-envelope-format-unsupported");
            }

            RequireKey(reader, 1); var dataRootId = new Guid(ReadBytes(reader, 16));
            RequireKey(reader, 2); var vaultId = new Guid(ReadBytes(reader, 16));
            RequireKey(reader, 3); var generation = reader.ReadUInt64();
            RequireKey(reader, 4); if (reader.ReadTextString() != "argon2id") throw new VaultFormatException("kdf-unsupported");
            RequireKey(reader, 5); if (reader.ReadInt32() != 0x13) throw new VaultFormatException("argon2-version-unsupported");
            RequireKey(reader, 6); var memory = reader.ReadInt32();
            RequireKey(reader, 7); var iterations = reader.ReadInt32();
            RequireKey(reader, 8); var lanes = reader.ReadInt32();
            RequireKey(reader, 9); var salt = ReadBytes(reader, 16);
            RequireKey(reader, 10); var nonce = ReadBytes(reader, 12);
            RequireKey(reader, 11);
            if (reader.ReadStartArray() != 2) throw new VaultFormatException("wrap-shape-invalid");
            var ciphertext = ReadBytes(reader, 32);
            var tag = ReadBytes(reader, 16);
            reader.ReadEndArray();
            reader.ReadEndMap();
            if (reader.BytesRemaining != 0) throw new VaultFormatException("key-envelope-trailing-data");
            var envelope = new VaultKeyEnvelope(dataRootId, vaultId, generation, new(memory, iterations, lanes), salt, nonce, ciphertext, tag);
            Validate(envelope);
            return envelope;
        }
        catch (CborContentException exception)
        {
            throw new VaultFormatException($"key-envelope-invalid:{exception.GetType().Name}");
        }
    }

    private static byte[] BuildAssociatedData(Guid dataRootId, Guid vaultId, ulong generation, Argon2Parameters parameters)
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartArray(8);
        writer.WriteInt32(1);
        writer.WriteByteString(dataRootId.ToByteArray());
        writer.WriteByteString(vaultId.ToByteArray());
        writer.WriteUInt64(generation);
        writer.WriteTextString("argon2id");
        writer.WriteInt32(parameters.MemoryKiB);
        writer.WriteInt32(parameters.Iterations);
        writer.WriteInt32(parameters.Lanes);
        writer.WriteEndArray();
        return writer.Encode();
    }

    private static void Validate(VaultKeyEnvelope envelope)
    {
        envelope.Parameters.Validate();
        if (envelope.DataRootId == Guid.Empty || envelope.VaultId == Guid.Empty || envelope.Generation == 0
            || envelope.Salt.Length != 16 || envelope.Nonce.Length != 12
            || envelope.Ciphertext.Length != 32 || envelope.Tag.Length != 16)
        {
            throw new VaultFormatException("key-envelope-fields-invalid");
        }
    }

    private static void RequireKey(CborReader reader, int expected)
    {
        if (reader.ReadInt32() != expected) throw new VaultFormatException("key-envelope-key-invalid");
    }

    private static byte[] ReadBytes(CborReader reader, int expectedLength)
    {
        var value = reader.ReadByteString();
        if (value.Length != expectedLength) throw new VaultFormatException("key-envelope-field-size-invalid");
        return value;
    }
}
