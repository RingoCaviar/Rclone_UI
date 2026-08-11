using System.Collections.Immutable;
using System.Text.Json;
using RcloneUI.DataRoot;

namespace RcloneUI.Transfers;

public sealed class VaultTransferRunJournal(IDataRootSession dataRoot) : ITransferRunJournal, IDisposable
{
    private static readonly Guid JournalRecordId = Guid.Parse("e5fd1ec7-5275-4e1e-9257-2cbf08a377ef");
    private readonly SemaphoreSlim gate = new(1, 1);

    public async ValueTask SaveAsync(TransferRunSnapshot snapshot, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
            records[snapshot.RunId] = snapshot;
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(records);
            try
            {
                var observed = dataRoot.Observe();
                var result = await dataRoot.ExecuteAsync(new UpsertVaultRecord(JournalRecordId, VaultRecordType.Activity, 1, plaintext), observed.Revision, cancellationToken).ConfigureAwait(false);
                if (result.Status != DataRootCommandStatus.Applied) throw new InvalidOperationException($"transfer-journal-{result.Status.ToString().ToLowerInvariant()}");
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally { gate.Release(); }
    }

    public async ValueTask<TransferRunSnapshot?> ReadAsync(TransferRunId runId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await ReadAllAsync(cancellationToken).ConfigureAwait(false)).GetValueOrDefault(runId); }
        finally { gate.Release(); }
    }

    public async ValueTask<ImmutableArray<TransferRunSnapshot>> ReadIncompleteAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadAllAsync(cancellationToken).ConfigureAwait(false)).Values
                .Where(snapshot => snapshot.TerminalResult is null)
                .OrderBy(snapshot => snapshot.UpdatedUtc)
                .ToImmutableArray();
        }
        finally { gate.Release(); }
    }

    public void Dispose() => gate.Dispose();

    private async ValueTask<Dictionary<TransferRunId, TransferRunSnapshot>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var record = await dataRoot.ReadAsync(JournalRecordId, cancellationToken).ConfigureAwait(false);
        if (record is null) return [];
        if (record.RecordType != VaultRecordType.Activity || record.SchemaVersion != 1) throw new InvalidDataException("Transfer journal schema is unsupported.");
        return JsonSerializer.Deserialize<Dictionary<TransferRunId, TransferRunSnapshot>>(record.Plaintext.Span) ?? throw new InvalidDataException("Transfer journal is invalid.");
    }
}
