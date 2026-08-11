---
status: accepted
---

# Treat rclone as an execution engine, not the safety authority

Use the bundled, capability-discovered rclone RC API to execute transfers, listings, checks, and narrowly scoped file operations, while the Background Host owns the immutable preview contract, conflict policy, confirmations, phase transitions, durable per-path evidence, retention, retries, cancellation barriers, and business-level terminal results.

Do not implement the accepted Move contract with `sync/move`: execute Copy, verify the frozen manifest, revalidate each source, and then delete only verified source files. Likewise, when Mirror deletions are gated by post-transfer High-Assurance verification, split copying, verification, and deletion into separate Host-controlled phases rather than relying on one `sync/sync` call.

Dry-run logger output is advisory planning evidence, not an authoritative manifest. Every execution request is bound to the accepted preview, exact rclone binary and capability snapshot, canonical endpoints, complete safety-relevant `_config` and `_filter` values, and a versioned adapter contract. Missing or changed capabilities fail closed instead of silently weakening the selected safety policy.

This separation costs additional listings, verification reads, storage, and orchestration complexity, but it is necessary because rclone operations are not transactions, RC progress history is bounded and ephemeral, backup directories have no retention policy, cancellation is cooperative, and backend feature flags do not guarantee atomicity.
