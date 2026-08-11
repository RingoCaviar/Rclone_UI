# Prototype result

Environment: Windows NT 10.0.26200.0, .NET 8.0.23.

- Invisible top-level window creation and `WTSRegisterSessionNotification` succeeded.
- Self-delivered lock, unlock, suspend, automatic resume, query-end-session, end-session, and Restart Manager query messages all reached the window procedure.
- Every event appended a journal intent and called `FileStream.Flush(true)`.
- Seven durable journal lines were observed.
- Slowest measured handler duration was 21.238 ms.
- `WM_QUERYENDSESSION` returned acceptance immediately after the checkpoint.

These are dispatch/checkpoint measurements, not proof of operating-system delivery. Real lock, sleep/hibernate, Restart Manager, logoff, shutdown, forced termination, Windows 10, .NET 10, rclone cancellation, and Mount drain remain untested.
