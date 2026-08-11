# Prototype result

Contract: `host-ipc/v1`  
Frame: `uint32-le jsonLength | strict UTF-8 JSON | HMAC-SHA-256(length || JSON)`  
Maximum JSON length: 8 MiB.

All 25 deterministic cases passed:

- golden encode/decode;
- truncated prefix, JSON, and MAC;
- oversized length rejected before payload allocation;
- invalid MAC and invalid UTF-8;
- duplicate JSON property and missing required field;
- unknown message type;
- correct sequence, replay rejection, and gap rejection;
- compatible minor negotiation, major mismatch, and no minor overlap;
- authenticated unknown additive field;
- idempotency first execution, same-request replay, and conflicting reuse;
- next state revision, revision gap, and epoch change;
- expired deadline recognition.

The generated golden frame is printed by the harness. This is a deterministic contract experiment, not a production parser or a replacement for coverage-guided fuzzing and cross-assembly/version fixtures.
