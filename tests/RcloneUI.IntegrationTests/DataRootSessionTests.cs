using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RcloneUI.DataRoot;

namespace RcloneUI.IntegrationTests;

public sealed class DataRootSessionTests
{
    private static readonly byte[] Password = "correct horse battery staple"u8.ToArray();

    [Fact]
    public async Task VaultRecordsRemainEncryptedAndReadableAfterReopen()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryRoot();
        try
        {
            var opened = await DataRootSession.OpenForTestingAsync(
                new(root, DataRootOpenMode.CreateIfMissing, Password),
                new TestKeyDeriver(), cancellationToken);
            Assert.True(opened.Status == DataRootOpenStatus.Opened, opened.DiagnosticCode);
            await using (var session = opened.Session!)
            {
                var recordId = Guid.Parse("11111111-2222-3333-4444-555555555555");
                var result = await session.ExecuteAsync(
                    new UpsertVaultRecord(recordId, VaultRecordType.Remote, 1, "secret-remote-name"u8.ToArray()),
                    0,
                    cancellationToken);
                Assert.Equal(DataRootCommandStatus.Applied, result.Status);
                Assert.Equal("secret-remote-name", Encoding.UTF8.GetString((await session.ReadAsync(recordId, cancellationToken))!.Plaintext.Span));

                var migration = await session.ExecuteAsync(
                    new MigrateVault(2, Password),
                    result.Revision,
                    cancellationToken);
                Assert.Equal(DataRootCommandStatus.Applied, migration.Status);
                Assert.Equal(2UL, migration.Revision);
                Assert.Equal("secret-remote-name", Encoding.UTF8.GetString((await session.ReadAsync(recordId, cancellationToken))!.Plaintext.Span));
            }

            Assert.DoesNotContain("secret-remote-name", Encoding.UTF8.GetString(File.ReadAllBytes(FindVaultDatabase(root))), StringComparison.Ordinal);

            var reopened = await DataRootSession.OpenForTestingAsync(
                new(root, DataRootOpenMode.OpenExisting, Password),
                new TestKeyDeriver(), cancellationToken);
            Assert.Equal(DataRootOpenStatus.Opened, reopened.Status);
            await using var reopenedSession = reopened.Session!;
            Assert.Equal(2UL, reopenedSession.Observe().Revision);
            Assert.Equal(2, Directory.GetDirectories(Path.Combine(root, "vault", "generations")).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LiveWriterLeaseRejectsASecondSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryRoot();
        try
        {
            var first = await DataRootSession.OpenForTestingAsync(new(root, DataRootOpenMode.CreateIfMissing, Password), new TestKeyDeriver(), cancellationToken);
            await using var firstSession = first.Session!;

            var second = await DataRootSession.OpenForTestingAsync(new(root, DataRootOpenMode.OpenExisting, Password), new TestKeyDeriver(), cancellationToken);

            Assert.Equal(DataRootOpenStatus.AlreadyOwned, second.Status);
            Assert.Null(second.Session);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WrongPasswordAndCorruptSelectorFailClosed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryRoot();
        try
        {
            var created = await DataRootSession.OpenForTestingAsync(new(root, DataRootOpenMode.CreateIfMissing, Password), new TestKeyDeriver(), cancellationToken);
            await created.Session!.DisposeAsync();
            var wrongPassword = await DataRootSession.OpenForTestingAsync(
                new(root, DataRootOpenMode.OpenExisting, "wrong password"u8.ToArray()),
                new TestKeyDeriver(), cancellationToken);
            Assert.Equal(DataRootOpenStatus.AuthenticationFailed, wrongPassword.Status);

            var generationsBefore = Directory.GetDirectories(Path.Combine(root, "vault", "generations"));
            File.WriteAllText(Path.Combine(root, "vault", "CURRENT"), "corrupt");
            var corrupt = await DataRootSession.OpenForTestingAsync(new(root, DataRootOpenMode.CreateIfMissing, Password), new TestKeyDeriver(), cancellationToken);

            Assert.Equal(DataRootOpenStatus.NeedsRecovery, corrupt.Status);
            Assert.Equal(generationsBefore, Directory.GetDirectories(Path.Combine(root, "vault", "generations")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LockClearsAccessUntilTheCorrectPasswordUnlocks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryRoot();
        try
        {
            var opened = await DataRootSession.OpenForTestingAsync(new(root, DataRootOpenMode.CreateIfMissing, Password), new TestKeyDeriver(), cancellationToken);
            await using var session = opened.Session!;
            session.Lock();

            Assert.Null(await session.ReadAsync(Guid.NewGuid(), cancellationToken));
            Assert.False(await session.UnlockAsync("wrong password"u8.ToArray(), cancellationToken));
            Assert.True(await session.UnlockAsync(Password, cancellationToken));
            Assert.Equal(DataRootSessionState.Unlocked, session.Observe().State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeletedCiphertextRowForcesRecoveryInsteadOfAppearingMissing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryRoot();
        try
        {
            var opened = await DataRootSession.OpenForTestingAsync(new(root, DataRootOpenMode.CreateIfMissing, Password), new TestKeyDeriver(), cancellationToken);
            var recordId = Guid.NewGuid();
            await opened.Session!.ExecuteAsync(new UpsertVaultRecord(recordId, VaultRecordType.Remote, 1, "secret"u8.ToArray()), 0, cancellationToken);
            await opened.Session.DisposeAsync();

            using (var connection = new SqliteConnection($"Data Source={FindVaultDatabase(root)};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM encrypted_records;";
                command.ExecuteNonQuery();
            }

            var reopened = await DataRootSession.OpenForTestingAsync(new(root, DataRootOpenMode.OpenExisting, Password), new TestKeyDeriver(), cancellationToken);

            Assert.Equal(DataRootOpenStatus.NeedsRecovery, reopened.Status);
            Assert.Null(reopened.Session);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HostileArgon2ParametersAreRejectedBeforeNativeAllocation()
    {
        var parameters = new Argon2Parameters(int.MaxValue, 1, int.MaxValue);

        var exception = Assert.Throws<VaultFormatException>(parameters.Validate);

        Assert.Equal("argon2-parameters-out-of-policy", exception.Code);
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "RcloneUI.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string FindVaultDatabase(string root) =>
        Path.Combine(
            Directory.GetDirectories(Path.Combine(root, "vault", "generations")).Order(StringComparer.Ordinal).Last(),
            "vault.db");

    private sealed class TestKeyDeriver : IVaultKeyDeriver
    {
        public void Derive(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, Argon2Parameters parameters, Span<byte> output)
        {
            parameters.Validate();
            using var hmac = new HMACSHA256(password.ToArray());
            Assert.True(hmac.TryComputeHash(salt, output, out var written));
            Assert.Equal(32, written);
        }
    }
}
