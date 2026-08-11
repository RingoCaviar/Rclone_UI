# Application module boundaries

Status: implementation baseline  
Date: 2026-08-11

## Design rule

Organize the code around deep modules that own complete user-visible behavior. A caller supplies semantic intent and receives semantic state/results; it never assembles rclone options, SQL statements, Windows handles, cryptographic envelopes, recovery phases, or updater file operations.

The Background Host is the only process allowed to mutate application state or own rclone. The Desktop renders snapshots/events and submits commands through the Host Protocol. The Updater performs only a previously committed authenticated update plan.

## Deployable modules

| Module | Interface | Owns | Must not own |
|---|---|---|---|
| `RcloneUI.Desktop` | User interaction over `HostClient` | Avalonia shell, navigation, forms, localization, accessibility, tray presentation | Vault/rclone/Win32 state, business lifecycle decisions |
| `RcloneUI.Host` | `host-ipc/v1` commands, snapshots, events | Composition root, sole state authority, scheduling, workflow admission, lifecycle reconciliation | Visual controls or updater file replacement |
| `RcloneUI.Updater` | One authenticated `UpdatePlan` and one-time handoff | Verified version staging, paired pointer transaction, health timeout and rollback | Vault business data, rclone operations, UI state |
| `RcloneUI.Contracts` | Data-only versioned protocol and persisted-contract types | DTOs, stable enums/units, frame schemas, compatibility fixtures | Networking, storage, process control, UI models |

`RcloneUI.Contracts` is deliberately small. It does not become a shared utility project or expose implementation abstractions.

## Host-owned deep modules

### Data Root Session

Interface:

```text
Open(request) -> DataRootOpenResult
Execute(command, expectedRevision) -> DataRootResult
Observe() -> DataRootSnapshot
Close(reason) -> CloseResult
```

Owns canonical volume/directory identity, exclusive writer lease, Data Root availability, Vault unlock/lock, encrypted generations, snapshots, migrations, settings, durable lifecycle journal, and recovery admission. It is the only module that opens the Vault or writer lock.

Errors are semantic: `Unavailable`, `ReadOnly`, `AlreadyOwned`, `NeedsRecovery`, `AuthenticationFailed`, `UnsupportedFormat`, `IntegrityFailed`, `ResourceLimitExceeded`.

Internal seams exist for filesystem/volume identity, SQLite, cryptography, DPAPI, and clock/randomness because production and deterministic fault adapters are required. They are not exposed to Host callers.

### Work Coordinator

Interface:

```text
Submit(WorkIntent, expectedStateRevision, idempotencyKey) -> AdmissionResult
Cancel(WorkRunId, idempotencyKey) -> CancellationResult
Observe() -> WorkSnapshot
Reconcile(LifecycleObservation) -> ReconciliationResult
```

Owns the queue, concurrency, schedules, locked-session policy, retry admission, lifecycle terminal results, notification eligibility, and coordination between Transfer and Mount work. It persists through Data Root Session and never calls SQLite directly.

### Remote Catalog

Interface:

```text
DescribeProviders() -> ProviderCatalog
AdvanceSetup(RemoteSetupStep) -> SetupOutcome
Test(RemoteId) -> RemoteHealth
Browse(RemoteLocation, PageRequest) -> BrowsePage
Change(RemoteChange, expectedRevision) -> RemoteResult
```

Owns provider wizard progression, runtime schema interpretation, Remote identity, repair/edit/import conflict policy, dependency checks, and redacted errors. It hides rclone configuration state machines and Vault record representation.

### Transfer Orchestrator

Interface:

```text
Preview(TransferTaskRevision) -> PreviewOutcome
Execute(AcceptedPreviewId) -> TransferRunAccepted
Cancel(TransferRunId) -> CancellationResult
Observe(TransferRunId) -> TransferRunSnapshot
```

Owns preview binding, conflicts, safety generations, Copy/Verify/Delete phases, capability fallbacks, retry classification, cancellation barrier, limits, durable manifests, and one business terminal result. It delegates typed execution primitives to Rclone Runtime; callers never select RC endpoints or `_config` keys.

### Mount Manager

Interface:

```text
Validate(MountProfileRevision) -> MountValidation
Start(MountProfileRevision) -> MountStartResult
Stop(MountId, StopMode) -> MountStopResult
Recover(MountProfileId, RecoveryChoice) -> MountRecoveryResult
Observe() -> MountSnapshot
```

Owns Mount identity, readiness evidence, drive-letter policy, presets, VFS risk, conservative drain, force-unmount consent, resume/crash reconciliation, and recovery caches. It delegates mount primitives to Rclone Runtime and Windows Mount adapters.

### Rclone Runtime

Interface:

```text
Start(VerifiedRclone) -> RuntimeCapabilities
Execute(RcloneOperationContract) -> OperationHandle
Observe(OperationHandle) -> OperationObservation
Stop(OperationHandle) -> StopAcknowledgement
Shutdown() -> RuntimeStopResult
```

