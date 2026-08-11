# Transfer safety semantics mapped to rclone

Research date: 2026-08-11  
Contract version: `transfer-rclone-adapter/v1`  
Scope: mapping `docs/product/transfer-task-semantics.md` to the bundled rclone RC API and identifying the safety guarantees that remain owned by the Background Host.

## Decision

Treat rclone as a capability-discovered execution engine, not as the application's safety authority. The Background Host owns the immutable task/preview contract, conflict and confirmation policy, orchestration phases, safety-copy retention, path compatibility checks, durable per-path journal, cancellation barrier, and business terminal result. It invokes a verified, exact rclone binary through authenticated loopback RC calls and records that binary's `core/version` and `executeId`.

Use `sync/copy`, `sync/sync`, and selected `operations/*` calls, always asynchronously where available, with a unique `_group`, a complete per-call `_config`, and a complete per-call `_filter`. Discover option JSON names and types from that exact binary's `options/info`/`options/get` rather than freezing Go field names in the UI. `_config` and `_filter` inherit unspecified daemon globals, so the adapter must explicitly set every safety-relevant value and validate the active values with `options/local` in compatibility tests ([RC per-call configuration and filters](https://rclone.org/rc/#setting-config-flags-with-_config), [RC option discovery](https://rclone.org/rc/#option-blocks)).

