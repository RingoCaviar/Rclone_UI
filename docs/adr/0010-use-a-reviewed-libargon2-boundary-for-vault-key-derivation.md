---
status: accepted
---

# Use a reviewed libargon2 boundary for Vault key derivation

Use a bundled, verified x64 build of the official `libargon2` reference implementation behind a minimal P/Invoke adapter for Master Password key derivation. Do not use `BouncyCastle.Cryptography` 2.6.2 for this boundary: it passed the RFC 9106 Argon2id vector and met latency expectations on the development machine, but its managed API and source contract do not provide sufficient evidence for the Vault's strict intermediate-secret lifetime requirement.

Persist Argon2id v1.3 parameters with a floor of 64 MiB, t=3, and p=4; validate all loaded parameters against explicit lower and upper bounds before allocation. Default to one derivation at a time, coalesce duplicate unlock requests, and admit additional work only within a measured memory budget. Pin and explicitly clear caller-owned buffers, verify the native library digest before loading, test the exact shipped binary against official vectors, and record its implementation identity in Vault metadata.

Acceptance remains conditional on a low-end Windows 10/11 hardware spike meeting the defined latency and memory-pressure gates. Failure of that gate requires revisiting the supported hardware floor or an explicit security decision, never silently weakening an existing Vault.
