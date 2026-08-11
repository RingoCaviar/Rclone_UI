# Mount lifecycle mapping to rclone, WinFsp, and Windows

Status: recommendation for Issue #15  
Research date: 2026-08-11  
Contract: `mount-rclone-winfsp-adapter/v1`  
Scope: Windows 10/11 x64, an unelevated per-user Background Host, managed rclone and WinFsp

## Recommendation

Treat rclone and WinFsp as the filesystem execution mechanism, not as the Mount lifecycle authority. The Background Host owns Profile identity, admission checks, drive-letter policy, readiness, safe-unmount barriers, cache-risk classification, crash reconciliation, and truthful user-visible outcomes. A successful `mount/mount` call means only that rclone accepted and created a mount; a successful `mount/unmount` means only that rclone's unmount implementation returned without error. Neither proves that cached writes reached the Remote.

Use the documented RC endpoints `mount/types`, `mount/mount`, `mount/listmounts`, `mount/unmount`, `vfs/list`, `vfs/stats`, `vfs/queue`, `vfs/refresh`, and `vfs/poll-interval`, but discover each endpoint and its live option schema from `rc/list` and `options/info`. rclone documents `mountOpt` and `vfsOpt` as option objects and also accepts flat CLI-derived names; nested objects take precedence. The adapter shall emit only nested, typed objects generated from the exact managed binary's schema and shall reject unknown saved options until the user reviews them rather than silently dropping them ([rclone RC mount API](https://rclone.org/rc/#mountmount-create-a-new-mount-point), [rclone option blocks](https://rclone.org/rc/#option-blocks)).

## Capability classes

| Class | Meaning | UI rule |
|---|---|---|
| `Direct` | A documented endpoint/field provides the mechanism, but normal operational errors remain possible. | Enable after schema and platform validation. |
| `Observed` | The Host can combine multiple observations into evidence, but there is no atomic guarantee. | Label the observation time and uncertainty. |
| `Orchestrated` | The Host must persist intent, sequence calls, impose policy, and reconcile failures. | Never attribute the guarantee to rclone or WinFsp. |
| `Unavailable` | No supported interface proves the requested fact. | Hide the claim or show `Unknown`; never synthesize success. |

Capabilities have the runtime states `available`, `unavailable`, `attemptable`, and `unknown`. Endpoint presence alone is not backend health; a mount remains attemptable until its preflight and readiness probe succeed.

## Requirement mapping

### Profile, Remote, and subpath

