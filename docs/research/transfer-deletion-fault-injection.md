# Transfer deletion fault-injection result

Issue: [#25](https://github.com/RingoCaviar/Rclone_UI/issues/25)

## Decision

Move source deletion and Mirror target deletion are admitted per path by the Background Host. The execution adapter receives only a typed `DeletionAdmission`, never the complete Accepted Preview as implicit deletion authority.

Each candidate must be eligible in the immutable preview, positively verified, and free of skipped, conflict, failed, changed, or possibly-affected evidence. Move candidates also carry their accepted size and an explicit requirement for per-file source revalidation immediately before deletion. An expected destructive phase with no admitted paths fails closed.

## Deterministic matrix

The integration harness covers:

- Safety Copy, Copy, Verify, Move-delete, and Mirror-delete cancellation boundaries;
- cooperative adapter cancellation and truthful `CancelledWithPartialResults` results;
- quota, verification, and capability/configuration failures;
- mixed clean, changed, skipped, and unverified paths;
- successful and failed non-atomic Safety Copy fallback;
- eventual-consistency verification succeeding on the third attempt and exhausting all retries;
- 250-path manifests, beyond rclone `core/transferred`'s 100-entry window;
- restart recovery of non-terminal checkpoints as `InterruptedBySystemOrCrash`, without truncating evidence.

## Invariants

- Delete is never called before successful Copy and Verify phases.
- A failed Safety Copy blocks Mirror deletion.
- A transient consistency delay may retry, but deletion begins only after positive verification.
- Cancellation closes admission and requests cooperative rclone stop; cancellation during an admitted delete is reported as potentially partial rather than rolled back or called successful.
- Startup recovery is idempotent and never resumes deletion from a pre-crash admission.
- Complete per-path evidence is owned by the Host journal and does not depend on rclone's bounded transferred view.

## Verification

The full repository verification passed with 171 tests, one native Argon2 candidate test skipped by design, zero warnings or errors, and no vulnerable dependencies.
