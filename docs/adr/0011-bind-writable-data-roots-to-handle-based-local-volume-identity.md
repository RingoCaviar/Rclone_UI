---
status: accepted
---

# Bind writable Data Roots to handle-based local volume identity

Admit writable Data Roots only after binding an opened root directory to its Windows volume and directory file identity, rejecting unexpected reparse traversal, linked state files, UNC/network locations, mapped drives, and known cloud-synchronization roots. Path strings, drive letters, lock-file existence, PID payloads, and content hashes are not sufficient identity.

Hold one persistent `writer.lock` handle opened with `FileShare.None` for the full writable Background Host lifetime. Its payload is diagnostic only. Another Host may rewrite stale ownership data only after the operating system grants that same exclusive handle; it never deletes or breaks a live lock.

Local NTFS is approved for writable mode. FAT32/exFAT writable support remains release-gated on destructive removable-media testing of locking, disk-full, removal, selector replacement, flush, and recovery. Unsupported or unverified roots receive read-only diagnostics and relocation rather than weakened locking.

Recheck root identity after resume, media arrival, I/O anomalies, and before generation publication. Identity loss or change stops new mutations immediately. Recovery selects only a completely validated old or new copy-on-write generation; ambiguity enters guided read-only recovery and never creates an empty replacement Vault.