| Product requirement | Mechanism | Class and limit |
|---|---|---|
| Stable Profile identity/display name across Remote rename | Application UUID and Vault references; never use the rclone remote name or mount point as identity. | `Orchestrated`. rclone mount identity is only the active `fs`/mount-point pair. |
| Unique volume/share name | Validate all saved/active Profiles before launch; send live `mountOpt.VolName` (schema spelling discovered from `options/info`). | `Orchestrated`. In Windows network mode rclone derives a UNC share from `--volname`, and explicitly requires uniqueness for concurrent mounts ([Windows mount modes](https://rclone.org/commands/rclone_mount/#mounting-modes-on-windows)). |
| Exact subpath exists and is listable | Call `operations/stat`/`operations/list` against the exact Remote subpath, then mount that exact `fs`; fail on missing/inaccessible path. | `Orchestrated`; preflight is a point-in-time check. Never fall back to root. |
| Remote health | Authenticated `rc/noopauth`, exact-path list/stat, then a bounded post-mount root probe. | `Observed`; no check predicts continued cloud availability. |

### Presets and option mapping

Persist semantic preset intent plus the resolved option snapshot. Do not persist enum ordinals as the durable format; rclone's RC data model permits enumerations as integers but recommends strings, while sizes are bytes/size strings and durations are nanoseconds/duration strings ([RC data types](https://rclone.org/rc/#data-types)). Resolve these against `options/info blocks=mount,vfs` for each bundled binary.

| Preset | Required resolved intent | Direct rclone mechanism | Limit |
|---|---|---|---|
| Read-only browsing | `readOnly=true`, minimal write cache, explicit cache directory | `mountOpt.ReadOnly`; VFS cache `off` or `minimal` only if the live schema accepts it | Read-only is a filesystem behavior, not an offline snapshot. Some applications give poor write errors, so UI labeling remains required. |
| Standard read/write | writes cache, 10 GiB soft target, explicit per-Profile cache | `vfsOpt.CacheMode="writes"`, `CacheMaxSize=10 GiB`, `CacheDir=<profile path>` | VFS cache enables ordinary random writes; cloud filesystems remain less reliable than local disks ([VFS caching](https://rclone.org/commands/rclone_mount/#vfs-file-caching)). |
| Maximum compatibility | full cache, 20 GiB soft target | `CacheMode="full"`, `CacheMaxSize=20 GiB`, explicit cache path | Higher local I/O and storage use; it does not make the Remote transactional or fully offline. |
| Custom | discovered `mount`/`vfs` fields only | `options/info` metadata and `mount/mount` nested objects | Schema exposure is direct, semantic safety classification is application-owned. Dangerous or newly unknown fields require review. |

`CacheMaxSize`, `CacheMinFreeSpace`, and `CacheMaxAge` are cache cleanup controls, not hard reservation/accounting guarantees. rclone documents that open files cannot be evicted and the cache may exceed maximum size; therefore the Host must preflight free space, report actual `vfs/stats.diskCache.bytesUsed/outOfSpace`, and never claim a hard quota ([VFS cache options](https://rclone.org/commands/rclone_mount/#vfs-file-caching), [vfs/stats](https://rclone.org/rc/#vfsstats-stats-for-a-vfs)). The Host must not delete cache files directly during an active VFS.

Assign one exclusive cache root to each Profile and reject simultaneously active mounts whose cache roots overlap. rclone also warns that two mounts of the same or overlapping Remote must not share a VFS cache because corruption can result. Pending files left after process exit are retried when rclone restarts with the same relevant flags; the Host must therefore preserve the resolved recovery contract and must not “repair” an interrupted cache using newly edited options ([VFS cache options](https://rclone.org/commands/rclone_mount/#vfs-file-caching)).

### Windows presentation, drive identity, and UAC

rclone documents two different Windows presentations. Fixed mode is the default and supports a letter or a directory mount; `--network-mode` makes a network drive and requires a letter. A UNC `volname` or UNC mount point also implies network mode. Windows treats fixed storage as fast/reliable and network storage as higher latency, so this product's cloud default remains network mode ([rclone Windows mount modes](https://rclone.org/commands/rclone_mount/#mounting-modes-on-windows)). WinFsp implements these using distinct `\Device\WinFsp.Disk` and `\Device\WinFsp.Net` constructors; network filesystems register with MUP, whereas disk filesystems get a virtual volume device ([WinFsp design](https://winfsp.dev/doc/WinFsp-Design/)).

| Requirement | Mapping |
|---|---|
| Network/fixed selection | `mountOpt.NetworkMode` (live field name required). Also validate `VolName`: a UNC value implies network mode. Directory mounts are rejected for network mode. |
| Unelevated visibility | Launch the Host/rclone in the interactive user's non-elevated logon context. rclone states that drives created elevated are not visible to the normal Explorer token and recommends non-elevated mounting ([Windows caveats](https://rclone.org/commands/rclone_mount/#windows-caveats)). |
| No linked-connections/service/SYSTEM mutation | Application policy; do not edit `EnableLinkedConnections`, configure WinFsp Launcher, or elevate the Host. rclone documents these only as alternatives with different visibility/ownership consequences, not as requirements. |
| Administrator-only application | Offer a fixed directory mount as a separately validated alternative; rclone states directory mounts avoid the drive-letter elevation limitation. Do not promise this solves every application's ACL/identity requirements. |
| Preferred letter | Preflight `GetLogicalDrives`, then request the explicit `X:`. Microsoft defines set bits as currently assigned local, removable, or mapped drives ([GetLogicalDrives](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getlogicaldrives)). Recheck immediately before the RC call; races remain possible. |
| Automatic letter | Send `mountPoint="*"`; rclone chooses from Z backward and returns the actual mount point. Persist it only after readiness succeeds ([mount/mount](https://rclone.org/rc/#mountmount-create-a-new-mount-point)). |
| Conflict/no silent fallback | If a remembered explicit letter is occupied, enter `DriveLetterConflict`; never substitute `*`. This is application policy. |
| Stable active identity | Key the active record by `mountInstanceId`, Profile revision, rclone incarnation, returned mount point, normalized `fs`, process identity, and start time. Letter and volume label alone are not identity. |

Drive-letter enumeration cannot prove ownership. During reconciliation combine `mount/listmounts`, `vfs/list`, `GetLogicalDrives`, `QueryDosDevice`, process identity, and the journal. Microsoft notes `QueryDosDevice` retrieves current MS-DOS device mappings and may expose both current and undeleted prior mappings, so an unexplained letter is `ForeignOrStale`, never automatically removed ([QueryDosDevice](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-querydosdevicew)).

### Start and readiness

`mount/mount` accepts `fs`, `mountPoint`, optional `mountType`, `mountOpt`, and `vfsOpt`, returning the actual point. `mount/types` discovers compiled implementations; rclone prioritizes `mount`, `cmount`, then `mount2` if no type is supplied. The adapter must choose an explicitly tested type and fail closed if it disappears ([mount RC API](https://rclone.org/rc/#mountmount-create-a-new-mount-point)).

Startup admission is application-owned: operational unlocked session; exact Remote/subpath test; compatible rclone plus required RC endpoints/fields; supported WinFsp architecture/version; available explicit letter; unique share/volume; writable cache directory with sufficient free space; and no interrupted instance requiring recovery.

Readiness is a bounded evidence gate, not the RC response alone:

1. Persist `StartRequested` with Profile/options/capability hashes.
2. Call `mount/mount`; record returned mount point.
3. Require the point in `mount/listmounts` and matching VFS in `vfs/list`.
4. Confirm Windows exposes the requested/returned letter or directory and that it resolves to the expected presentation.
5. Perform a bounded root metadata/list probe through the mounted path; for writable presets also confirm `vfs/stats` identifies the expected cache.
6. Require the managed rclone process to remain alive through a stabilization interval.
7. Publish `Ready`, or attempt one bounded cleanup and publish a single actionable failure. Never repeatedly remount or loop UAC.

Windows rclone mounts run in the foreground and normally disappear when the process ends; WinFsp closes filesystem handles on process termination and is designed to delete its volume objects ([rclone mount](https://rclone.org/commands/rclone_mount/), [WinFsp design](https://winfsp.dev/doc/WinFsp-Design/)). This supports crash detection but does not prove every drive mapping is immediately absent, hence reconciliation remains mandatory.

### Cache, pending writes, and observability

| Requested fact/action | Source | Truthful interpretation |
|---|---|---|
| Cache bytes, files, errors, uploads queued/in progress, out-of-space, paths | `vfs/stats fs=<exact VFS>` | Direct snapshot. Fields are optional/versioned; missing fields become `Unknown`, not zero. The documented response has no per-file dirty/verified flag ([vfs/stats](https://rclone.org/rc/#vfsstats-stats-for-a-vfs)). |
| Pending items | `vfs/queue fs=<exact VFS>` when cache mode > off | Returns name, id, size, expiry, tries, delay, uploading. `expiry` is eligibility time, not creation/dirty time; negative does not mean actively uploading ([vfs/queue](https://rclone.org/rc/#vfsqueue-queue-info-for-a-vfs)). |
| Pending count/bytes | Aggregate the current queue and reconcile with `vfs/stats`. | `Observed`; disagreement or endpoint loss is `Unknown/Risk`, never clean. |
| Oldest pending age | Host timestamps first observation and persists a privacy-safe queue fingerprint. | `Orchestrated`; rclone's queue does not expose creation time, so after crash the true age may be unknown. |
| Last upload attempt/error | Host event/log correlation plus queue `tries`/`delay`. | `Observed`; structured queue does not carry last error or time. Human logs are diagnostics, not a durable contract. |
| Open files | `vfs/stats.inUse` is only an aggregate VFS usage indicator; cache docs state open files are not evictable. | Per-file open-handle enumeration is `Unavailable` through documented RC. Do not show a fabricated list. Optional Windows Restart Manager can identify some processes holding specific known resources, but it is not a complete filesystem-open-handle oracle. |
| Dirty/verified cache items | Queue presence and upload state are evidence of pending upload. | No documented RC proves all cached items remotely verified. Absence of queue plus zero queued/in-progress/errors is necessary but not sufficient after interruption. |
| Soft capacity recovery | Let rclone's configured VFS cleanup manage eligible entries; warn from free-space and stats. | Do not independently evict active cache. rclone will not evict open files and may exceed `CacheMaxSize`; dirty/recovery quarantine is Host policy. |

Do not advertise complete offline sync. Cached reads and delayed cached writes depend on VFS mode, retained chunks/files, backend behavior, cache capacity, and rclone lifetime. `vfs/queue-set-expiry` can accelerate or delay queued uploads but cannot stop an upload already started; it is an advanced recovery tool, not pause or rollback ([queue-set-expiry](https://rclone.org/rc/#vfsqueue-set-expiry-set-the-expiry-time-for-an-item-queued-for-upload)).

### Safe and force unmount

There is no documented rclone RC operation to atomically reject new opens while draining old ones, enumerate every open file, flush and remotely verify all writes, then unmount. Therefore safe unmount is a Host protocol:

1. Persist `SafeUnmountRequested`; mark the UI `Draining` and reject new application commands. The mounted drive may still accept opens because no supported RC quiesce gate exists.
2. Poll `vfs/queue` and `vfs/stats`. Require stable zero queued, zero uploading, zero errored, no out-of-space, and no process/RC error for a configured quiet interval.
3. If any relevant field/endpoint is missing, return `CannotProveClean`; offer Return or Continue Waiting. Never turn `Unknown` into clean.
4. Attempt `mount/unmount mountPoint=<returned exact point>`. rclone documents an error if unmount fails, including busy cases; do not use `unmountall` for a Profile ([mount/unmount](https://rclone.org/rc/#mountunmount-unmount-selected-active-mount)).
5. Confirm absence from `mount/listmounts`/`vfs/list`, Windows namespace reconciliation, and continued daemon health. Only then publish `StoppedClean`.

The normal protocol can provide a conservative **no currently observable pending upload** result, not remote durability or per-file verification. If product policy requires remote verification, retain a Host manifest at file-close/queue observation and run explicit `operations/stat/check` work before declaring verified; files modified only through filesystem activity may still lack a complete authoritative manifest.

Force unmount records consent and a recovery-cache manifest, calls `mount/unmount` once, waits bounded time, then may terminate the owned rclone only through the established supervised-process path. It never deletes cache. Result is `StoppedForcedRecoveryRequired` whenever cleanliness was not proved. Process termination is not a filesystem rollback.

### Sleep, outages, and crashes

The hidden Host window receives `WM_POWERBROADCAST`: `PBT_APMSUSPEND` announces suspension and `PBT_APMRESUMEAUTOMATIC` announces every ordinary resume, although Microsoft notes a Windows 10 sleep-to-hibernate transition may omit that resume event ([WM_POWERBROADCAST](https://learn.microsoft.com/en-us/windows/win32/power/wm-powerbroadcast), [PBT_APMRESUMEAUTOMATIC](https://learn.microsoft.com/en-us/windows/win32/power/pbt-apmresumeautomatic)). Persist intent continuously; messages are hints, not transaction boundaries.

- On suspend, journal the observation but deliberately leave the foreground rclone mount running.
- On resume, retain the same instance/letter and re-run daemon, RC, mount-list, Windows namespace, VFS, cache, and mounted-root probes. Never create a second mount as recovery.
- A Remote probe failure with a live mount becomes `DegradedConnection`; keep the drive while VFS retries and expose risk. rclone itself warns cloud mounts are not as reliable as local filesystems and cannot retry like copy/sync without local caching ([mount versus copy](https://rclone.org/commands/rclone_mount/#rclone-mount-vs-rclone-synccopy)).
- Unexpected rclone exit becomes `Interrupted`; WinFsp cleanup is expected but must be observed. Unexpected Host exit kills Job-contained rclone by ADR-0007. On restart, any nonterminal Mount requires `NeedsRecovery`, cache quarantine/inspection, and explicit Check and Remount even if auto-mount is enabled.
- If the letter exists but is not attributable to the current rclone/VFS/process record, enter `ForeignOrStaleDrive`. Never delete it automatically. A WinFsp/network-provider conflict or delayed namespace cleanup needs diagnostics and an explicit recovery action.
- If cache metadata/content is corrupt, inconsistent, or unreadable, stop new writes, quarantine by atomic rename only after no live VFS owns it, preserve it, and offer scan/export/reverification. rclone exposes no supported general cache-repair RC; automatic cache clearing is forbidden.

### Editing, deletion, and updates

Display-only Profile edits do not affect the active contract. Any change to Remote/config reference, subpath, mount point/type, letter, fixed/network mode, cache mode/path/capacity, or runtime option creates a new Profile revision staged behind safe unmount. The active instance retains its immutable resolved snapshot; never claim it adopted saved changes.

Deleting a Profile requires a stopped instance and a clean-evidence record. If pending/errored/unknown/recovery cache exists, delete only the Profile metadata while preserving a separately identified recovery record and directory. `vfs/stats`/`queue` are necessary observations, not proof that deletion is safe.

Component downloads and signature verification may run while mounted. Replacement of rclone, WinFsp, Host, cache layout, or an option-contract schema waits for safe unmount of affected Profiles. Re-detect endpoint sets, option schemas, mount types, rclone version, and installed WinFsp architecture/version afterward; remount is an explicit user choice. WinFsp installation/update is outside rclone RC and is the only explicitly elevated installer step.

WinFsp detection is application-owned and must corroborate its 32-bit registry view `HKLM\SOFTWARE\WinFsp` (`InstallDir`/`SxsDir`), expected architecture DLL/driver files, service/driver state, and trusted file/product version. The official WinFsp registry documentation says this key is under `WOW6432Node` on x64/ARM64 and that `InstallDir` and `SxsDir` locate the installation; registry presence alone can be stale and is not compatibility proof ([WinFsp registry settings](https://github.com/winfsp/winfsp/wiki/WinFsp-Registry-Settings)). Installation is downloaded only from an allow-listed official release asset, cryptographically verified by the updater policy, and launched through explicit installer UAC; the Host and UI remain unelevated.

## Adapter contract

The durable public contract is semantic and versioned; rclone field names are an internal resolved layer.

```text
MountAdapterContractV1 {
  contract: "mount-rclone-winfsp-adapter/v1"
  profileId: UUID
  profileRevision: UInt64
  mountInstanceId: UUID
  requested: {
    remoteRef: VaultRecordId
    subpath: CanonicalRemotePath
    presentation: NetworkDrive | FixedDrive | FixedDirectory
    letter: Automatic | Preferred(A..Z)
    volumeName: ValidatedUniqueName
    preset: ReadOnly | Standard | MaximumCompatibility | Custom
    cache: { path, maxBytes?, minFreeBytes?, maxAge?, portable }
    autoMount: bool
  }
  resolved: {
    rcloneVersion, rcloneBinaryDigest, winFspVersion, winFspArchitecture
    mountType, endpointSetDigest, optionSchemaDigest
    fs, mountPointRequest, mountOpt, vfsOpt
  }
  runtime: {
    hostEpoch, rcloneIncarnation, processId, processStartTimeUtc
    returnedMountPoint?, startedAtUtc?, lastObservationUtc?
  }
  evidence: {
    rcListed, vfsListed, windowsNamespace, mountedRootProbe
    queueSnapshot?, statsSnapshot?, cacheRisk
  }
}
```

Rules:

- Secrets never enter this DTO; `remoteRef` resolves only inside the Host.
- `resolved.mountOpt/vfsOpt` are validated against the exact `optionSchemaDigest`; they are diagnostic snapshots, not portable promises across rclone versions.
- A major contract change is required if cleanliness/readiness semantics weaken. Minor additive fields must default to `Unknown`, never false/zero.
- Reject duplicate Profile mount instances. Every mutation carries expected Profile revision, mount-state revision, and idempotency key.
- Persist intent before `mount/mount`, `mount/unmount`, process termination, cache quarantine, update, and Profile/cache deletion.

## State machine

```text
Stopped
  -> Validating
     -> Starting -> Probing -> Ready
     -> StartFailed

Ready
  -> DegradedConnection -> Ready
  -> Suspending -> Reconciling -> Ready | DegradedConnection | NeedsRemount
  -> SafeUnmountRequested -> Draining -> Unmounting -> StoppedClean
                                        -> CannotProveClean
                                        -> UnmountFailed
  -> Interrupted -> NeedsRecovery -> CheckingRecovery
                                  -> StoppedRecoveryPreserved
                                  -> RemountEligible

CannotProveClean | UnmountFailed
  -> Draining
  -> Ready
  -> ForceUnmounting -> StoppedForcedRecoveryRequired

Any active state with unexplained letter/process/cache ownership
  -> ForeignOrStaleDrive | CacheQuarantined
```

`Ready`, `StoppedClean`, and `RemountEligible` require named evidence predicates. They are not aliases for an RC 200 response or a missing process. `NeedsRemount` stops new application-initiated writes but cannot promise that arbitrary Windows processes are technically blocked while a mount remains reachable.

## Truthful diagnostics and fallback

Always show the resolved Remote/subpath, presentation, returned letter/path, volume/share name, rclone/WinFsp versions, selected mount type, uptime, last successful root probe, cache path/preset/configured limits, free space, available stats/queue values, process state, recent redacted errors, and evidence freshness.

Fallback rules:

- Missing `mount/*`: Mount unavailable for that binary; do not fall back to parsing CLI output in the managed lifecycle.
- Missing required mount/VFS option field: preset or feature unavailable; preserve configuration as needing migration.
- Missing `vfs/queue`: pending item details `Unknown`; safe unmount cannot prove clean for write-cached mounts.
- Missing/new `vfs/stats` field: display `Unknown`; no zero default.
- Missing WinFsp or wrong architecture/version: Mount unavailable; offer official verified installer, explicit UAC, re-detection, and explicit retry.
- RC unreachable but process alive: `ControlLost`; do not launch another rclone or infer mount failure.
- Process gone: `Interrupted`, not stopped clean.
- Backend lacks polling/change notification: mount remains attemptable with degraded freshness; expose `vfs/poll-interval` capability and manual `vfs/refresh`, but do not promise immediate external-change visibility ([VFS directory cache](https://rclone.org/commands/rclone_mount/#vfs-directory-cache), [vfs/refresh](https://rclone.org/rc/#vfsrefresh-refresh-the-directory-cache)).

## What cannot be guaranteed

- Cloud availability, POSIX/NTFS equivalence, or local-disk reliability.
- Atomic read-after-write visibility or transactional multi-file writes.
- Complete offline synchronization.
- A complete per-file list of open Windows handles through documented rclone RC.
- A durable per-file dirty timestamp, last upload error, or remote verification record from `vfs/queue` alone.
- A hard VFS cache capacity ceiling or eviction of open files.
- Blocking all new third-party opens during a safe-unmount drain.
- That a successful unmount proves all writes reached and were verified on the Remote.
- Automatic rollback after force unmount, process crash, cache loss, or power loss.
- Immediate disappearance or safe ownership of an unexplained drive letter.
- Cross-elevation drive visibility while preserving the project's unelevated per-user boundary.
- Compatibility of saved raw option fields across rclone versions without rediscovery and validation.
- Precise `max-transfer`/`cutoff-mode` behavior for VFS writeback. rclone's mount documentation gives no mount-specific cutoff contract, so do not expose this as a guaranteed Mount capacity policy without a validation result.

## Acceptance matrix

| Scenario | Required evidence/result |
|---|---|
| Network and fixed mounts, explicit/automatic letters | Correct live fields, unique labels/shares, returned letter captured, Explorer/API presentation verified on Windows 10 and 11. |
| Elevated versus normal clients | Normal Explorer sees unelevated mount; elevated visibility limitation is explained; Host/UI never elevate. Fixed-directory alternative is tested. |
| Preferred letter occupied before or during launch | `DriveLetterConflict`; no silent fallback and no second instance. Race returns one actionable launch failure. |
| Readiness faults after RC success | Missing VFS/Windows/root probe never becomes `Ready`; cleanup is bounded and status is truthful. |
| Read-only preset | Backend writes do not occur across representative Win32 applications; capture how each application reports or fails the write because a useful error is not guaranteed. UI remains explicitly labeled read-only. |
| Standard/full cached writes and outage | Queue/stats/risk transition correctly; cached behavior is described without offline-sync claims. |
| Capacity pressure | Open/uploading/queued data is preserved; over-limit behavior and out-of-space are reported; no direct active-cache deletion. |
| Safe unmount, active write/open file | Clean result only after stable queue/stat predicates and successful namespace removal; unknown/open ambiguity blocks `StoppedClean`. |
| Force unmount | Explicit warning/consent; cache preserved; result always requires recovery unless cleanliness was already proved. |
| Sleep/resume and network loss | Same instance/letter reconciled; no duplicate; degraded/needs-remount states match evidence. |
| Kill rclone/Host at every lifecycle transition | Mount becomes interrupted, descendants contained, cache preserved, no auto-remount, no false success. |
| Stale/foreign letter and WinFsp cleanup delay | Never remove an unattributed mapping; provide diagnostics and explicit recovery. |
| Corrupt cache/metadata or removed cache device | Stop new writes, quarantine/preserve when safe, export/recovery offered, never auto-clear. |
| Minimum and candidate rclone/WinFsp versions | Golden schema fixtures validate endpoint/field/type drift; unsupported fields disable only affected capabilities. |
| Component update with active Mount | Download/verify allowed; replacement blocked until affected Mounts stop cleanly; capabilities redetected before optional remount. |

## Required validation tickets

1. **Windows mount presentation and readiness spike:** exercise network/fixed/directory modes, automatic and conflicted letters, share uniqueness, UAC token visibility, `mount/mount` return timing, namespace probes, and cleanup on Windows 10/11 with minimum/latest rclone and supported WinFsp versions.
2. **VFS clean-drain and force-unmount spike:** record `vfs/queue`, `vfs/stats`, filesystem behavior, remote results, RC unmount errors, process termination, and cache contents under open handles, active/failed uploads, outage, out-of-space, and injected crashes. Determine the strongest defensible `StoppedClean` predicate.
3. **Suspend/crash/stale-namespace recovery spike:** inject sleep/hibernate, Host/rclone termination, removable-cache loss, WinFsp/network-provider conflict, and delayed/stale drive mappings; verify no duplicate mount or automatic cache deletion.
4. **Mount option compatibility fixtures:** capture `rc/list`, `mount/types`, `options/info blocks=mount,vfs`, `vfs/stats`, and `vfs/queue` for the minimum and candidate component matrix, then lock typed adapters and unknown-field behavior with golden tests.
5. **VFS cutoff and recovery-options spike:** verify whether transfer cutoff settings apply safely to writeback, and test recovery of crash-left pending files under identical versus changed cache/mount options. Until proven, keep cutoff out of preset guarantees and require the original resolved option snapshot for recovery.

## Decision risks

- The documented VFS telemetry is insufficient to prove per-file remote durability. Product language must stay at “no currently observable pending uploads” unless an application-owned verification manifest is added.
- rclone RC unmount does not expose a force flag or a quiesce primitive. Force is process supervision plus preserved recovery state, not a stronger rclone guarantee.
- Drive visibility and namespace cleanup vary by user token, mapping type, WinFsp version, and other network providers. A real Windows matrix is required before setting the supported component floor.
- Cache option schemas and RC response fields can evolve. Runtime discovery reduces breakage but cannot replace minimum/latest compatibility fixtures.
- Directory mounting avoids the documented elevation split for letters, but does not guarantee every administrator-only application's security or filesystem assumptions.
