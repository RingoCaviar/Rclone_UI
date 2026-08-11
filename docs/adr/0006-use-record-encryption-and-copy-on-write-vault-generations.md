---
status: accepted
---

# Use record encryption and copy-on-write Vault generations

Store Vault structure and transactions in Microsoft.Data.Sqlite while encrypting every sensitive record with AES-256-GCM under a random Vault Data Encryption Key; wrap that key independently with an RFC 9106 Argon2id-derived Master Password key and optional user-scoped DPAPI. Use one Background Host writer, rollback-journal SQLite, authenticated manifests, copy-on-write generations, an explicit migration phase journal, and verified `CURRENT` switching instead of relying on whole-file SQLite encryption or filesystem rename atomicity, because the format must remain portable and recover old-or-new valid state on NTFS and less reliable FAT/exFAT media without custom cryptographic primitives.
