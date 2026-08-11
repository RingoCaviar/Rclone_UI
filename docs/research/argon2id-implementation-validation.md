# Argon2id implementation validation

Research and prototype date: 2026-08-11  
Issue: #16  
Prototype evidence: branch `prototype/argon2id-validation`, commit `89f111a`

## Verdict

Do not use `BouncyCastle.Cryptography` 2.6.2 as the production Argon2id boundary. It is functionally correct against the RFC 9106 Argon2id v1.3 vector and fast on the available development machine, but its public API/source contract does not satisfy the Vault's strict secret-lifetime requirement. Proceed with a small, reviewed P/Invoke boundary to the official `libargon2` reference implementation, built with memory clearing enabled and wrapped by bounded parameter validation, concurrency admission, pinned native-binary verification, and explicit caller-buffer zeroing.

Keep the accepted persisted floor at Argon2id v1.3, 64 MiB, t=3, p=4, 128-bit random salt, and a 256-bit derived wrapping key. Treat parameters read from a Vault as hostile input: validate version/type, salt and output sizes, memory, iterations, and lanes against both minimums and application maximums before allocating. Serialize or tightly bound password derivations so four concurrent unlocks cannot be triggered accidentally by UI retries.

## Evidence

The throwaway .NET 8 harness pins `BouncyCastle.Cryptography` 2.6.2 and passes the complete RFC 9106 Section 5.3 Argon2id vector. RFC 9106 describes Argon2 v1.3, recommends Argon2id when the side-channel choice is uncertain, recommends four lanes, and explicitly tells implementations to enable memory wiping when available ([RFC 9106](https://www.rfc-editor.org/rfc/rfc9106.html)). The pinned package and source identity are recorded by the harness ([NuGet package](https://www.nuget.org/packages/BouncyCastle.Cryptography/2.6.2), [Bouncy Castle C# source](https://github.com/bcgit/bc-csharp/tree/release-2.6.2)).

On the available Windows x64 machine (`10.0.26200.0`, .NET 8.0.23, 32 logical processors), the 64 MiB/t=3/p=4 configuration produced seven sequential samples between 125.13 and 138.59 ms, with a 136.96 ms median. Four concurrent calls completed in 204.96 ms, with a theoretical 256 MiB Argon2 workspace and 321.76 MiB observed process peak working set. These are development-machine observations, not proof of minimum Windows 10 hardware performance.

Source review of release 2.6.2 shows that `Argon2BytesGenerator.Reset` clears its main block array after derivation and `Argon2Parameters.Clear` separately clears cloned salt, secret, and associated-data arrays. The byte-array password remains caller-owned, and the public contract does not prove non-elidable clearing of every password-derived/intermediate managed buffer. Functional vector success cannot establish secret erasure. The official reference implementation exposes its memory-clearing behavior in a smaller native boundary that can be reviewed and pinned ([official Argon2 reference implementation](https://github.com/P-H-C/phc-winner-argon2)).

## Required production boundary

The native adapter must expose one operation equivalent to:

```text
DeriveArgon2idV13(
  passwordBytes,
  salt16,
  memoryKiB: bounded >= 65536,
  iterations: bounded >= 3,
  lanes: bounded >= 4,
  output32) -> Success | InvalidParameters | ResourceUnavailable | Failure
```

Requirements:

- Bundle and load only the verified x64 library from the active application version directory; never search `PATH`.
- Validate hostile persisted parameters before native allocation. Establish explicit product ceilings through the minimum-hardware test rather than accepting RFC-format maxima.
- Permit one derivation by default. A second derivation is admitted only under a measured memory budget; UI duplicate requests coalesce.
- Pin input/output buffers for the call, prevent copies where practical, clear caller-owned password and output buffers with `CryptographicOperations.ZeroMemory`, and verify the native build's internal clearing configuration/source.
- Return only typed error categories. Never log passwords, derived keys, salts together with Vault identifiers, or native buffer contents.
- Record implementation ID, library digest, Argon2 version, and parameters in the Vault generation metadata so migration and rewrapping are explicit.
- Run official vectors in CI against the exact shipped native binary and run negative tests for overflow, excessive parameters, cancellation expectations, and concurrent memory pressure.

## Performance gate still required

The available environment cannot answer the minimum Windows 10 hardware question. Before implementation acceptance, run the pinned native candidate and the Bouncy Castle harness as a comparison on the declared minimum x64 hardware class, cold and warm, under normal memory pressure. Recommended acceptance gate:

- single derivation p95 at or below 1.5 seconds;
- no unhandled allocation failure at the 64 MiB floor;
- two admitted concurrent derivations remain responsive without paging collapse;
- requests beyond the concurrency budget queue rather than allocate;
- the derived tag matches RFC and cross-implementation fixtures.

If the floor exceeds the latency gate, do not silently lower stored security parameters. Revisit the supported hardware floor or present a deliberate migration decision.

## Prototype disposition

Per the prototype policy, the harness is intentionally absent from `main`. It is preserved as primary evidence on [`prototype/argon2id-validation`](https://github.com/RingoCaviar/Rclone_UI/tree/prototype/argon2id-validation/prototypes/argon2id-validation) at commit `89f111a`. Main retains only this verdict and the architecture decision.
