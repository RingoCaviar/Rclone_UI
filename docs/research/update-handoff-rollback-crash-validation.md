# Update handoff and rollback crash validation

Validation date: 2026-08-11  
Issue: #22  
Prototype evidence: branch `prototype/update-handoff-crash-matrix`, commit `03cfc05`

## Verdict

The update protocol converges when application version and Vault generation are treated as one recoverable pair and the new pair is not authoritative until a durable `NewHealthPassed` record exists. The throwaway harness injected faults around every modeled side effect/journal boundary and recovered all 45 cases to exactly one old healthy pair, one new healthy pair, or `DataRootUnavailable` while media was absent.

Keep the old application directory and old Vault generation fully intact until the new Host passes the complete 30-second health contract and that result is durably recorded. Before that commit point, recovery restores both old selectors. At or after it, recovery completes both new selectors. Never independently “repair” only the application pointer or only the Vault pointer.

## Tested state sequence

1. `PlanWritten`
2. `VersionVerified`
3. `HandoffIssued`
4. `OldHostStopped`
5. `VersionPointerSwitched`
6. `NewHostBaseHealthy`
7. `VaultStaged`
8. `VaultVerified`
9. `VaultPointerSwitched`
10. `NewHealthPassed`
11. `Committed`

For each phase, the harness injected failure before its action, after the action but before the journal, after the journal, and with the Data Root unavailable after the phase. It also injected explicit new-Host health failure.

Results:

- 45/45 cases passed;
- before durable `NewHealthPassed`, recovery selected application `v1` and Vault generation `1`;
- after durable `NewHealthPassed`, recovery selected `v2` and generation `2`;
- media absence returned `DataRootUnavailable` without mutation, then converged after return;
- health failure returned to the old pair;
- every healthy recovery contained exactly one Host marker.

## Transaction contract

The update journal binds:

- transaction ID and update plan hash;
- old/new application versions, directory digests, and active-pointer values;
- old/new Vault generation IDs, format/schema versions, and authenticated manifest digests;
- old Host identity/epoch, one-time handoff-token digest, new Host identity/epoch;
- current phase, phase revision, timestamps, typed failure, and health evidence digest.

Persist intent before every irreversible-looking side effect and persist completion after verifying it from disk/process state. Journal replacement, application pointer replacement, and Vault selector replacement use flushed same-directory files on the admitted Data Root volume. Recovery distrusts phase text alone and revalidates every artifact it selects.

## Health commit point

The new pair becomes authoritative only when the new Host proves within the bounded health window:

- executable/version directory integrity and expected update plan;
- exclusive Data Root lease and authenticated handoff token;
- compatible IPC protocol and secured pipe;
- selected/staged Vault structural and cryptographic validity;
- lifecycle journal reconciliation;
- expected managed-component versions/capabilities;
- no unexplained old/new Host or rclone process;
- ability to reopen through the intended new selectors.

`NewHostBaseHealthy` is insufficient. A Vault migration staged or temporarily selected before final health remains reversible because the old generation and old application stay untouched. If final health fails or times out, stop the new Host, restore the old Vault selector, restore the old application pointer, and start the old Host. Only after this rollback itself verifies may cleanup be scheduled.

## Recovery rules

- Missing/unreadable Data Root: do not modify application or Vault pointers; report `DataRootUnavailable` and wait for identity-verified return.
- Missing/corrupt journal: choose only a pair independently proven compatible and authenticated; ambiguity enters guided recovery.
- Phase before `NewHealthPassed`: restore and verify old application + old Vault.
- Durable `NewHealthPassed` with valid new evidence: complete/verify new application + new Vault.
- New evidence invalid at any phase: roll back the pair if old evidence remains valid; otherwise guided recovery.
- Never run old Host against a new-only Vault schema or new Host against an unverified staged generation.
- Enforce one Host through mutex, Data Root lease, authenticated handoff identity, and process start-time evidence; pointer text is not process authority.
- Retain the previous pair until at least one subsequent clean launch/unlock and normal snapshot policy permits cleanup.

## What remains unproven

- actual Authenticode/hash verification and GitHub Release artifacts;
- real updater/Host processes, pipe handoff authentication, timeout, and process identity;
- real SQLite/AEAD migration and backward-reader compatibility;
- disk-full/partial-write behavior, FAT/exFAT removal, and storage firmware power-loss durability;
- WinFsp MSI boundaries and active Mount/Transfer drain;
- Windows 10/11 and selected .NET LTS behavior.

## Prototype disposition

The harness remains off `main` as primary evidence on [`prototype/update-handoff-crash-matrix`](https://github.com/RingoCaviar/Rclone_UI/tree/prototype/update-handoff-crash-matrix/prototypes/update-handoff-crash-matrix) at commit `03cfc05`.
