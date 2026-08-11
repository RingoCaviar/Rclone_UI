# Windows Mount presentation and readiness validation

## Decision

`Ready` is a Host-owned conclusion, not the successful return of rclone's `mount/mount` RC call. A mount is Ready only when all of these observations agree:

- the RC request was accepted and the rclone process remains alive;
- the endpoint is registered and the requested Windows namespace is present;
- that namespace is owned by the new mount instance;
- an instance-specific marker is visible through the presented namespace;
- a mounted-root probe succeeds within a bounded deadline; and
- cache state is not known to be unobservable.

Any missing observation yields a stable actionable diagnostic. A failed startup must then prove that unmount was requested and the namespace disappeared within its cleanup deadline. If cleanup cannot be proved, the instance enters `RecoveryRequired`; it is never presented as safely stopped or automatically retried.

## Automated harness

Run `scripts/validate-windows-mount-readiness.ps1` with an rclone executable and output path. The harness uses a local backend and exercises:

- fixed-directory presentation;
- automatic fixed-drive allocation;
- a unique UNC network-drive namespace;
- RC return timing versus marker/root readiness;
- namespace ownership and actual Windows drive type; and
- bounded teardown, including whether rclone host exit was necessary.

The output records OS, token elevation, rclone version, WinFsp installation identity, per-mode timing, probes, and cleanup evidence. It fails closed when a probe or cleanup guarantee is absent.

## Observed Windows 11 result

On Windows 11 build 26200, under a normal unelevated token with the locally installed WinFsp SxS runtime:

- rclone v1.74.4 and v1.75.0 passed all three presentation modes;
- fixed-directory and fixed-drive namespaces disappeared after `mount/unmountall`;
- the network namespace could remain until the rclone RC daemon exited, so cleanup must be allowed to supervise and terminate that host rather than trusting `unmountall` alone.

## Remaining release evidence

The automated portion is complete. Release qualification still requires the same harness/output on:

- Windows 10 and Windows 11;
- normal and elevated tokens;
- the pinned minimum and latest supported WinFsp versions; and
- the pinned minimum and latest supported rclone versions.

Conflicted preferred drive letters and duplicate share names must also be rejected with their stable validation diagnostics. These are human/system-mutation gates because changing WinFsp versions and elevation context affects the host machine.
