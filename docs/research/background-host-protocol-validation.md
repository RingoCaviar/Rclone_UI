# Background Host protocol framing and compatibility validation

Validation date: 2026-08-11  
Issue: #23  
Prototype evidence: branch `prototype/ipc-protocol-contract`, commit `73077c6`

## Verdict

Use protocol `host-ipc/v1` with this authenticated frame:

```text
uint32 little-endian jsonLength (0..8 MiB)
jsonLength bytes of strict UTF-8 JSON
32 bytes HMAC-SHA-256 over (length prefix || exact JSON bytes)
```

Keep the MAC outside JSON. This authenticates the length, exact serialized envelope, body, and unknown additive fields without requiring JSON canonicalization or a self-referential `mac` property. Read exactly four bytes first, reject lengths above 8 MiB before allocating payload storage, then read the exact JSON and fixed MAC under a deadline.

The deterministic harness passed 25/25 framing, parser, authentication, sequencing, negotiation, idempotency, deadline, and state-revision cases. This validates the contract shape, not a production parser or full fuzz campaign.

## Envelope v1

Required JSON fields:

```text
protocolMajor: integer
protocolMinor: integer
messageType: stable string enum
requestId: bounded opaque ID
sequence: unsigned 64-bit integer
stateEpoch: bounded opaque ID
stateRevision: unsigned 64-bit integer
deadlineUtc: RFC 3339 UTC timestamp
idempotencyKey: bounded opaque ID
body: object whose schema is selected by messageType
```

The negotiated feature/capability set determines whether an additive body field or operation is usable. Unknown top-level fields are retained or ignored only after the entire raw frame authenticates. Unknown message types and unknown required body variants fail closed.

Hello/HelloAck use the incarnation challenge key from secured discovery to authenticate their frames. After both nonces and protocol selection are verified, derive a per-connection key with HKDF-SHA-256 and use it for subsequent frames. Sequence numbers are independent and strictly increasing per direction, starting at 1. A replay, duplicate, zero, overflow, or gap disconnects the connection; reconnect performs a new handshake and receives a new key.

## Strict parser contract

- Reject oversized length before payload allocation.
- Require exactly `4 + jsonLength + 32` bytes for a complete frame; streaming implementation may buffer incrementally but cannot accept trailing bytes as part of the same frame.
- Verify HMAC in constant time before parsing attacker-controlled JSON beyond the bounded buffer.
- Reject invalid UTF-8, BOM, comments, trailing commas, duplicate property names at every object depth, excessive nesting, non-object envelope, missing required fields, invalid types, unsupported message types, non-finite/ambiguous numbers, and numeric overflow.
- Bound all strings, arrays, map entries, body-specific counts, and decompressed/decoded content. Protocol v1 has no frame compression.
- Never deserialize .NET type names or exception objects. Errors are typed codes plus bounded redacted details.

## Compatibility

Each side advertises one protocol major and an inclusive supported minor range. Connection is allowed only when majors match and minor ranges overlap; select the highest common minor. A major mismatch permits only the separately authenticated incompatibility/update UX already defined, never operational commands.

Minor versions are additive. New optional fields may be ignored by an older peer only when the negotiated capability says they are noncritical. Removing/renaming fields, changing units or meanings, tightening an accepted enum in an incompatible way, or changing authentication/framing requires a new major.

Maintain golden frames for:

- current UI/current Host;
- previous UI/current Host;
- current UI/previous Host;
- lowest supported minor overlap;
- no overlap and major mismatch;
- unknown additive fields in outer envelope and representative bodies;
- every stable error/result enum and unit-bearing value.

## Idempotency, deadline, and state

- Mutation requests require a unique idempotency key and a canonical semantic request hash computed after schema validation. First use executes; identical reuse returns the recorded result; reuse with a different semantic hash is `IdempotencyConflict`.
- Persist idempotency intent/result for mutations whose side effects survive Host restart. Expiry cannot be shorter than the maximum retry/reconciliation window.
- Reject an already-expired request before mutation. A deadline bounds admission and waiting; it does not roll back work already committed.
- Responses echo `requestId` and the authoritative state epoch/revision.
- After handshake, the UI obtains a snapshot `(epoch, revision)` and accepts only event `revision + 1` in that epoch. A gap, duplicate outside replay rules, or epoch change requires a fresh snapshot; never guess missing state.
- Cancellation has its own idempotency key and returns requested/acknowledged/domain-result states rather than claiming rollback.

## Prototype cases

Passed cases included golden roundtrip; truncated prefix/JSON/MAC; oversized prefix; invalid MAC/UTF-8; duplicate/missing fields; unknown message; replay/gap; major/minor negotiation; authenticated additive field; idempotency replay/conflict; state gap/epoch change; and expired deadline.

## Remaining release work

- production incremental parser with bounded pooled memory and cancellation;
- coverage-guided fuzzing plus corpus regression;
- generated current/previous contract assemblies exchanging real frames;
- handshake/HKDF golden vectors and bidirectional sequence tests over actual named pipes;
- durable idempotency behavior across disconnect-before-response and Host restart;
- large paged snapshots/events, backpressure, slow reader/writer, deadline, and cancellation;
- secret/redaction scans and performance on Windows 10/11 selected .NET LTS.

## Prototype disposition

The harness remains off `main` as primary evidence on [`prototype/ipc-protocol-contract`](https://github.com/RingoCaviar/Rclone_UI/tree/prototype/ipc-protocol-contract/prototypes/ipc-protocol-contract) at commit `73077c6`.
