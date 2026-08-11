# Host session and shutdown message validation

Validation date: 2026-08-11  
Issue: #21  
Prototype evidence: branch `prototype/host-lifecycle-messages`, commit `2906146`

## Verdict

An invisible top-level Windows window can serve as the Background Host's notification adapter. The prototype registered for WTS session notifications, dispatched session, power, end-session, and Restart Manager-shaped messages through one window procedure, and durably checkpointed each intent with a maximum observed handler time of 21.238 ms.

These messages are hints and short finalization opportunities, never the sole source of durable truth. The Host must journal lifecycle state continuously. Window handlers record a bounded intent, update in-memory admission state, trigger asynchronous cancellation/reconciliation, and return promptly; they never wait for transfers, VFS drain, network calls, rclone exit, Vault migration, or updater handoff.

The safe self-test proves message routing and checkpoint cost only. Release qualification remains conditional on actual Windows 10/11 lock, sleep/hibernate, Restart Manager, logoff, shutdown, and forced-termination delivery.

## Observed behavior

Environment: Windows NT `10.0.26200.0`, .NET 8.0.23.

- invisible top-level window creation succeeded;
- `WTSRegisterSessionNotification(..., NOTIFY_FOR_THIS_SESSION)` succeeded;
- self-delivered lock, unlock, suspend, automatic resume, query-end-session, end-session, and Restart Manager query messages reached the same window procedure;
- each message appended a journal intent and called `FileStream.Flush(true)`;
- seven expected durable journal records were present;
- the slowest handler completed in 21.238 ms;
- `WM_QUERYENDSESSION` returned true after checkpointing rather than waiting for cleanup.

Self-delivered messages do not prove that the operating system always sends every notification, sends them in a particular order, or gives a stable time budget before forced termination.

## Message contract

| Signal | Immediate Host action | Deferred reconciliation |
|---|---|---|
| WTS session lock | Lock sensitive UI/Vault presentation, journal observation, block new manual sensitive work. | Existing configured tasks/Mounts continue under accepted policy. |
| WTS session unlock | Journal observation; require normal unlock policy before sensitive commands. | Refresh UI state and reconcile missed events. |
| `PBT_APMSUSPEND` | Journal suspend observation and stop new admission; do not deliberately unmount. | None can be assumed before sleep. |
| `PBT_APMRESUMEAUTOMATIC` | Journal resume observation and schedule reconciliation. | Check Data Root identity, Host/rclone, Mounts, Remotes, clocks, and schedules. |
| `WM_QUERYENDSESSION` | Persist `SystemEndRequested`, close admission, signal cooperative cancellation, return true promptly. | Best effort only; do not block Windows waiting for clean completion. |
| `WM_ENDSESSION(wParam=true)` | Persist final observation if possible and enter ending state. | Assume termination may occur immediately. |
| Restart Manager close | Same checkpoint/close-admission path, tagged `RestartManager`. | Cooperate only within bounded update policy; no cross-session assumption. |
| Process kill/power loss | No handler guarantee. | Job containment and next-start journal reconciliation are authoritative. |

## Timing and implementation rules

- Target every window handler to finish within 250 ms, with a hard internal watchdog/diagnostic at that threshold. The observed 21 ms flush leaves margin but is not a platform guarantee.
- The handler writes one compact, pre-serialized lifecycle intent through the sole journal writer. It does not perform SQLite migrations, snapshots, Remote calls, or log rotation.
- Cancellation, safe-unmount, and rclone shutdown run asynchronously from already-journaled domain state. They may complete if Windows allows time, but success is never inferred from receiving an end-session message.
- Do not use shutdown blocking to promise clean transfers. If a future narrowly scoped block reason is considered, it requires a separate UX/platform decision and strict deadline.
- Duplicate and reordered messages are idempotent observations keyed by Host epoch and monotonic state revision.
- Missing resume events are covered by reconciliation on any later timer, UI connection, network change, or operation admission.
- Forced termination is expected: the Job closes, descendants terminate, and nonterminal operations become `InterruptedBySystemOrCrash` on next start.

## Release-gated matrix

- Windows 10 and 11 with selected .NET LTS;
- real lock/unlock, sleep, hibernate, automatic/user resume, and sleep-to-hibernate transition;
- Restart Manager initiated close/update;
- sign-out, shutdown, restart, canceled shutdown, forced shutdown, and process termination;
- active Transfer, cached-write Mount, idle Mount, locked Vault, missing Data Root, and journal I/O delay/failure;
- verify message order/timestamps, handler duration, journal durability, rclone/Job outcome, and next-start business state.

The matrix must never require delaying Windows indefinitely. An absent/late signal or incomplete cleanup produces a truthful interrupted/recovery state rather than clean success.

## Prototype disposition

The harness remains off `main` as primary evidence on [`prototype/host-lifecycle-messages`](https://github.com/RingoCaviar/Rclone_UI/tree/prototype/host-lifecycle-messages/prototypes/host-lifecycle-messages) at commit `2906146`.
