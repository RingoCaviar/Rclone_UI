# Background Host IPC and process-supervision research

Status: recommendation for Issue #13  
Research date: 2026-08-11  
Scope: Windows 10/11 x64, one logged-in user session, one writable Background Host per Data Root

## Recommendation

Use a **local-only duplex Windows named pipe** owned by the Background Host. Protect it with an explicit DACL for the current logon SID (plus `SYSTEM` only if diagnostics require it), deny network-origin access, and authenticate each connection with a challenge/response bound to a random Host-incarnation secret. Use a byte stream with a small, versioned, length-prefixed UTF-8 JSON envelope. Do not host HTTP, gRPC, or rclone RC on this control endpoint.

Make the Background Host the sole mutable-state and lifecycle authority. The Portable App should contain four executables/modules:

- `RcloneUI.exe`: Avalonia UI and IPC client; never owns rclone.
- `RcloneUI.Host.exe`: tray, scheduler, Vault writer, IPC server, lifecycle reconciler, and process supervisor.
- `RcloneUI.Updater.exe`: minimal verified update coordinator, launched only after an authenticated update plan is committed.
- `rclone.exe`: managed component, started only by the Host and assigned to a Host-owned Job Object before it can do useful work.

Share a contract assembly containing data-only DTOs, envelope parsing, protocol constants, error codes, and compatibility tests. Keep UI models, Vault persistence, rclone command construction, Win32 process handles, and update implementation out of that assembly.

This implements [ADR-0003](../adr/0003-separate-the-background-host-from-the-ui.md) and preserves the single-writer rules in [ADR-0002](../adr/0002-use-an-explicit-portable-data-root-and-encrypted-vault.md) and [ADR-0006](../adr/0006-use-record-encryption-and-copy-on-write-vault-generations.md).

## Why named pipes

| Candidate | Fit | Decision |
|---|---|---|
| Windows named pipe | Native ACL and logon-session boundary, client impersonation/identity inspection, duplex, no TCP port, first-class `System.IO.Pipes` API. Windows supports multiple instances and local-only use. | **Choose.** |
| Loopback HTTP/HTTP/2/gRPC | Familiar schemas and tooling, but opens a TCP listener, adds HTTP server lifetime/configuration, proxy/firewall ambiguity, CSRF-like browser reachability concerns, and still needs an application secret. Loopback is not an identity boundary. | Reject for UI/Host control. Continue using separately authenticated loopback RC only for the Host-to-rclone boundary already chosen by the project. |
| Unix-domain socket on Windows | Avoids TCP and .NET supports `AddressFamily.Unix`, but Windows socket-path lifecycle and ACL/discovery semantics add no advantage over the native pipe for a Windows-only product. | Reject for v1. |
| Anonymous pipe / inherited stdio | Strong parent-child relationship, but cannot support a UI that closes, crashes, and later reconnects to an independently living Host. | Reject. |
| Shared memory + events | Fast, but requires custom synchronization, framing, access control, crash recovery, and backpressure. This workload is control-plane traffic, not bulk transfer. | Reject. |

