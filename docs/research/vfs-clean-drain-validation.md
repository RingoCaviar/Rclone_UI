# VFS clean drain and forced-unmount recovery

## Strongest defensible predicate

Rclone does not expose a per-file durability proof or a complete open-handle oracle. `StoppedClean` therefore means that the Host observed every available safety signal, not that it can prove remote storage media durability.

Before unmount, all of the following must remain true for a configured quiet interval:

- both `vfs/stats` and `vfs/queue` are available and belong to the expected VFS;
- pending files, pending bytes, uploading files, failed uploads, and observable open files are zero;
- the cache is observable and does not report out-of-space;
- the remote is healthy and the owned rclone process/RC channel remains healthy.

After that drain, `StoppedClean` additionally requires the RC unmount request to be accepted and the owned Windows namespace to disappear within its deadline. An RC success response alone is insufficient.

## Failure semantics

- Open handles, active/failed uploads, outage, out-of-space, missing RC fields, cancellation, or an unstable quiet interval prevent safe unmount.
- RC unmount failure or a namespace that remains visible enters `RecoveryRequired` and preserves the cache manifest.
- Forced unmount always preserves recovery data unless the same complete clean predicate had already been proved; terminating rclone is supervision, not rollback or upload completion.
- Unknown numeric or Boolean fields never default to zero/false.

## Validation boundary

The typed manager tests fault each independent observation and teardown proof. A release qualification run must additionally capture raw `vfs/stats`, `vfs/queue`, remote contents, namespace state, and cache contents while inducing open handles, upload interruption, backend outage, cache exhaustion, cancellation, RC failure, and owned-process termination on the supported rclone/WinFsp matrix.

Those destructive/environmental runs belong in disposable Windows virtual machines. Recovery cache contents must be copied and hashed before the fixture is reset.
