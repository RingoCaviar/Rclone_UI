---
status: accepted
---

# Bind destructive transfers to preview and safety copies

Require configuration-bound dry-runs for Move, Mirror Sync, and high-risk options; preserve replaced or deleted target content in seven-day safety copies by default; and report cancellation as partial rather than attempting implicit rollback. These defaults deliberately trade speed, storage cost, and one-click convenience for understandable and recoverable behavior, because rclone operations are not transactions and ordinary users must never mistake one-way mirroring, cancellation, or process success for guaranteed data preservation.
