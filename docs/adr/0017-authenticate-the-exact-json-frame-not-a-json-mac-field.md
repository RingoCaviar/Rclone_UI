---
status: accepted
---

# Authenticate the exact JSON frame, not a JSON MAC field

Frame Host IPC as a little-endian 32-bit JSON length, up to 8 MiB, followed by exact strict UTF-8 JSON bytes and a 32-byte HMAC-SHA-256 over the length prefix plus JSON. Keep the MAC outside the envelope so all fields, including unknown additive fields, are authenticated without JSON canonicalization or a self-referential MAC property.

Reject oversized lengths before allocation and verify the fixed-size MAC before strict JSON parsing. Reject invalid UTF-8, duplicate keys, ambiguous JSON, missing/invalid fields, unknown message types, and sequence replay or gaps. Use a fresh handshake-derived connection key and independent strictly increasing sequences in each direction.

Protocol major changes are breaking; compatible peers negotiate the highest overlapping additive minor. Mutations use durable idempotency keys bound to semantic request hashes, and UI state uses epoch plus contiguous revision with mandatory resnapshot on gaps or epoch change. The contract prototype passed 25/25 cases; production remains release-gated on cross-version golden assemblies, named-pipe integration, durable idempotency, and fuzzing.