Owns verified process launch, Job containment, authenticated loopback RC, live endpoint/option discovery, typed adapter contracts, stats normalization, and process diagnostics. It is an execution module, never the owner of Transfer or Mount safety policy.

Production uses the bundled rclone adapter; tests use a scripted in-memory adapter. This is a real seam because both implementations exercise the same orchestrator interfaces.

### Managed Update Coordinator

Interface:

```text
Check(UpdateScope) -> UpdateAvailability
Stage(UpdateSelection) -> StagedUpdate
Prepare(StagedUpdateId) -> UpdatePlan
Commit(UpdatePlanId) -> HandoffResult
Recover() -> UpdateRecoveryResult
```

Owns trusted sources, artifact verification, component compatibility, active-work gates, paired application/Vault update plan, one-time handoff, health evidence, rollback, and retention. The external Updater is a narrow adapter executing the committed plan.

### Diagnostic Exporter

Interface:

```text
Preview(DiagnosticSelection) -> DiagnosticPreview
Export(AcceptedDiagnosticPreview) -> DiagnosticExportResult
```

Owns redaction, path masking, default versus extended Vault metadata, bounded collection, and manifesting. Other modules return typed diagnostic facts; they do not write support archives.

## External seams and adapters

| Seam | Production adapter | Test adapter | Visibility |
|---|---|---|---|
| Host Protocol | authenticated named pipe | in-memory framed duplex | Desktop/Host external seam |
| rclone execution | verified `rclone rcd` RC | scripted runtime | internal to Host |
| Vault persistence | SQLite + record AEAD + libargon2 | fault-injected scratch store | internal to Data Root Session |
| Windows process containment | Win32 process/Job adapter | deterministic process model | internal to Rclone Runtime |
| Windows Mount presentation | WinFsp/drive/session adapter | scripted namespace/VFS evidence | internal to Mount Manager |
| update artifact source | GitHub Releases/official component source | static signed fixture source | internal to Update Coordinator |
| time/random/filesystem | Windows/.NET implementations | deterministic/fault adapters | internal to owning module |

Do not create generic `IFileSystem`, `IPlatformService`, repository-per-table, command-per-RC-endpoint, or wrapper-per-Win32-call interfaces at the external module surface. Those are shallow and spread ordering rules across callers.

## Dependency direction

```text
Desktop -> Contracts <- Host <- Updater handoff adapter
                        |
                        +-> Data Root Session
                        +-> Work Coordinator
                            +-> Remote Catalog
                            +-> Transfer Orchestrator -> Rclone Runtime
                            +-> Mount Manager --------> Rclone Runtime
                        +-> Managed Update Coordinator
                        +-> Diagnostic Exporter
```

Domain policy modules may depend on immutable domain values and their own internal ports. They never depend on Avalonia, SQLite, rclone JSON models, WinFsp types, registry APIs, or updater filesystem DTOs.

## State ownership

| State | Sole owner |
|---|---|
| Data Root identity, Vault generations, journal | Data Root Session |
| Queue, schedules, Work Run admission | Work Coordinator |
| Remote definitions and setup state | Remote Catalog through Data Root Session |
| Accepted Preview, Transfer Run manifests, Safety Generation | Transfer Orchestrator through Data Root Session |
| Mount Profile runtime snapshot, Mount, Recovery Cache risk | Mount Manager through Data Root Session |
| rclone process, RC credentials, Job handle, live capabilities | Rclone Runtime |
| staged update and paired handoff transaction | Managed Update Coordinator |
| visible page/view state | Desktop |

## Test surface

Tests call the same external interface as production callers:

- Data Root Session fault corpus proves old-valid-or-new-valid and sole writer.
- Work Coordinator scenario tests prove queue, schedule, lock, shutdown, and terminal-state semantics.
- Remote Catalog provider fixtures prove setup/repair without exposing rclone questions to the UI.
- Transfer Orchestrator scripted runtime scenarios prove preview binding and no unsafe deletion.
- Mount Manager scripted namespace/VFS scenarios prove readiness, drain, and recovery language.
- Rclone Runtime compatibility fixtures prove exact binary/endpoint/option mapping and containment.
- Host Protocol golden/fuzz tests prove cross-process compatibility.
- Update Coordinator crash matrix proves paired recovery.

Implementation details may be tested internally where needed, but callers and acceptance tests do not bypass these interfaces.

## Implementation order

1. Solution skeleton, build/test conventions, and Contracts primitives.
2. Data Root Session with identity/lease, journal, Vault generations, and recovery.
3. Host process shell plus authenticated protocol and state snapshots.
4. Rclone Runtime and managed-component discovery.
5. Remote Catalog.
6. Work Coordinator and Transfer Orchestrator.
7. Mount Manager.
8. Managed Update Coordinator and external Updater.
9. Desktop shell and vertical user journeys, integrated incrementally from steps 3–8.
10. Diagnostics, accessibility/localization acceptance, packaging, CI, and release hardening.