Microsoft documents named pipes as duplex IPC with multiple server instances and client impersonation; `System.IO.Pipes` exposes them directly [.NET named-pipe guide](https://learn.microsoft.com/en-us/dotnet/standard/io/how-to-use-named-pipes-for-network-interprocess-communication). Windows warns that default pipe security grants more access than this product needs and recommends a logon SID to restrict a pipe to a terminal-services logon session [named-pipe security](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights). Named pipes can otherwise be reachable remotely when the Server service is active, so the DACL must also deny `NT AUTHORITY\NETWORK` and clients must use `.` as the server [named pipes](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipes), [pipe names](https://learn.microsoft.com/en-us/windows/win32/ipc/pipe-names).

## Data Root identity, discovery, and authentication

### Stable identity

On first writable initialization, store a random 128-bit `dataRootId` in the authenticated Data Root manifest. It is an opaque identity, not a path hash. A path can change when the portable directory moves; conversely, two byte-for-byte copied Data Roots must be detected as clones rather than silently treated as one live root.

Before the manifest can be trusted, canonicalize the selected path only to locate it and open its lock; do not use canonical path as long-term identity. If two accessible directories have the same `dataRootId`, show a clone-identity recovery flow that assigns a new ID to one copy only after it is exclusively locked and its generation verifies.

### Names and runtime record

Derive non-secret names from `SHA-256(product-id || protocol-family || logon-SID || dataRootId)`:

- mutex: `Local\RcloneUI.Host.<base32-hash>`;
- pipe: `\\.\pipe\RcloneUI\host\<base32-hash>`.

`Local\` deliberately scopes the mutex to the current session; the logon SID in both the name derivation and ACL prevents another interactive logon of the same account from joining this Host. A named mutex is a race arbiter, not evidence that Vault state is consistent: .NET raises `AbandonedMutexException` after an owner exits without releasing it and explicitly says protected structures may be inconsistent [.NET abandoned mutex](https://learn.microsoft.com/en-us/dotnet/api/system.threading.abandonedmutexexception).

While holding the mutex, the Host exclusively opens `runtime/host.lock` and atomically publishes `runtime/endpoint.json` containing only:

```json
{
  "format": 1,
  "dataRootId": "uuid",
  "pipe": "derived-name",
  "hostPid": 1234,
  "hostStartTimeUtc": "2026-08-11T00:00:00Z",
  "sessionId": 2,
  "incarnation": "random-128-bit-id",
  "challengeKey": "random-256-bit-base64",
  "hostBuild": "semver+commit",
  "protocolMajor": 1,
  "protocolMinor": 0
}
```

Restrict the runtime directory/file to the current user where NTFS ACLs exist. On FAT/exFAT, treat the secret as protection against accidental cross-connection and stale endpoint reuse, not against another local account that can read the device; the product's established encrypted-Vault boundary remains authoritative.

### Connection authentication

1. The client connects only to local server `.` and sends `Hello` with `dataRootId`, `incarnation`, UI build/protocol range, a 256-bit client nonce, and `HMAC-SHA-256(challengeKey, canonical-hello-fields)`.
2. The Host verifies the pipe client's token/logon SID (via named-pipe impersonation), session ID, IDs/nonces, and HMAC before parsing any privileged request. Windows provides server-side client impersonation for this purpose [ImpersonateNamedPipeClient](https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-impersonatenamedpipeclient), and .NET exposes `RunAsClient` [NamedPipeServerStream.RunAsClient](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstream.runasclient).
3. The Host replies with its nonce, negotiated protocol, state epoch, capabilities, and an HMAC over both nonces and the negotiated values.
4. Derive a per-connection key with HKDF-SHA-256 from `challengeKey` and both nonces; authenticate every subsequent envelope with a monotonically increasing sequence number and HMAC. Disconnect on duplicate/out-of-order sequence, oversized frame, invalid MAC, or identity drift.

This is defense in depth, not a sandbox: malicious code already running as the same user can usually read the runtime record or inject into same-user processes. The security goals are to reject other users/sessions, remote clients, stale/cross-root clients, corruption, and unauthenticated browser/network traffic.

## One-Host startup and stale-endpoint protocol

```text
UI resolves Data Root
  -> connect using a fresh, valid endpoint record
     -> success: activate existing Host
     -> failure: launch Host once and wait with bounded backoff

Host launch
  -> acquire named mutex
     -> acquired/abandoned: exclusively open host.lock, verify Data Root, reconcile journal
     -> not acquired: connect to incumbent and request ActivateUi; then exit
  -> create secured first pipe instance
  -> publish endpoint.json only after pipe is listening
  -> accept authenticated clients
```

Rules:

- Use a bounded startup deadline (recommended 15 seconds) and expose `starting`, `recovering`, and actionable failure states; never launch repeatedly in a tight loop.
- Treat an abandoned mutex as a recovery trigger, not permission to overwrite state. Verify Vault generations and lifecycle journal before admitting mutations.
- Validate a runtime PID with both PID and process creation time; Windows Restart Manager likewise uses PID plus start time as a unique process identity [RM_UNIQUE_PROCESS](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/ns-restartmanager-rm_unique_process).
- A failed pipe connect alone does not prove the Host is dead. If mutex/lock remains owned, wait or report an unresponsive Host. Do not delete its endpoint or steal ownership.
- If the mutex and lock can be acquired and the recorded process identity is absent/mismatched, quarantine the stale endpoint, recover, and publish a new incarnation/key.
- If the pipe accepts but authentication, root identity, or version negotiation fails, do not kill or replace the incumbent. Show the precise incompatibility.
- The Host creates the first pipe instance with a security descriptor that prevents an untrusted process from creating/claiming another server instance. Windows notes that creating another instance is governed by `FILE_CREATE_PIPE_INSTANCE`, and broad generic-write rights can accidentally grant it [named-pipe security](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights).

## Wire contract and compatibility

Use `PipeTransmissionMode.Byte` and explicit framing rather than depending on Windows message mode:

```text
uint32 little-endian payloadLength (maximum 8 MiB)
UTF-8 JSON envelope (no BOM)
```

Envelope fields: `protocolMajor`, `protocolMinor`, `messageType`, `requestId`, `sequence`, `stateEpoch`, `deadlineUtc`, `body`, `mac`. Responses echo `requestId`; events have a unique `eventId` and increasing `stateRevision`. Unknown optional fields are ignored. Unknown message types, missing required fields, non-finite/ambiguous numeric forms, duplicate object keys, invalid UTF-8, and frames over the limit are rejected. Bulk logs and file lists are paged/streamed as bounded chunks with cancellation and backpressure; file content never traverses this pipe.

Contract rules:

- Protocol major is breaking. No operational command is admitted when majors do not overlap.
- Minor versions are additive. Negotiate the highest common minor and advertise feature capability IDs; clients must not infer a feature from build number.
- Host is authoritative for state. UI commands carry an idempotency key and expected state revision where mutation races matter. Retry is allowed only for commands declared idempotent.
- Every request has a deadline and cancellation ID. Cancellation means “requested,” not “rolled back”; domain result states remain those in the Transfer/Mount specifications.
- The UI obtains a full snapshot after handshake, subscribes from its revision, and re-snapshots if it misses an event or the `stateEpoch` changes after Host recovery.
- DTOs use stable string enum values and explicit units (`bytes`, `milliseconds`, UTC timestamps). Never serialize .NET type names or exceptions as the public contract.

### Split-version behavior

| UI vs Host | Behavior |
|---|---|
| Same major, overlapping minor | Connect at common minor; disable unsupported controls from capability list. |
| UI newer, major incompatible | Host keeps work running; UI offers to open matching installed UI or complete/roll back the update. No mutation requests. |
| Host newer, major incompatible | UI displays a minimal signed incompatibility record and launches matching UI through updater metadata; it must not downgrade a live Host. |
| Executables from different release directories | Allowed only during a recorded update handoff and only if compatibility negotiation succeeds. Otherwise enter repair mode. |

Keep the previous complete version directory inactive. Updates switch an `active-version` pointer only after files/signatures are verified. The updater asks the authenticated Host to `PrepareUpdate(planHash)`. The Host refuses while Transfer Tasks or Mounts are active, drains clients, persists a journal checkpoint, returns a one-time handoff token, and exits cooperatively. The updater waits for process identity and lock release, never just a missing pipe, switches the pointer, starts the new Host with the token and expected prior journal epoch, and requires a health handshake within a bounded window (recommended 30 seconds). Health means: executable integrity accepted, Data Root locked, manifest/Vault readable or recoverably locked, journal reconciled, pipe authenticated, expected protocol available, and no unexpected rclone child. On failure, terminate the failed new Host, restore the old pointer, and start the old Host against the unchanged/compatible generation. A Vault migration that cannot be read by the old Host must not be committed until the new Host health check passes; follow ADR-0006 copy-on-write generations.

## Job Object ownership

The Host creates one unnamed Job Object per Host incarnation, sets `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, and keeps its only inheritable authority handle private. Closing the last handle with that flag terminates associated processes [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects), [limit flags](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information).

For each rclone process:

1. Create it suspended with handle inheritance disabled except an explicit allow-list.
2. Assign it to the Host Job Object.
3. Resume only after assignment succeeds; otherwise terminate the suspended process and mark launch failed.
4. Do **not** set `JOB_OBJECT_LIMIT_BREAKAWAY_OK` or `JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK`, and do not use `CREATE_BREAKAWAY_FROM_JOB`.
5. Associate the job with an I/O completion port and journal process-new/process-exit notifications, while also retaining process handles for authoritative exit codes.

Windows automatically associates normal descendants with the parent's job unless breakaway is enabled. Windows 10/11 support nested jobs (introduced in Windows 8), but parent-job limits remain effective and assignment can fail if a valid hierarchy cannot be formed [nested jobs](https://learn.microsoft.com/en-us/windows/win32/procthread/nested-jobs). Therefore startup must call `IsProcessInJob`, attempt assignment while suspended, and report a distinct `HostEnvironmentJobConflict` if an external launcher job prevents the required containment. Do not silently run rclone uncontained. A future updater must remain outside the Host job so it survives the Host's cooperative exit; pass it no job handle and authenticate its one-time handoff.

`KILL_ON_JOB_CLOSE` is crash containment, not graceful shutdown. Normal exit first stops admission, asks rclone to cancel/unmount, checkpoints the journal, waits to policy deadline, then explicitly terminates leftovers before closing the job handle. `TerminateJobObject` kills all processes in the job and nested child jobs and is reserved for the forced-recovery path [TerminateJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-terminatejobobject).

## Lifecycle journal boundary

The lifecycle journal is durable domain intent and reconciliation evidence, not an IPC transcript or raw process log. The Host is its sole writer.

Persist before side effects:

- command/idempotency ID, actor (`manual`, `schedule`, `recovery`, `update`), Data Root and Host epoch;
- validated Transfer Task/Mount Profile revision and requested transition;
- operation ID, rclone invocation identity, sanitized argument/config hashes, intended paths by protected Vault reference;
- `launch-intent` before process creation and `process-started` immediately after assigned/resumed, including PID plus start time;
- cancellation, shutdown, update-drain, unmount, and safe-stop intent;
- observed process exit, rclone result summary, pending uploads/deletes/conflicts, and final domain outcome;
- suspend/resume, session end, Host recovery, and journal checkpoint boundaries.

Do not persist secrets, bearer tokens, raw IPC payloads, full command lines, or HMAC keys. High-rate progress is an in-memory/event-stream concern; checkpoint only bounded coarse progress needed to explain/reconcile an interruption. On restart, any nonterminal intent is `Interrupted/NeedsReconciliation` until observable state proves otherwise; never infer success from a missing process.

For user session changes, a hidden Host window can register for session notifications and handle `WM_WTSSESSION_CHANGE` [WM_WTSSESSION_CHANGE](https://learn.microsoft.com/en-us/windows/win32/termserv/wm-wtssession-change). For shutdown, checkpoint continuously and treat `WM_QUERYENDSESSION`/`WM_ENDSESSION` as a short finalization opportunity, not a transaction window. Microsoft directs applications to save state promptly and notes forced shutdown can still terminate them [Restart Manager application guidance](https://learn.microsoft.com/en-us/windows/win32/rstmgr/guidelines-for-applications). Restart Manager is useful to identify/update processes holding version files, but it does not support shutdown across sessions [Restart Manager](https://learn.microsoft.com/en-us/windows/win32/rstmgr/about-restart-manager).

## Threat model

Protected against:

- a different Windows user or logon session connecting to or pre-creating the endpoint;
- remote named-pipe access;
- stale endpoint records, PID reuse, cross-Data-Root confusion, replay on a new Host incarnation;
- malformed/oversized frames, unauthenticated commands, accidental duplicate mutations;
- UI crash, rclone crash, Host crash leaving ordinary descendants, and split-version update failure;
- portable-media loss or non-atomic writes through authenticated generations and reconciliation.

Not protected against:

- malware or an administrator controlling the same user process/session;
- offline reading of plaintext runtime/cache/log data on FAT/exFAT;
- kernel compromise, process injection, executable replacement before signature/integrity verification;
- rclone/backend semantics beyond verified capabilities;
- abrupt power loss after an external remote side effect but before its local journal acknowledgement. Reconciliation must represent this as uncertainty, not success.

## Failure matrix

| Failure | Required result |
|---|---|
| Two UIs launch simultaneously | One Host wins mutex/lock; both authenticate to it. Loser Host exits without writing state. |
| Endpoint exists, pipe absent, mutex owned | Wait bounded time, then report unresponsive Host; do not steal/delete. |
| Endpoint exists, mutex/lock recoverable, PID+start time stale | Quarantine endpoint, reconcile, publish new incarnation. |
| Mutex abandoned | Verify Data Root/Vault/journal, mark incomplete work interrupted, then listen. |
| Wrong user/session or remote client | DACL/identity check rejects before command parsing. |
| Bad HMAC/replay/oversized frame | Disconnect, rate-limit diagnostics, no state change. |
| UI crashes/disconnects | Host and work continue; reconnect uses snapshot/revision. |
| rclone launch cannot be job-contained | Kill suspended process; operation fails with environment diagnostic. |
| rclone exits unexpectedly | Journal observed exit; operation becomes interrupted/failed, never success. |
| Host crashes | Last job handle closes and descendants terminate; next Host reconciles journal. |
| Data Root disappears | Host stops new work, retains bounded in-memory status, never relocates silently. |
| Session locks | Host continues configured work; UI-sensitive commands require unlock state. |
| Logout/shutdown | Stop admission, checkpoint, cooperative stop within deadline; remaining work becomes system-interrupted. |
| Protocol major mismatch | Read-only incompatibility UX only; incumbent Host/work remain untouched. |
| Update health check fails | Restore old active version and old compatible Vault generation. |
| Updater dies after old Host exits | Next normal launch reads update journal, selects last healthy pointer/generation, and resumes recovery. |

## Acceptance criteria

- Exactly one writer can own a Data Root under 100 concurrent UI/Host launches; all successful clients converge on the same Host incarnation.
- Pipe ACL tests reject another user, another logon session, and network identity; same-session current-user clients still must pass HMAC handshake.
- Fuzz tests reject truncation, invalid UTF-8/JSON, duplicate keys, integer overflow, frames over 8 MiB, unknown required fields, replay, and sequence gaps without mutation or unbounded allocation.
- Compatibility tests cover current/current, previous-UI/current-Host, current-UI/previous-Host, nonoverlapping major, unknown additive fields, and missing capabilities.
- Every mutation has an idempotency test across disconnect-before-response and Host restart.
- Killing the UI does not affect work. Killing the Host terminates the complete rclone descendant tree and recovery never labels it successful.
- Tests run Host beneath a compatible external job and an intentionally incompatible job; rclone never runs if containment fails.
- Stale endpoint tests include PID reuse simulation using mismatched creation time, abandoned mutex, corrupt endpoint, cloned Data Root ID, and unavailable removable Data Root.
- Shutdown/session tests cover lock/unlock, suspend/resume, logoff, Restart Manager close, forced termination, and journal recovery.
- Update fault injection at every handoff step yields either the old healthy version/generation or the new healthy version/generation, never two writers or a partially replaced active directory.
- Logs and journal scans prove no Master Password, Vault key, provider token, challenge key, raw sensitive path, or unsanitized rclone command line is persisted.

## Required implementation spikes

1. **Pipe ACL and identity spike:** verify the chosen .NET LTS can create the exact logon-SID DACL and first-instance semantics without an unsafe compatibility shim; verify behavior across Fast User Switching, `runas`, elevated UI, and FAT/exFAT Data Roots.
2. **Job containment spike:** launch rclone suspended, assign/resume, observe descendants through a completion port, then test under Windows Terminal, Explorer, debugger, CI runner, and updater parent jobs on Windows 10 and 11.
3. **Shutdown spike:** measure the Avalonia/Host hidden-window delivery of WTS and end-session messages and prove bounded checkpoint/cancel behavior under real logoff and forced shutdown.
4. **Update crash matrix spike:** implement only the handoff journal/pointer harness and inject process termination/power-loss equivalents around every step, including a copy-on-write Vault schema migration.
5. **Protocol spike:** generate golden JSON vectors and cross-version contract tests before selecting a source generator or RPC library. Do not adopt a framework that hides framing limits, authentication, state revisions, or compatibility policy.

## Decision risks

- Same-user IPC authentication cannot protect against same-user malware; documentation must not overstate it.
- Exact named-pipe ACL construction and first-instance behavior varies by the selected .NET package/API surface and needs a Windows spike.
- Job assignment may be constrained by an external parent job. Failing closed is safer but can make the app unusable in some launch environments; measure before release.
- A portable Data Root on FAT/exFAT weakens runtime-secret confidentiality and crash atomicity. The design depends on authenticated generations, conservative reconciliation, and explicit warnings.
- Split-version rollback is only real if every schema migration preserves the prior readable generation until new-Host health succeeds; an in-place migration would invalidate the guarantee.

