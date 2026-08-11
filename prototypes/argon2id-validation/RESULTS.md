# Prototype result

Environment: Windows NT 10.0.26200.0 x64, .NET 8.0.23, 32 logical processors.  
Package: BouncyCastle.Cryptography `2.6.2+b4f2f6ad76`.  
Parameters: Argon2id v1.3, 64 MiB, t=3, p=4, 32-byte output.

- RFC 9106 Section 5.3 vector: PASS (`0D640DF58D78766C08C037A34A8B53C9D01EF0452D75B65EB52520E96B01E659`).
- Sequential samples (ms): 137.00, 137.58, 128.82, 138.59, 125.13, 136.96, 132.77.
- Median: 136.96 ms. Maximum of seven samples: 138.59 ms.
- Concurrent elapsed: one 119.41 ms, two 163.26 ms, four 204.96 ms.
- Four-worker theoretical Argon2 workspace: 256 MiB; observed process peak working set: 321.76 MiB.

These measurements characterize only this machine. They do not establish the Windows 10 minimum-hardware latency gate or a reliable process-memory delta; a controlled low-end hardware run remains necessary.

Source inspection at the pinned release shows `Argon2BytesGenerator.Reset` clears its main block memory after derivation, while `Argon2Parameters.Clear` must be called separately to clear cloned salt/secret/additional values. The public contract does not prove that all password/intermediate buffers receive non-elidable clearing. This fails the product's strict secret-lifetime requirement even though functional correctness passed.
