---
status: accepted
---

# Own Mount readiness, drain, and recovery in the Background Host

Use rclone and WinFsp as the filesystem execution mechanism, while the Background Host owns Mount admission, stable instance identity, drive-letter policy, readiness evidence, safe-unmount drain, cache-risk classification, and crash recovery through the versioned `mount-rclone-winfsp-adapter/v1` contract.

A successful `mount/mount` call is not a Ready result. Ready requires the expected rclone/VFS registration, Windows namespace presentation, mounted-root probe, cache observation where applicable, and a live supervised process. Likewise, a successful `mount/unmount` call proves neither remote durability nor per-file verification. A clean stop requires conservative, stable queue and VFS observations followed by unmount and namespace reconciliation; missing evidence produces `CannotProveClean`, never an assumed zero or success.

There is no supported rclone RC primitive that atomically blocks new third-party opens, enumerates every open handle, drains and verifies all writes, and then unmounts. Force unmount is therefore an explicitly consented supervision action that preserves the cache and ends in recovery-required state whenever cleanliness was not already proved.

Saved Profiles retain semantic preset intent plus the exact resolved option and component capability snapshot. Runtime option changes require safe unmount, interrupted caches are recovered only with their original compatible contract, and unexplained drive mappings or corrupt caches are preserved for explicit inspection rather than automatically removed or cleared.