Do **not** implement the accepted Move semantics with one `sync/move` call. `sync/move` owns its source-deletion timing, whereas the product requires application-selected verification before each source deletion. Execute Copy phase, verification phase, then application-controlled `operations/deletefile` (and optional empty-directory cleanup) from the durable verified manifest. This also prevents skipped/conflicted sources from being removed. The official `--ignore-existing` documentation says skipped Move files remain, but the stronger product guarantee still requires explicit orchestration rather than depending on a compound command ([rclone options: `--ignore-existing`](https://rclone.org/docs/#ignore-existing)).

## Capability classes

| Class | Meaning |
|---|---|
| Direct | A documented RC endpoint/option gives the required operational behavior, subject to documented backend limits. |
| Composed | The Host combines documented rclone operations and verifies their outputs; no single call gives the product guarantee. |
| Application | rclone exposes useful data or primitives, but the policy, durable evidence, or safety invariant belongs entirely to the Host. |
| Unsupported | No documented, portable rclone contract is sufficient; the UI must block, downgrade explicitly, or offer a warned alternative. |

## Semantic mapping

### Operations and conflicts

| Product requirement | Mapping | Class and limitations |
|---|---|---|
| Copy adds/updates and never deletes target-only or source content | `sync/copy`, `_async:true`, unique `_group`; no `DeleteExcluded` | Direct. `copy` does not make the destination identical and does not remove target-only objects ([rclone copy](https://rclone.org/commands/rclone_copy/), [RC `sync/copy`](https://rclone.org/rc/#synccopy-copy-a-directory-from-source-remote-to-destination-remote)). |
| Preserve a newer target by default | Preflight manifest plus `_config.Update:true` for execution | Composed. `--update` skips a destination whose modtime is newer; its exact behavior depends on usable modtimes, modify window, size and (in some cases) checksum. The Host must classify and persist the conflict itself; rclone has no structured `conflict` result ([`--update`](https://rclone.org/docs/#update)). |
| Source newer replaces through safety copy | `sync/copy` with generated `BackupDir`, timestamped `Suffix`, and `SuffixKeepExtension`; only after preview manifest says replace | Composed. `--backup-dir` handles objects rclone decides to overwrite, but it does not prove the product's conflict classification. |
| Equal timestamp but verified content differs | Preview/check with checksum when a common reliable hash exists, or downloaded comparison; then selected replacement | Composed. Default equality may treat equal modtime+size as equal without checking checksum; `_config.CheckSum:true` changes equality to size+checksum but falls back when hashes are unavailable. Host must force and record the chosen verification profile ([rclone sync copy options](https://rclone.org/commands/rclone_sync/#copy-options)). |
| Mirror is one-way, source authoritative | `sync/sync`, default `DeleteAfter`, `IgnoreErrors:false`, with safety-copy options | Direct for one-way convergence; composed for preview/confirmation and durable evidence. Official sync modifies destination only and warns that it deletes target-only data ([rclone sync](https://rclone.org/commands/rclone_sync/)). |
| Source Always Wins | Copy/sync without `Update`, `IgnoreExisting`, or `Immutable`; force comparison using the selected equality settings, using `IgnoreTimes:true` only when unconditional retransmission is explicitly intended | Composed. “Always wins” is an application policy, not one rclone switch; `--ignore-times` retransfers even identical objects. |
| Skip Existing | `_config.IgnoreExisting:true` | Direct for skipping. The Host derives conflict records; it must not use compound Move for the accepted Move invariant ([`--ignore-existing`](https://rclone.org/docs/#ignore-existing)). |
| Stop on Conflict / continue safe files | Host first builds a conflict manifest; block execution for stop policy or pass a safe `FilesFromRaw` subset for continue policy | Application. rclone has no generic stop-on-newer-conflict switch or typed conflict stream. |
| Case collisions and duplicate provider objects | `operations/fsinfo.Features.CaseInsensitive` and `DuplicateFiles`, plus complete source/target listings; optional `_config.FixCase` only for an explicitly chosen mapping | Application. `fsinfo` reports characteristics, not the collision set. Sync states duplicate objects are not handled; never silently select one ([RC `operations/fsinfo`](https://rclone.org/rc/#operationsfsinfo-return-information-about-the-remote), [rclone sync](https://rclone.org/commands/rclone_sync/)). |

### Preview, filters, names, and overlap

The authoritative preview is a Host-created artifact, not a dry-run log. Its key is:

`SHA-256(canonicalTaskConfig || sourceRoot || destinationRoot || rcloneVersion || capabilitySnapshot || filterIR || safetyPolicy)`.

Run the same endpoint and exact options intended for execution with `_config.DryRun:true`, `_async:true`, a preview-only `_group`, and logger-report options supported by the live schema. The sync/copy logger flags (`Combined`, `Differ`, `MissingOnDst`, `MissingOnSrc`, `Match`, `Error`, and `DestAfter`) describe predicted paths. Their official limitations are material: paths are logged during execution, so they predict what **should** happen rather than prove what did; `--no-traverse` makes destination-only results incomplete; logger output is unsupported/incomplete with hard max-duration, compare/copy-dest, whole-directory server-side moves, high-level retries, and unusual error paths. Use `Retries:1` in preview to avoid duplicate records ([sync logger flags and limitations](https://rclone.org/commands/rclone_sync/#logger-flags)).

RC `operations/check` is a useful structured comparison endpoint: it returns `success`, `hashType`, and arrays for combined/missing/match/differ/error, and supports `download` and `oneWay`. It is not a transfer plan and cannot distinguish overwrite from conflict policy or filter-caused deletion by itself ([RC `operations/check`](https://rclone.org/rc/#operationscheck-check-the-source-and-destination-are-the-same)). Therefore the Host normalizes dry-run reports, listings, and checks into typed `PreviewItem`s and rejects a preview if a logger limitation is active and the required count cannot be derived independently.

Pass ordered raw/visual rules in `_filter` (`FilterRule`/live-discovered equivalent) and age/size rules as typed filter options. Rclone processes combined filter rules in order, first match wins; mixing include/exclude/filter families can produce different ordering, and `files-from` overrides other filters. The visual compiler must emit one ordered filter-rule family and round-trip it before allowing raw-to-visual conversion ([rclone filtering](https://rclone.org/filtering/), [RC `_filter`](https://rclone.org/rc/#setting-filter-flags-with-_filter)).

For “Included/Excluded and responsible rule”, evaluate the same filter intermediate representation in the Host and validate it against an rclone listing (`operations/list` or `lsf`) using the same `_filter`. `--dump filters -vv` is human-oriented diagnostic output, not a supported structured RC protocol, so it must not be parsed as the production rule-trace API. `DeleteExcluded` is a separate high-risk boolean and its destination deletions must be marked `filterDelete` in the Host preview.

Rclone explicitly rejects overlapping sync remotes, with a documented exception when an inner destination is excluded. This runtime error is not sufficient: before preview the Host canonicalizes local paths, remote/config identities and roots, detects ancestor/equality relations, detects backup-dir overlap, and serializes overlapping write targets. Unknown identity is `unsafe-unknown` and requires the advanced override ([rclone sync overlap rule](https://rclone.org/commands/rclone_sync/), [`--backup-dir` overlap constraint](https://rclone.org/docs/#backup-dir-dir)).

Invalid Windows names, target encoding transformations, normalization and case-only collisions require Host preflight over full path components and backend-specific encoding knowledge. `fsinfo` supplies `CaseInsensitive`, `Precision`, hashes and feature flags, but not a general “is this name representable without collision?” API. Unknown wrappers/providers must be blocked from destructive execution until a mapping is selected or validated by a non-mutating probe.

### Safety copies, deletion, and retention

| Requirement | Mapping | Class and limitations |
|---|---|---|
| Backup before target overwrite/delete | `_config.BackupDir`, `Suffix`, `SuffixKeepExtension` on copy/sync | Direct only if the backup path is on the same destination remote and the remote supports server-side move or copy. Existing same backup path may be overwritten, so generate a run-unique directory/suffix ([`--backup-dir`](https://rclone.org/docs/#backup-dir-dir), [`--suffix`](https://rclone.org/docs/#suffix-suffix)). |
| Reliable move unavailable alternatives | Inspect destination `fsinfo.Features.Move`/`Copy`; Cancel, or Host `operations/copyfile` to safety + verify + `operations/deletefile`, or warned direct delete | Composed. High-level rclone may fall back from server-side operations, but `--backup-dir` itself requires server-side move or copy. Feature flags signal optimized primitives, not an atomicity guarantee. |
| Seven-day retention and cleanup status | Host stores each safety generation and expiry; after expiry list and delete only that generation through filtered `operations/delete`/individual delete, journaling each outcome | Application. `--backup-dir` creates backups but has no retention scheduler or cleanup receipt. Never call `purge` for filtered retention because purge does not obey filters ([rclone filtering](https://rclone.org/filtering/)). |
| Mirror deletes after successful transfers | sync default `_config.DeleteMode:"after"` (live enum/name from `options/info`), `IgnoreErrors:false` | Direct. Official `--delete-after` collects deletes and starts them only after the copy pass succeeds; prior I/O errors suppress deletion ([delete timing](https://rclone.org/docs/#delete-before-during-after)). |
| No target deletion after source/transfer/verification error | `DeleteAfter`, `IgnoreErrors:false`, followed by separate High-Assurance check before any additional application-owned delete | Composed. Built-in delete-after knows rclone copy-pass errors, not a later Host verification failure. If post-transfer verification must gate Mirror deletions, use a two-phase Host plan rather than one `sync/sync`: copy/update, verify, then execute the previewed target-only deletion manifest with revalidation. |
| Early delete / Ignore Errors | `DeleteMode=before|during`, `IgnoreErrors:true` only in advanced request | Direct but high risk; Host mandates re-preview and confirmation. |
| Filtered files untouched / delete-excluded separate | Default `DeleteExcluded:false`; set true only for accepted high-risk plan | Direct operational behavior; Host separately counts and confirms filter-caused deletions ([`--delete-excluded`](https://rclone.org/filtering/#delete-excluded-delete-files-on-dest-excluded-from-sync)). |

### Verification and Move

At capability capture, call `operations/fsinfo` for both configured remote stacks and intersect their returned `Hashes`. Do not hard-code provider hash support. The Host chooses a deterministic preference order based on cryptographic strength and backend reliability and stores the chosen hash and rationale. Rclone's ordinary copy verifies transferred checksums when available unless `IgnoreChecksum` is enabled; this is transfer-integrity checking, not a durable, complete post-transfer audit ([`--ignore-checksum`](https://rclone.org/docs/#ignore-checksum), [RC `fsinfo` hashes](https://rclone.org/rc/#operationsfsinfo-return-information-about-the-remote)).

Profiles:

- `Basic`: rclone normal equality/transfer checks using size+modtime and checksum where available. Label explicitly; backend timestamp precision comes from `fsinfo.Precision`.
- `CommonHash`: execute with `_config.CheckSum:true`, then `operations/check(oneWay:true, download:false)` and require its structured `success`, empty `differ`/`missingOnDst`/`error`, and expected `hashType`.
- `HighAssurance`: `operations/check(oneWay:true, download:true)` over the executed manifest (using `_filter.FilesFromRaw` or isolated roots), which downloads both sides and hashes content. This is expensive and its capability/cost must be previewed. Crypt requires its dedicated `cryptcheck` semantics if checking encrypted underlying data; otherwise compare through the crypt remote.

`operations/check` returns a structured combined report and selected `hashType`; use this instead of parsing command logs ([RC `operations/check`](https://rclone.org/rc/#operationscheck-check-the-source-and-destination-are-the-same), [rclone check](https://rclone.org/commands/rclone_check/)). If no common reliable hash exists, fall back to Basic or explicit downloaded High Assurance; never claim hash verification.

Accepted Move state machine:

1. Preview Copy semantics and conflicts; freeze the selected path manifest.
2. Run `sync/copy` for eligible paths with safety-copy options.
3. Run the selected one-way verification over eligible paths.
4. For each verified path, re-stat source identity (size/modtime and hash when selected), then call `operations/deletefile`; never delete if source changed since preview/copy.
5. Optionally remove now-empty source directories with narrowly scoped operations.

This is slower than `sync/move`, but it is the only mapping that makes “verified before source delete” and “preserve failed, skipped, conflicted, filtered and cancelled sources” an application-verifiable invariant.

### Retries, cancellation, limits, and partial results

Rclone distinguishes low-level retries (typically one HTTP request) from high-level retries of the whole operation. `_config.Retries:3` gives three whole-operation attempts; `_config.LowLevelRetries` controls provider request retries, and `RetriesSleep` is a fixed interval, not exponential backoff ([retry options](https://rclone.org/docs/#retries-int), [low-level retries](https://rclone.org/docs/#low-level-retries-number)). Consequently the product's classified transient-only exponential schedule is Application-owned: invoke each rclone execution with `Retries:1`, classify the structured RC/job error plus backend/error diagnostics into transient/permanent/unknown, and schedule up to three Host rounds with exponential delay. Do not claim rclone provides the classification. Preserve low-level backend retries unless compatibility testing proves otherwise.

Cancellation is `job/stop(jobid)` (or scoped `job/stopgroup`) and is cooperative. The Host enters `Cancelling`, closes its own deletion admission barrier immediately, calls stop once, and polls `job/status` until `finished`; already completed changes are not rolled back ([RC jobs and `job/stop`](https://rclone.org/rc/#jobstop-stop-the-running-job)). A compound `sync/sync` may already have admitted deletion work, so strict “prevent not-yet-started deletion” after cancellation requires the two-phase application deletion plan. No documented generic pause contract exists.

Limits map as follows:

- Per-task bandwidth: per-call `_config.BwLimit` only after live schema validation. Global daemon bandwidth and scheduled windows: `core/bwlimit`, which accepts only one current rate, applies across transfer and Mount traffic owned by that daemon. The Host computes the strictest global/window rate before setting it; if a per-call limiter cannot be validated, serialize or use the stricter global value ([RC `core/bwlimit`](https://rclone.org/rc/#corebwlimit-set-the-bandwidth-limit)).
- Size/duration: `_config.MaxTransfer`, `MaxDuration`, `CutoffMode`. `SOFT` stops starting new transfers after the limit and therefore matches “finish current file then stop” most closely; `HARD` stops immediately; `CAUTIOUS` tries not to exceed max-transfer and does not apply to max-duration. Limits produce distinct rclone errors/CLI codes, but the Host derives `StoppedByLimit` from the configured limit plus terminal error, not text alone ([max limits and cutoff modes](https://rclone.org/docs/#max-duration-duration)).
- A scheduled bandwidth timetable is configured at daemon/global level; per-task schedule combination and “strictest wins” are Host policy.

For progress, poll `core/stats(group)` and drain `core/transferred(group)` frequently. The latter retains only the last 100 completed entries and is neither a complete manifest nor durable history. `job/status` normally expires soon after completion. Persist each observation into the lifecycle journal, but use execution/verification manifests as the source of truth for completed/skipped/conflicted/failed paths ([RC stats and completed transfers](https://rclone.org/rc/#coretransferred-returns-stats-about-completed-transfers), [RC async job expiry](https://rclone.org/rc/#running-asynchronous-jobs-with-_async--true)). Re-running safely means create a new preview and let rclone skip objects satisfying the same frozen equality policy; never promise byte-level resume.

## Versioned strong adapter contract

The C# implementation may use different names, but its serialized Host↔adapter boundary must preserve these semantics:

```text
TransferRcloneRequestV1 {
  contract: "transfer-rclone-adapter/v1"
  runId, taskId, taskConfigVersion, acceptedPreviewId
  rclone: { expectedVersion, expectedExecuteId, endpointSetHash, optionSchemaHash }
  operation: Copy | MirrorCopyPhase | MirrorDeletePhase | MoveCopyPhase | MoveDeletePhase | Verify
  source: CanonicalEndpoint; destination: CanonicalEndpoint
  selectedPaths: FrozenManifestRef
  conflictPolicy: PreserveNewer | SourceWins | SkipExisting | StopOnConflict
  verification: Basic | CommonHash(hash) | HighAssuranceDownload
  safetyCopy: Disabled | Required { remote, directory, uniqueSuffix, expiresAt }
  deletion: None | AfterVerifiedManifest { manifest, ignoreErrors: false }
  filters: OrderedFilterIrV1
  limits: { bandwidth?, maxTransfer?, maxDuration?, cutoff: Soft | Hard | Cautious }
  retryAttempt: { hostRound: 1..3, rcloneHighLevelRetries: 1 }
  options: CompleteDiscoveredConfigMap
}

TransferRcloneEventV1 =
  JobAccepted(jobId, executeId, group) |
  Progress(statsSnapshot) |
  PathObserved(path, purpose, bytes, error?) |
  CancellationAcknowledged |
  JobTerminal(success, structuredError?, output?)

TransferBusinessResultV1 {
  terminal: Succeeded | CompletedWithConflicts | Failed |
            CancelledWithPartialResults | InterruptedBySystemOrCrash | NotExecuted
  reason: Completed | ConflictsPreserved | VerificationFailed | LimitReached |
          Cancelled | HostLost | RcloneLost | CapabilityChanged | PolicyBlocked | Error
  manifests: { completed, skipped, conflicted, failed, deleted, possiblyAffected }
  verificationEvidence[]; safetyCopyGenerations[]; diagnosticsRef
}
```

The adapter rejects a request when the accepted preview hash, rclone version, `executeId`, endpoint/schema hashes, canonical endpoints, or safety-relevant options differ. Unknown RC JSON response fields are retained/ignored safely; missing required fields or endpoints are a capability change, not silently defaulted behavior.

## Acceptance matrix

Test every supported bundled update candidate at the minimum supported version and latest candidate, on local↔local plus representative hash-rich, no-hash, case-insensitive, wrapper/crypt, server-side-move, and copy-delete-fallback remotes.

| Scenario | Required assertion |
|---|---|
| Copy with target-only file | Target-only file remains; report has no delete. |
| Target newer | File unchanged, durable conflict item, terminal `CompletedWithConflicts`. |
| Source newer/equal-time-different | Existing target appears in unique safety generation before replacement; verification evidence recorded. |
| Mirror dry-run | Typed copy/replace/delete/filter-delete counts equal independent before/after listings; no mutation. |
| Logger limitation active | Preview is blocked or independently derived; never accepted from incomplete logger output. |
| Raw ordered filters | Host decisions equal rclone listing for an adversarial first-match suite; raw→visual only on lossless round trip. |
| `delete-excluded` | Filter-caused deletes are separately listed and require new preview/confirmation. |
| Copy/read/verify error | No Mirror deletion phase starts; no Move source is deleted. |
| Move cancellation between phases | Verified-and-deleted paths reported; all other sources remain; terminal partial. |
| Cancellation during rclone call | Host deletion barrier closes before `job/stop`; terminal waits for `job/status.finished`. |
| No common hash | UI says Basic, or High Assurance downloads; never labels hash-verified. |
| Common hash changes after config edit | Capability snapshot invalidates preview. |
| Safety backend lacks Move but has Copy | Copy-to-safety, verify, delete succeeds; interruption at every boundary is recoverable from journal. |
| Safety backend lacks Move/Copy | Cancel is default; direct delete requires elevated warning; no fake safety-copy claim. |
| Seven-day cleanup | Only expired, app-owned generations are deleted; failures remain visible/retryable. |
| Overlap/case/encoding collision | Preview blocks before any mutation, including wrapper aliases resolving to the same root. |
| Three transient rounds | Host delays exponentially; rclone high-level retries stay 1; permanent errors get no blind retry. |
| Soft/hard cutoff | Soft finishes active file and starts none; hard may leave current file for retransmission; both yield `LimitReached`. |
| More than 100 files | Durable result manifest remains complete despite `core/transferred` cap. |
| rclone/Host crash | No success inferred; journal yields `InterruptedBySystemOrCrash` and possibly-affected manifest. |

## Requirements owned by the Background Host

The following are not direct rclone guarantees and must remain explicit Host responsibilities:

1. Canonical task versioning, preview expiry/binding, schedule eligibility, dangerous confirmation and typed target-name confirmation.
2. Complete preview normalization and counts, including reconciliation around logger limitations.
3. Conflict classification, Stop on Conflict, continue-safe subset, and `Completed with Conflicts`.
4. Invalid-name/encoding/case collision detection, remote alias/path overlap, and concurrency serialization.
5. Run-unique safety-copy naming, capacity warning, seven-day retention and cleanup journal.
6. Copy-to-safety-then-delete fallback and all crash boundaries around it.
7. High-Assurance verification selection/evidence and the Copy→Verify→Delete Move state machine.
8. Mirror's strict post-verification deletion gate (requires two phases, not a single sync).
9. Transient/permanent error taxonomy and three-round exponential retry schedule.
10. Cancellation admission barrier, partial-result manifests, system/crash reconciliation, and no-roll-back language.
11. Global/window/task limit composition and business result `Stopped by Limit`.
12. Durable history and mutually exclusive business terminal result; rclone success or exit code alone is insufficient.

## Known residual risks and required validation

- Option JSON keys/enums can evolve. The concrete mapper must be generated/validated from `options/info` for each bundled version and tested through `options/local`.
- RC dry-run logger destinations and whether every logger flag is usable without process-global file output need an executable spike; `operations/check` is structured, but it is not a full transfer planner.
- Backend `Move`/`Copy` features do not establish atomicity, consistency delay, or quota behavior. Safety fallback needs fault-injection testing per supported backend class.
- A destination may change after preview. Execution must re-stat destructive paths and reject stale items; no rclone option makes a prior preview transactional.
- `job/stop` is cooperative and has no documented “no more file/deletion admitted” acknowledgement. The Host can guarantee this only for its own phase boundaries.
- Normal rclone equality is optimized for synchronization, not forensic assurance. High Assurance necessarily incurs full reads and may be impractical for large tasks; expose cost before confirmation.

## Primary sources

- [rclone Remote Control API](https://rclone.org/rc/)
- [rclone global options](https://rclone.org/docs/)
- [rclone sync command and logger flags](https://rclone.org/commands/rclone_sync/)
- [rclone copy command](https://rclone.org/commands/rclone_copy/)
- [rclone move command](https://rclone.org/commands/rclone_move/)
- [rclone check command](https://rclone.org/commands/rclone_check/)
- [rclone filtering semantics](https://rclone.org/filtering/)
- [official rclone source and generated documentation](https://github.com/rclone/rclone)

