namespace RcloneUI.Updates;

public sealed record UpdateAdmission(bool DataRootAvailable, bool ActiveTransfers, bool ActiveMounts, bool RestartPending, string? DiagnosticCode = null);
public sealed record PairEvidence(bool OldApplicationValid, bool NewApplicationValid, bool OldVaultValid, bool NewVaultValid, bool NewHealthValid, bool ExactlyOneHost, string? HealthEvidenceDigest = null);

public interface IUpdateTrustVerifier
{
    ValueTask<bool> VerifyAsync(VerifiedArtifact artifact, CancellationToken cancellationToken);
}

public interface IUpdateRuntime
{
    ValueTask<UpdateAdmission> InspectAdmissionAsync(CancellationToken cancellationToken);
    ValueTask<PairEvidence> InspectPairAsync(PairedUpdatePlan plan, CancellationToken cancellationToken);
    ValueTask StopOldHostAsync(PairedUpdatePlan plan, CancellationToken cancellationToken);
    ValueTask SwitchApplicationAsync(PairedUpdatePlan plan, bool useNew, CancellationToken cancellationToken);
    ValueTask StageAndVerifyVaultAsync(PairedUpdatePlan plan, CancellationToken cancellationToken);
    ValueTask SwitchVaultAsync(PairedUpdatePlan plan, bool useNew, CancellationToken cancellationToken);
    ValueTask<bool> StartAndCheckNewHostAsync(PairedUpdatePlan plan, TimeSpan timeout, CancellationToken cancellationToken);
    ValueTask StartOldHostAsync(PairedUpdatePlan plan, CancellationToken cancellationToken);
}

public interface IUpdateJournal
{
    ValueTask SaveAsync(UpdateJournalEntry entry, CancellationToken cancellationToken);
    ValueTask<UpdateJournalEntry?> ReadAsync(UpdateTransactionId transactionId, CancellationToken cancellationToken);
}
