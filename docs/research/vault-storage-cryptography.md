# Vault storage, cryptography, and migration mechanism

Status: research recommendation for GitHub issue #12  
Sources reviewed: primary standards, upstream SQLite documentation, Microsoft/.NET documentation, and maintained cryptography-library sources, current as of 2026-08-11.

## Decision summary

Use **ordinary SQLite through `Microsoft.Data.Sqlite` as a transactional container, with sensitive values encrypted independently at the application layer**. Do not make a database-encryption extension the security boundary. Keep only structural identifiers, format versions, non-secret settings, encrypted-record headers, and ciphertext in SQLite. Encrypt every sensitive logical object with a randomly generated 256-bit Vault Data Encryption Key (VDEK) using .NET `AesGcm`, a fresh 96-bit nonce, a 128-bit tag, and canonical associated data. Wrap the VDEK rather than encrypting all data directly with the password-derived key.

Derive a 256-bit Key Encryption Key (KEK) from the Master Password using **Argon2id v=0x13**, a unique 128-bit salt, and parameters stored in the envelope. Start with RFC 9106's memory-constrained recommendation (`m=64 MiB`, `t=3`, `p=4`) and calibrate upward per device behind hard resource ceilings; never silently calibrate below that baseline. RFC 9106 explicitly recommends Argon2id and this parameter set as its second, memory-constrained default ([RFC 9106 §§3.1, 4, 7.4](https://www.rfc-editor.org/rfc/rfc9106.html)). Use the stable `BouncyCastle.Cryptography` package's Argon2 implementation only after its RFC test vectors pass in CI; Bouncy Castle is actively maintained and publishes its official .NET package and security policy ([Bouncy Castle .NET repository](https://github.com/bcgit/bc-csharp), [NuGet package](https://www.nuget.org/packages/BouncyCastle.Cryptography)). Pin an exact reviewed version and keep the KDF behind a narrow interface so it can be replaced without changing the on-disk envelope.

The optional “remember on this PC” feature stores a *second wrapping of the same VDEK* under Windows DPAPI `CurrentUser`; it never stores the Master Password or replaces the password-wrapped copy. Microsoft documents that `CurrentUser` can only be unprotected in the same user context, while `LocalMachine` is available to any account on the machine; therefore `LocalMachine` is prohibited ([`DataProtectionScope`](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope)). Removing the DPAPI envelope restores fully portable password-only behavior without re-encrypting Vault records.

Use SQLite rollback-journal mode, one Background Host writer, a held OS file handle as an outer single-writer lease, explicit migration journals, and verified same-directory generation switching. On removable FAT/exFAT, no filesystem primitive should be described as power-loss-proof: SQLite itself warns that broken locks, failed syncs, and non-power-safe flash controllers can corrupt databases ([SQLite corruption causes](https://www.sqlite.org/howtocorrupt.html)). The recovery contract therefore comes from redundant generations, hashes, AEAD authentication, and a small phase journal—not from assuming a rename or directory update is infallibly atomic.

## Why application-layer encrypted records

### Evaluated approaches

| Approach | Result | Reason |
|---|---|---|
| Plain SQLite plus application-layer AEAD | **Choose** | Uses upstream SQLite transactions and built-in .NET AES-GCM; ciphertext format, key rotation, per-record corruption isolation, and migrations remain under application control. No native encryption fork becomes a permanent format dependency. |
| SQLite Encryption Extension (SEE) | Do not choose for v1 | SEE is upstream SQLite's supported whole-file encryption extension, but it requires a commercial license and its current algorithm menu and distribution terms create a separate native-build/release obligation ([SQLite SEE documentation](https://sqlite.org/see/doc/release/www/readme.wiki)). It remains a possible future licensed alternative, not the portable format. |
| SQLCipher / SQLite3 Multiple Ciphers | Do not choose as the security boundary | These encrypt database pages, which hides schema and row counts better, but bind recovery and compatibility to a particular native SQLite build and cipher configuration. Microsoft has documented churn in encryption bundles and warned that an earlier free SQLCipher bundle was barely maintained; current Microsoft.Data.Sqlite versions may change the bundled native provider ([Microsoft.Data.Sqlite encryption change](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-11.0/breaking-changes), [custom SQLite builds](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)). |
| One encrypted JSON/blob file | Reject | Rewrites and re-encrypts the entire Vault for each change, has no relational constraints or transactional partial updates, and makes corruption all-or-nothing. |
| OS-only DPAPI encryption | Reject | Breaks portability and backup recovery on another machine/user. DPAPI is only an opt-in convenience wrapper. |

Application-layer encryption leaks the SQLite schema, approximate record sizes/counts, stable opaque IDs, key versions, and transaction timing. It must not leak Remote names, paths, provider details, schedules, history, credentials, or user labels. If hiding structural metadata later becomes a requirement, reassess an audited whole-file solution as defense in depth; do not pretend record encryption already provides it.

## Concrete cryptographic envelope

### Keys

- `VDEK`: 32 random bytes from `RandomNumberGenerator.Fill`; encrypts all records for one key generation.
- `KEK-password`: 32 bytes output by Argon2id from the normalized UTF-8 Master Password and a random 16-byte salt. Treat password bytes exactly as entered after one documented Unicode normalization policy; changing normalization is a format migration.
- `KEK-DPAPI`: not exported. DPAPI directly protects a versioned blob containing the VDEK and Vault identity with `DataProtectionScope.CurrentUser` and fixed application-specific optional entropy. Optional entropy is domain separation, not a secret.
- Never reuse a VDEK as a KEK, never derive record nonces from row IDs, and never store a plaintext password-equivalent verifier. Successful AEAD unwrap of the key envelope is the password check.

### Password envelope (`vault/key-envelope.cbor`)

Use deterministic/canonical CBOR (RFC 8949) with a fixed schema; reject duplicate keys, indefinite-length values, unknown critical fields, oversized values, unsupported algorithms, or parameters above policy ceilings before allocating memory. Canonical CBOR provides deterministic encoding rules suitable for authenticated metadata ([RFC 8949 §4.2](https://www.rfc-editor.org/rfc/rfc8949.html)). The envelope contains:

```text
format_version, vault_id (16 bytes), key_generation,
kdf = { id: "argon2id", version: 0x13, salt: 16 bytes, m_kib, t, p },
wrap = { id: "aes-256-gcm", nonce: 12 bytes, ciphertext: 32 bytes, tag: 16 bytes },
created_utc
```

The exact canonical encoding of `{format_version, vault_id, key_generation, kdf, wrap.id, created_utc}` is AEAD associated data. The password KEK encrypts exactly `VDEK || envelope-check-context`; on unwrap, validate length, Vault identity, key generation, and tag before accepting the key. AES-GCM is a standardized AEAD mode ([NIST SP 800-38D](https://csrc.nist.gov/pubs/sp/800/38/d/final)); .NET's `AesGcm` supports a 12-byte nonce and constructors that require an explicit tag size ([`AesGcm`](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm), [`NonceByteSizes`](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.noncebytesizes)). Always construct it with a 16-byte tag size.

### Record envelope

Each encrypted row stores:

```text
record_id BLOB(16) PRIMARY KEY,
record_type INTEGER NOT NULL,
schema_version INTEGER NOT NULL,
key_generation INTEGER NOT NULL,
nonce BLOB(12) NOT NULL,
ciphertext BLOB NOT NULL,
tag BLOB(16) NOT NULL,
revision INTEGER NOT NULL,
UNIQUE(key_generation, nonce)
```

Plaintext is canonical CBOR for one complete aggregate (for example one Remote, task, schedule, or history segment), with strict length/depth limits. Associated data is a byte-exact, versioned encoding of `vault_id || record_id || record_type || schema_version || key_generation || revision`. A row cannot therefore be copied to another Vault, type, ID, generation, or revision without authentication failure. Generate each nonce with the platform CSPRNG and enforce the database uniqueness constraint. Never retry encryption with the same nonce after ambiguity: generate a new nonce and increment the revision inside the transaction.

AEAD authenticates encrypted content but does not establish that the set of rows is complete. Keep critical indexes/manifests (active Remote IDs, task IDs, schedule IDs and latest revisions) as encrypted records too, and validate their references on every open. Deletion rollback or loss of a whole valid row is otherwise not detectable from its tag alone.

### Master Password changes and key rotation

- Ordinary password change: unwrap the VDEK with the old KEK, create a fresh salt and KEK, write and verify a new password envelope, then switch the envelope generation. Records are not rewritten.
- Cryptographic key rotation: generate a new VDEK/key generation, re-encrypt records in bounded transactions, retain both wrapped VDEKs in a migration keyring until every row and encrypted manifest references the new generation, verify all records, then retire the old key only after a recovery snapshot and a successful subsequent unlock.
- KDF upgrade: store parameters per password envelope. A successful unlock may offer an explicit rewrap with stronger parameters. Never rewrite the envelope before the new one has been independently reopened and verified.
- Zero mutable password, KEK, VDEK, plaintext, and tag buffers where practical with `CryptographicOperations.ZeroMemory`; acknowledge that managed strings/copies cannot be guaranteed erased. Keep secrets out of exceptions, logs, dumps, telemetry, clipboard, and command lines.

## Persistence layout

```text
data/
  vault/
    CURRENT                         # tiny generation selector, no secrets
    generations/
      0000000000000042/
        vault.db                    # SQLite container
        key-envelope.cbor           # password-wrapped VDEK
        dpapi-envelope.bin          # optional; current-user wrapper
        manifest.cbor               # hashes, sizes, versions; authenticated by VDEK
    transaction.cbor                # create/migrate/restore/password-change phase journal
    writer.lock                     # held lease; diagnostic ownership payload
    quarantine/                     # never opened automatically
  snapshots/
    <utc>-<generation>/...          # complete closed generation, max 3 by product policy
  backups/                          # temporary export staging only
```

`CURRENT` contains a strict ASCII generation number and checksum. It contains no user-chosen path. Every referenced path is resolved beneath the canonical Data Root; reject traversal, reparse-point escapes, hard-link surprises, and generation contents not matching the authenticated manifest. Never open a database through multiple aliases: SQLite warns that renaming/unlinking an open database or addressing it by multiple names can break journal recovery ([SQLite corruption causes](https://www.sqlite.org/howtocorrupt.html)).

Use `PRAGMA application_id` for a fixed project-assigned magic and `PRAGMA user_version` only as a redundant coarse schema marker; the authoritative format/schema versions are explicit rows and the authenticated manifest. SQLite provides both pragmas plus `integrity_check`, `quick_check`, and `foreign_key_check`; `integrity_check` does not detect foreign-key violations, so both checks are required ([SQLite PRAGMA documentation](https://sqlite.org/pragma.html)).

## SQLite operating profile

Use one pinned SQLite native build brought by a pinned supported `Microsoft.Data.Sqlite` version. At open, assert the actual SQLite version and required compile options. Configure every writable connection before use:

```sql
PRAGMA journal_mode=DELETE;
PRAGMA synchronous=EXTRA;
PRAGMA foreign_keys=ON;
PRAGMA trusted_schema=OFF;
PRAGMA busy_timeout=5000;
PRAGMA mmap_size=0;
```

Prefer DELETE rollback journaling over WAL for a portable removable Data Root. WAL creates `-wal` and `-shm` companions, requires shared-memory coordination between connections, and a crash may leave the WAL for the next opener ([SQLite temporary/WAL files](https://sqlite.org/tempfiles.html)). A rollback journal gives the simplest closed-generation snapshot. `synchronous=EXTRA` adds a directory sync after a DELETE-mode journal is removed and improves durability around power loss ([SQLite `PRAGMA synchronous`](https://sqlite.org/pragma.html)). This still cannot make a lying USB controller durable.

Only Background Host opens the live Vault read/write. UI requests go through Host IPC. Keep database transactions short; do Argon2 and bulk AEAD work outside a write transaction against a private staged generation. Never copy `vault.db` with an OS file-copy while it is live. For a live snapshot, use `SqliteConnection.BackupDatabase`, which invokes SQLite's backup facility and blocks writers in the current API ([Microsoft.Data.Sqlite backup](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup)); then close and verify the destination before publishing it. SQLite's online backup API produces a snapshot of the source and exists specifically to avoid inconsistent raw copies ([SQLite Backup API](https://www.sqlite.org/backup.html)).

## Single-writer lease

Before opening SQLite read/write, canonicalize and validate the Data Root, then open `writer.lock` with `FileMode.OpenOrCreate`, read/write access, and `FileShare.None`, holding that exact handle for the whole writable lifetime. The payload is diagnostic only: random instance ID, canonical Data Root identity, machine/user identifiers, PID, process start time, and protocol version. `.NET` exposes exclusive sharing through `FileShare.None`; `CreateNew` also fails when a path already exists ([`FileStream` constructor](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.-ctor)).

The lock's authority is the live exclusive handle, not the payload or file existence. After a crash, a new process attempts the exclusive open and may rewrite stale diagnostics only after it succeeds. Never “break” a lock by deleting the file while an exclusive open fails. If the filesystem cannot demonstrate reliable exclusive handles, or is a network/share/cloud-synced location, refuse write mode and offer read-only diagnostics or migration to a supported local Data Root. SQLite explicitly warns that unreliable filesystem locks can corrupt a database ([SQLite over network filesystems](https://sqlite.org/useovernet.html)).

## Migration and commit protocol

Every migration is copy-on-write at the **generation** level; never run a destructive schema/key migration in place.

1. Acquire the writer lease; reject new tasks that mutate Vault state.
2. Open and authenticate the current generation; run `integrity_check`, `foreign_key_check`, decrypt/authenticate every record, and validate encrypted manifests/references.
3. Create a new same-Data-Root staging generation with a fresh ID. Write `transaction.cbor` with transaction ID, source/destination generation, operation, expected schema/key versions, and phase `preparing`; flush it with `FileStream.Flush(true)` ([`.NET FileStream.Flush(Boolean)`](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush)).
4. Use SQLite backup or logical migration into the staged database. Apply forward-only, numbered, deterministic migration steps in transactions. Record each completed step inside the staged DB and in the outer journal.
5. Close all connections. Reopen staged files from disk, assert versions, run SQLite checks, decrypt every record, validate cross-record invariants and manifest hashes, then mark the outer phase `verified` and flush.
6. Publish the staged directory under `generations/<id>`. It is still inactive. Write a new `CURRENT.new`, flush it, then replace `CURRENT` with same-directory `ReplaceFileW` where available or `MoveFileEx(..., MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)` as a fallback. Microsoft documents replacement and write-through semantics, but cross-volume moves may degrade to copy/delete, so all transaction files must remain on the same volume ([moving/replacing files](https://learn.microsoft.com/en-us/windows/win32/fileio/moving-and-replacing-files), [`MoveFileEx`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexa)).
7. Reopen through `CURRENT`, unlock, and rerun fast invariants. Mark `committed`; retain the prior generation as a snapshot until the new generation has successfully unlocked on a later clean start.

Recovery is state reconciliation, not blind replay:

- no valid journal: choose only a generation named by a valid `CURRENT`; otherwise enter recovery;
- `preparing`/`migrating`: old generation remains authoritative; quarantine incomplete staging;
- `verified`, selector still old: either safely publish the verified generation once or discard it; never remigrate in place;
- selector new, journal not committed: validate the new generation; keep it if completely valid, otherwise restore the old selector;
- both selector copies ambiguous or storage reports I/O anomalies: do not write; enumerate fully valid generations and require guided recovery.

Do not claim directory rename, `ReplaceFileW`, or `Flush(true)` provides transactional durability across every FAT/exFAT device. Microsoft documents `ReplaceFile` as a one-file replace and `MoveFileEx` write-through behavior, not multi-file atomicity; SQLite documents that non-power-safe flash can damage unrelated files. Generation redundancy and external encrypted backups are mandatory recovery layers.

## Backup and restore

- A snapshot is a complete, closed generation plus its password key envelope and authenticated manifest. Never omit or mismatch SQLite rollback/WAL companions; the selected profile closes/checkpoints the database so only `vault.db` is expected.
- Snapshot creation uses the SQLite backup API to a staging directory, then closes, verifies SQLite structure and all AEAD records, writes/authenticates the manifest, flushes files, and publishes the directory. Retain the latest three snapshots per product decision; a snapshot inside the same device is not protection against device loss.
- Full export is a versioned archive containing a complete generation, password envelope, manifest, application/rclone configuration needed by the product specification, and a format README. Exclude `dpapi-envelope.bin`, caches, lock files, journals, and plaintext logs. Encrypt all sensitive export members under the existing VDEK or a freshly generated export key wrapped by a separately confirmed backup password. Prevent zip-slip/path traversal and cap member sizes/counts.
- Restore never merges. Extract to a new directory under strict limits, verify manifest hashes, unlock with its password, run SQLite and full AEAD/invariant checks, then import as a new Data Root/generation using the migration protocol. Preserve the old Data Root until the restored one unlocks on a later clean start.
- A backup's format reader must support a documented bounded range. Newer unsupported versions are rejected read-only; older supported versions are migrated into a new generation, never modified inside the archive.

## Schema-version policy

Maintain three independent monotonically increasing integers:

1. `vault_format_version`: envelope, manifest, and generation layout;
2. `database_schema_version`: tables/indexes/constraints;
3. per-record `schema_version`: plaintext aggregate encoding.

Readers accept only explicitly supported versions, reject unknown critical fields, and preserve no unknown sensitive content by accident. Every application release declares `min_read`, `max_read`, and `current_write` versions. Migrations are ordered functions with preconditions, postconditions, resource bounds, and golden fixtures. A release is eligible for automatic update only if it can leave the current Vault untouched until a new verified generation commits and the previous application can still select the old generation after rollback.

Do not rely on SQLite's internal `schema_version`; SQLite warns that manipulating it can cause prepared statements to use stale schemas ([SQLite PRAGMA documentation](https://sqlite.org/pragma.html)). Use application-owned migration tables plus the authenticated manifest.

## Corruption and compatibility test corpus

CI must use published primitives' vectors and generated whole-system fixtures. Never substitute “round trip succeeded” for standards conformance.

### Cryptographic vectors

- RFC 9106 §5.3 Argon2id vector, including version, lanes, memory, passes, salt, secret/associated-data cases and expected tag ([RFC 9106 §5.3](https://www.rfc-editor.org/rfc/rfc9106.html#section-5.3)).
- NIST AES-GCM known-answer tests for 256-bit keys, 96-bit IVs, AAD, empty/non-empty plaintext, and invalid tags from the NIST CAVP/ACVP vectors ([NIST cryptographic algorithm validation program](https://csrc.nist.gov/projects/cryptographic-algorithm-validation-program)).
- DPAPI CurrentUser round trip for the same user, failure under a different Windows user, tampered blob failure, and removal/recreation of the optional envelope without changing record ciphertext.
- Golden canonical-CBOR bytes; reject duplicate/out-of-order/indefinite/oversized encodings where the profile forbids them.

### Tamper cases

Flip/truncate/extend each of nonce, ciphertext, tag, AAD-bound header, password envelope, encrypted manifest, SQLite page, and `CURRENT`. Swap records between IDs/types/Vaults/generations. Delete a valid row, replay an older revision, duplicate a nonce, remove a manifest member, provide absurd Argon2 parameters, and use a wrong password. Required outcome is authenticated failure or recovery mode—never partial plaintext or an empty “new” Vault.

### Crash and filesystem cases

Inject termination and synthetic I/O errors after every journal write, SQLite commit, file flush, close, directory publish, selector replacement, and first reopen. Repeat on Windows 10/11 with NTFS and representative FAT32/exFAT removable media; physically remove the medium at each boundary in a dedicated destructive test fixture. Test disk-full, read-only transition, corrupt/hot rollback journal, stale lock payload with no live handle, live competing handle, selector loss, one-generation loss, two valid generations, and a controller that ignores flush (simulated). SQLite's own atomic-commit description explains that durability depends on VFS/device characteristics and rollback journals ([SQLite atomic commit](https://sqlite.org/atomiccommit.html)).

Run `integrity_check` plus `foreign_key_check`, then AEAD-authenticate every record and validate encrypted referential manifests. Also fuzz the envelope/archive parsers and migration inputs with strict CPU, memory, nesting, row-count, and file-size budgets; untrusted KDF parameters must be policy-checked before Argon2 allocation.

## Recovery invariants

Implementation is acceptable only if all of these remain true after every tested interruption:

1. At most one process holds writable authority for a canonical Data Root; UI never opens the live database independently.
2. The active selector names one fully closed, structurally valid and cryptographically authenticated generation, or the application enters read-only recovery without creating a fresh Vault.
3. No plaintext credential, Remote metadata, task, schedule, history, Master Password, KEK, or VDEK is persisted outside authenticated encryption.
4. Every accepted encrypted row is bound to its Vault, identity, type, schema, key generation, and revision; tag failure is never downgraded to “missing.”
5. A wrong password and corrupted envelope are distinguishable internally for diagnostics only when safely knowable, but the UI never overwrites either and never offers a bypass.
6. Password change never rewrites user data and leaves the old envelope/snapshot recoverable until the new envelope has unlocked from disk.
7. Schema or key migration never modifies the only known-good generation and converges after restart to old-valid or new-valid, never a mixed generation.
8. Backup/restore never merges identities and never activates data before complete structural, cryptographic, and semantic validation.
9. DPAPI loss, another machine/user, or disabling “remember” cannot make the password-wrapped Vault unrecoverable.
10. FAT/exFAT or device uncertainty reduces automation and may force read-only recovery; it never relaxes integrity checks or deletes the last valid generation.

## Implementation acceptance criteria

1. A reference implementation passes RFC 9106 Argon2id and NIST AES-GCM vectors using pinned dependencies.
2. Wrong-password, tag, header, row-swap, row-loss, replay, nonce-duplication, SQLite-corruption, and manifest-corruption fixtures all fail closed.
3. Crash injection at every migration/password-change/backup phase always recovers the previous generation or the completely verified new one.
4. A second Background Host cannot acquire write authority; a stale diagnostic payload is recoverable only after an exclusive handle succeeds.
5. The same exported backup restores on another Windows 10/11 machine using only its backup/Master Password; DPAPI state is neither required nor exported.
6. Resource ceilings reject hostile Argon2 parameters, CBOR nesting, database sizes, and archive expansion before expensive allocation.
7. NTFS and FAT/exFAT removal/power-loss tests demonstrate the stated recovery behavior, and documentation does not promise stronger atomicity than observed.

## Unresolved risks and implementation spikes

- **Argon2 implementation assurance:** Bouncy Castle is maintained, but the project should record its security review provenance, benchmark its exact Argon2 API, verify memory clearing behavior, and compare RFC vectors before adoption. If assurance is insufficient, evaluate a pinned official `libargon2` native build with a minimal reviewed P/Invoke boundary; do not silently fall back to PBKDF2. PBKDF2 is only a compatibility escape hatch after an explicit design review.
- **Parameter calibration:** 64 MiB/3 passes/4 lanes is the portable floor from RFC 9106, not a universal optimum. Benchmark unlock latency and concurrent memory pressure on minimum-supported Windows hardware and impose both lower and upper policy bounds to resist hostile backup files.
- **Metadata leakage:** application-layer records expose structure and sizes. Confirm that this meets the product threat model before implementation; otherwise fund a supported whole-file encryption layer in addition to, not casually instead of, the envelope design.
- **Rollback/replay resistance:** a fully valid older generation copied back by an attacker with filesystem access cannot be detected without trusted external state. DPAPI can optionally remember a last-seen generation on one PC, but that would not be portable. Treat rollback detection as best-effort and clearly distinguish it from AEAD integrity.
- **Removable-media durability:** USB firmware may ignore flushes or corrupt unrelated sectors. No software-only local transaction solves this. The product must keep three local snapshots, encourage external encrypted exports, and test representative devices.
- **File identity/reparse points:** define and prototype the exact Windows canonical-path, volume-serial/file-ID and reparse-point policy before trusting `writer.lock` or generation paths. Reject network/cloud-synced/reparse-backed writable roots unless explicitly proven safe.
- **Secret lifetime:** managed-memory copies and crash dumps cannot be perfectly wiped. Add dump policy, secret-buffer ownership rules, clipboard prohibition, and diagnostic redaction to the security implementation review.

