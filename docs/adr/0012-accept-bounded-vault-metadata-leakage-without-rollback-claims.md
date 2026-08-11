---
status: accepted
---

# Accept bounded Vault metadata leakage without rollback claims

For v1, keep application-layer per-record authenticated encryption and accept that an offline Data Root observer can see coarse database structure, ciphertext sizes, generation counts, filesystem timestamps, and activity patterns. Minimize plaintext metadata and use bounded, versioned padding buckets for sensitive records, but do not add a whole-file encryption dependency solely to conceal structure.

Do not claim reliable rollback detection. A complete replacement by an older, internally valid portable Data Root cannot be distinguished without trusted external state. Do not add DPAPI generation memory because it is machine-bound, resettable, and likely to create false confidence and backup-restore ambiguity. Warn about an older generation only when authenticated comparison evidence actually exists.

Do not promise physical secure deletion on flash storage. Copy-on-write generations omit logically deleted records and retention removes old generations, while device retirement remains a full-volume or hardware-level concern. Default diagnostics exclude Vault structural statistics and all cryptographic artifacts; expanded structural diagnostics require explicit preview and consent.
