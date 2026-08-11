# VFS cutoff and interrupted-cache recovery options

## Decision

`max-transfer` and `cutoff-mode` do not have a sufficiently documented Mount/VFS writeback safety contract. They remain outside the guarantees of the built-in read-only, standard read/write, and maximum-compatibility presets. Supplying either setting to those presets is rejected; a custom profile must provide both and is treated as an advanced, non-guaranteed configuration.

Cutoff errors must never be interpreted as a clean drain. Queue, stats, quiet-interval, remote, cache, and post-unmount evidence remain mandatory.

## Recovery contract

Every writable mount persists a binding over the exact resolved recovery inputs, including:

- rclone binary and option-schema identity;
- backend, remote and subpath identity;
- cache root and cache mode;
- mount and VFS option snapshots, including writeback timing;
- mount implementation and Windows presentation mode; and
- any custom cutoff/transfer limit.

Crash-left cache data may only be reattached when the observed contract binding exactly matches the persisted binding. Changed cache paths or Mount/VFS flags enter `RecoveryRequired`; the Host does not start rclone against that cache and does not rewrite the old contract. An identical contract may reattach only the original journaled mount instance.

## Remaining destructive validation

In disposable Windows 10/11 virtual machines, create pending writes, terminate rclone before upload, and retain hashes of the cache and remote. Restart once with the identical resolved contract and once for each changed cache/mount/VFS flag. Capture queue, stats, cache and remote contents until terminal state.

Also exercise soft, hard and cautious cutoff during active VFS writeback. Unless those runs establish stable behavior across the minimum/latest rclone and WinFsp matrix, the UI must continue describing cutoff as experimental custom behavior rather than a capacity, pause, durability or recovery guarantee.
