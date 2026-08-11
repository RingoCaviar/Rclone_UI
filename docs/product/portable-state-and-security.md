# Portable state and credential security

## Data Root

- Default to `data/` beside the Portable App.
- Keep rclone configuration, application settings, Remote presentation metadata, Transfer Task definitions, schedules, history, redacted logs, cache indexes, snapshots, and update-recovery state under the Data Root.
- Keep application and managed-component binaries in the application directory. WinFsp remains a system component.
- Never silently redirect state to `%AppData%` or the registry.
- When the default location is not writable, require a different Data Root or offer read-only diagnostic mode.
- Locate a custom Data Root with a non-secret `data-root.json` beside the app when writable, `--data-root <path>`, or a shortcut argument. If none is available, ask at startup.

## Credentials and Vault

- Default to rclone configuration encryption with a user-supplied Master Password. `rclone obscure` does not count as encryption.
- Permit an unencrypted rclone configuration only through an advanced flow with an explicit risk warning.
- Keep Remote metadata, Transfer Tasks, schedules, and activity history in the encrypted Vault, separate from `rclone.conf`.
- Keep non-sensitive interface settings outside the Vault so startup and diagnostic mode can render before unlock.
- Ask for the Master Password at each launch by default.
- Offer an explicit "remember for this Windows user on this computer" option backed by user-scoped DPAPI. This convenience is non-portable and never places a decrypting secret in the Data Root.
- Do not provide password recovery, escrow, cloud backup, or a bypass. A forgotten password requires another known-password backup or a reset and Remote reconnection.
- Apply the confidentiality, metadata, rollback, deletion, and diagnostic boundaries in `docs/product/vault-threat-boundary.md`; do not describe record encryption as hiding file structure, size, count, or modification time.

## Locking and process lifetime

- Keep the Vault unlocked during a normal active session.
- Lock sensitive UI state when the Windows session locks, the computer suspends, or the user chooses Lock.
- Allow already-running transfers and Mounts to continue while locked. After the session was successfully unlocked once, the Background Host may start previously configured and enabled schedules while the Windows session remains logged in and locked.
- Block sensitive details, Remote changes, and new manual work until unlock. Clear operational unlock material when the user logs out or the Background Host exits.
- Permit one writable application instance per Data Root. Record machine, Windows user, process ID, and start time in the ownership lock.
- A second instance may activate the owner, choose another Data Root, or enter read-only diagnostic mode.
- Recover a stale lock only after confirming that the owning process no longer exists.

## Missing, read-only, and removable storage

- If the Data Root disappears, stop schedules and new operations immediately.
- Do not force-cancel active transfers or Mounts solely because the Data Root disappeared; buffer bounded status in memory and flush it if the root returns.
- Let operations fail normally if their own source or destination disappears.
- Read-only diagnostic mode may show versions, component health, a non-secret configuration summary, and exportable diagnostics. It may not change Remotes, run transfers, create Mounts, update components, or write history.
- Restrict Data Root access to the current user when supported by NTFS.
- On FAT/exFAT, warn that plaintext settings and redacted logs may be readable by other people. Encryption, not filesystem ACLs, remains the credential and Vault security boundary.

## Backup, password change, and relocation

- Create an encrypted snapshot before configuration changes, migrations, password changes, updates that mutate state, and other high-risk writes.
- Create at most one routine snapshot per day and retain the three most recent snapshots by default.
- Support export of a complete encrypted backup to a user-selected external location.
- Restore into a new Data Root after verifying password, integrity, and version compatibility. Never automatically merge a full backup into live state.
- Keep the old Data Root until the restored root completes a successful startup.
- Change the Master Password by verifying the old password, re-encrypting rclone configuration and the Vault in temporary files, verifying the result, and atomically switching. Never persist plaintext intermediate state.
- Retain the old encrypted snapshot until the new password successfully unlocks once.
- Move a Data Root only after pausing schedules and closing the Vault; copy, verify, switch the locator, and preserve the old root until successful startup.

## Logging and diagnostics

- Rotate logs and enforce configurable age and capacity limits.
- Never log passwords, tokens, authorization codes, decrypted configuration, DPAPI material, or sensitive request headers.
- Offer path masking for logs and diagnostic bundles.
- Treat failure to redact a known secret field as a security defect.

## Mount cache boundary

- Treat rclone VFS cache content as potentially plaintext user file data even when the Vault and rclone configuration are encrypted.
- Restrict cache access to the current user on NTFS where possible.
- Show a persistent warning for writable caches on FAT/exFAT, shared folders, or other locations without an equivalent access boundary.
- Do not claim transparent VFS-cache encryption in v1. Recommend a BitLocker-protected cache volume or an rclone crypt Remote when cached file content requires encryption at rest.
