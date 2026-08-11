# Data Root identity, locking, and recovery validation

Validation date: 2026-08-11  
Issue: #17  
Prototype evidence: branch `prototype/data-root-locking-recovery`, commit `60646f3`

## Verdict

The exclusive-handle design is valid for a local NTFS Data Root when combined with canonical volume/directory identity and a no-reparse policy. A persistent `writer.lock` opened with read/write access and `FileShare.None` rejected same-process, path-alias, and independent-process competitors in the prototype; after the owner closed, the same file could be reacquired without deleting a stale diagnostic payload.

Path text is not sufficient identity. Case and dot aliases can name the same root, reparse points can escape it, and hard links can give generation files multiple names. The Host must bind a writable root to its volume identity and opened root-directory identity, resolve every path component without following an unexpected reparse point, and reject linked/reparse-backed state files. Generation manifests must also reject unexpected link counts/file identities rather than authenticating only relative names.

Only local fixed or removable NTFS is release-approved for writable mode by this prototype. FAT32/exFAT remain **pending destructive-media validation**, not failed or assumed safe. UNC/network shares, mapped drives, cloud-synchronization roots, and roots containing/reached through reparse points are unsupported for writable mode in v1; offer read-only diagnostics and relocation. This restriction follows from the need for trustworthy exclusive locks and recovery semantics, not from whether ordinary file creation happens to work.

## Observed behavior

The throwaway harness created a unique directory under the Windows temporary directory and deleted only that directory. On Windows NT `10.0.26200.0`, fixed NTFS, it observed:

- case-insensitive and dot-segment aliases normalize to the same logical root;
- a held `FileShare.None` stream rejects both another handle and an independent child process;
- the live lock cannot be deleted under the selected sharing mode;
- after clean owner exit, the existing lock file can be exclusively reopened, establishing that payload age/file existence has no lock authority;
- a directory symbolic link to an outside directory can be detected by final-target resolution;
- an NTFS hard link mutates the same file through two names, proving that lexical containment and content hashes alone do not prove unique storage identity;
- interruption before selector creation or after flushing `CURRENT.new` leaves the old selected generation valid;
- replacing `CURRENT` selects the already-complete new generation.

The selector experiment models ordering and reconciliation, not physical power-loss durability. It did not test firmware that lies about flushes or cross-filesystem rename behavior.

## Writable-root admission contract

Before creating or opening `writer.lock`, the Background Host must:

1. obtain an absolute normalized path without relying on string case as identity;
2. open the root directory without traversing an unexpected reparse point and retain a handle while admitting it;
3. obtain and persist a stable volume identity plus directory file identity using Windows handle-based APIs;
4. require a local Windows volume with a supported filesystem policy; reject UNC, mapped/network, and known cloud-placeholder/synchronization roots;
5. walk the root-to-state path and reject reparse points; require state files and generation members to be regular files/directories owned by the expected root;
6. open the persistent `writer.lock` with `FileShare.None` and keep that exact handle for the entire writable Host lifetime;
7. recheck volume/root identity after every media arrival, resume, I/O anomaly, and before publishing a generation;
8. enter `DataRootUnavailable` immediately on identity change or disappearance, stop admitting new mutations, and buffer only bounded non-secret status in memory.

The diagnostic payload contains instance ID, machine/user identity, PID, process start time, Host epoch, root identity, and protocol version. It is informational. A new Host rewrites it only after successfully acquiring the exclusive handle; it never deletes or “breaks” a lock that it cannot acquire.

## Generation and failure contract

All staged generation files remain on the same admitted volume. A new generation becomes eligible only after it is closed, flushed, structurally checked, cryptographically authenticated, and its manifest verifies file identities/link policy. Only then may a flushed selector replacement activate it.

On startup or media return, recovery enumerates candidates read-only and chooses only:

- the old authenticated generation when the selector did not switch;
- the new authenticated generation when the selector switched and it fully validates;
- otherwise no writable generation, entering guided recovery.

Disk-full, removal, write-protection, and arbitrary I/O errors are treated identically at the policy boundary: abort mutation, preserve all candidates, release no claim of success, and require the volume/root identity plus generation set to reconcile before write mode resumes. The Host never creates an empty replacement Vault after an I/O anomaly.

## Unsupported or unverified cases

- FAT32/exFAT lock, rename, flush, removal, and recovery behavior on representative disposable media.
- Real disk-full at every file/journal/selector boundary.
- Physical removal and reinsertion with the same drive letter but different volume identity.
- USB controllers that acknowledge but do not durably persist flushes.
- Network/SMB locking and cloud-sync filter-driver behavior; these remain unsupported rather than scheduled for v1 qualification.
- Alias behavior involving volume mount points, short 8.3 paths, subst drives, and unusual reparse tags; admission must fail closed until covered.

## Prototype disposition

The destructive harness is intentionally absent from `main`. It remains primary evidence on [`prototype/data-root-locking-recovery`](https://github.com/RingoCaviar/Rclone_UI/tree/prototype/data-root-locking-recovery/prototypes/data-root-locking-recovery) at commit `60646f3`.
