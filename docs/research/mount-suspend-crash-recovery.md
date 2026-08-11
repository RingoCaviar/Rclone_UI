# Mount suspend, crash, and stale namespace recovery

## Reconciliation invariants

Reconciliation operates on the journaled `MountInstanceId` and its immutable profile revision. It does not create a replacement instance, allocate a new drive letter, or call start/stop/cleanup while deciding ownership.

- An owned mapping that still satisfies the complete Ready predicate remains attached to its original instance and presentation target.
- A missing or delayed mapping enters `RecoveryRequired`; the Host does not automatically remount it.
- A mapping whose ownership marker does not match is attributed to nobody and is never removed by reconciliation.
- Host/rclone termination is recorded as an interruption, not interpreted as a clean unmount.
- Missing removable cache media is reported as `CorruptCache` / `recovery-cache-missing`.
- The first uncertain reconciliation creates one recovery-cache record. Later passes reuse that reference.
- If a mapping reappears after recovery was required, the mapping can be observed but the recovery record and review-required state are not cleared automatically.

These rules make repeated Host starts idempotent and prevent delayed network-provider mappings from causing duplicate drive allocation.

## Release qualification matrix

Run the following in disposable Windows 10 and Windows 11 virtual machines and retain the journal, namespace inventory, RC evidence, and recovery manifest after every restart:

- sleep and resume, then hibernate and resume;
- terminate only the GUI, only the Host, and only the owned rclone process;
- disconnect and reconnect removable cache media;
- introduce WinFsp and network-provider drive/share conflicts;
- delay drive presentation beyond the normal readiness deadline; and
- inject a stale mapping with an absent or foreign ownership marker.

Every run must retain the original instance and requested target, create no duplicate namespace, leave unattributed mappings untouched, and preserve recovery data until an explicit recovery workflow clears it.
