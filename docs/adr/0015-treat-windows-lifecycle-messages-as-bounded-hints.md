---
status: accepted
---

# Treat Windows lifecycle messages as bounded hints

Receive WTS session, power, end-session, and Restart Manager notifications through an invisible top-level Background Host window, but treat them only as bounded hints. Durable lifecycle intent is journaled continuously; no correctness guarantee depends on every notification arriving or on Windows granting enough time for cleanup.

Each window handler records a compact idempotent observation, closes relevant admission, signals asynchronous work, and returns within a 250 ms internal target. It must not wait for transfers, Remote calls, Mount drain, rclone exit, Vault migration, or update handoff. `WM_QUERYENDSESSION` returns promptly after checkpointing and does not block shutdown to promise clean completion.

Actual cleanup is best effort. Forced termination is handled by Job containment and next-start reconciliation, with unfinished work reported as system/crash interrupted. The design is accepted from the safe dispatch prototype and remains release-gated on real Windows 10/11 lifecycle events.
