# Vault threat boundary

## v1 protection promise

The Vault protects Remote credentials, names, paths, task and schedule contents, and activity details when an attacker can copy, read, modify, or replace the Data Root while the application is locked or stopped.

The application authenticates encrypted records and fails closed on detectable corruption, record substitution, identity mismatch, unsupported formats, and invalid resource bounds. This does not protect an already-unlocked process from an administrator, kernel compromise, process-memory reader, keylogger, or equivalent execution-context attacker.

## Accepted metadata leakage

An attacker may observe the SQLite and filesystem structure, approximate record count, ciphertext sizes, generation count, file modification times, and patterns of recent activity. Plaintext storage is limited to information needed to locate, bound, authenticate, and recover encrypted records: random identifiers, format and key-generation data, revisions, nonces, tags, ciphertext lengths, and the active generation selector.

Remote/provider types, user labels, paths, task state, schedules, activity timestamps, and migration reasons remain encrypted. Filesystem timestamps and coarse structural changes are explicitly outside the confidentiality promise.

Sensitive records use versioned padding buckets of 1, 4, 16, 64, and 256 KiB. Larger content uses bounded segmented records. Before allocating or decrypting, enforce per-type ciphertext limits, record-count limits, total Vault limits, and parser depth limits. Invalid sizes enter corruption recovery; they are never repaired by guessing.

## Rollback boundary

v1 does not use machine-bound DPAPI state as a rollback detector. Such state would be non-portable, resettable, and prone to making ordinary backup restoration look malicious.

Show `OlderGenerationObserved` only when trustworthy comparison evidence is currently available, such as an authenticated transaction record, another valid newer generation, an explicit restore flow, or a higher generation already observed during the same run. A complete replacement with an older, internally valid Data Root may be indistinguishable from the current Vault and is not reliably detected.

When an older generation is observable, allow unlock but pause schedules, automatic Mounts, and automatic updates until the user explicitly continues or selects another Data Root. Describe the condition as an older version, not as a proven attack.

## Deletion and whole-file encryption

Do not claim physical secure deletion on SSD, USB, FAT, exFAT, or other flash-backed storage. Logical deletion removes current references and future copy-on-write generations omit deleted records; normal retention later removes old generations and snapshots. Media retirement requiring physical confidentiality needs full-volume encryption or device-level handling.

Whole-file SQLite encryption is not part of v1. Reconsider it only if the product promises to hide structure and activity, an enterprise/audit requirement demands it, or a maintainable and redistributable supported implementation passes update and recovery review. It would be defense in depth and would not replace per-record AEAD.

## Diagnostics and user disclosure

Default diagnostics include only format versions, health outcomes, typed error categories, and component versions. Record counts, ciphertext-size distributions, generation timelines, and schema inventories require a separate extended-diagnostics preview and opt-in. Never include ciphertext, key envelopes, DPAPI wrappers, or a complete Vault.

Use this concise disclosure when creating a Vault and exporting a backup:

> 密码会保护账号、路径和任务内容；持有数据文件的人仍可能看到文件大小、数量和最近修改时间。

English:

> Your password protects accounts, paths, and task contents. Someone with the data files may still see their sizes, counts, and recent modification times.
