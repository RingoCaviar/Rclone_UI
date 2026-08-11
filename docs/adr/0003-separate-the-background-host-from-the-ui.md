---
status: accepted
---

# Separate the Background Host from the UI

Run scheduling, Transfer Tasks, Mounts, the tray, and the managed rclone process in a per-user Background Host that is separate from the Avalonia UI, and bind the owned rclone process tree to the Host with Windows Job Object semantics. This makes window closure and UI crashes harmless to active work without installing a Windows service, while preserving a single explicit lifecycle authority for queuing, suspend/resume, shutdown, updates, and crash reconciliation during the logged-in session.
