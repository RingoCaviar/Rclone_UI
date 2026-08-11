# Background task and shutdown lifecycle

## Process model

- Run a per-user Background Host separately from the Avalonia UI.
- The Background Host owns the managed rclone process, Transfer Task queue, schedules, Mounts, lifecycle journal, and tray icon.
- Closing or crashing the UI does not interrupt Background Host work. Opening the UI reconnects to the existing Host for the selected Data Root.
- Do not install a Windows service and do not execute schedules while the user is logged out.
- Use Windows Job Object ownership so an unexpected Background Host exit cannot leave an unmanaged rclone process tree.

## Window, tray, and startup

- Closing the main window hides it to the tray and explains this behavior once.
- Provide explicit Exit actions in the tray and Settings.
- When there are no Transfer Tasks, Mounts, or enabled schedules, allow an opt-in setting that makes the close button exit.
- Offer per-user login startup, disabled by default. Make the created startup entry visible, removable, and repairable after the Portable App moves.
- Do not register each application schedule with Windows Task Scheduler. Schedules run only while the Background Host is running.
- Clearly warn that exiting the Background Host prevents future schedules from running.

## Queue and concurrency

- Run two Transfer Tasks concurrently by default, configurable from one through eight.
- Put additional work in a visible queue with pause, priority adjustment, and Run Next controls.
- Mounts do not consume Transfer Task slots but share configured global bandwidth limits.
- Serialize tasks that clearly contain overlapping write targets or otherwise unsafe path overlap.
- When overlap cannot be determined, warn and require an advanced explicit override before concurrent execution.

## Sleep, resume, and network

- On suspend, record running work as system-paused without deliberately cancelling it.
- On resume, wait for required networks and storage to stabilize, then reconcile the existing rclone job instead of starting a duplicate.
- If a Mount cannot recover, keep its assigned drive letter and mark it as requiring remount; never silently select a different letter.
- Coalesce multiple missed occurrences of one schedule into at most one catch-up run.
- By default, catch up only when the latest missed occurrence is within 24 hours. Each schedule may instead skip missed occurrences.
- When a scheduled task lacks network access, enter Waiting for Network for 30 minutes by default, configurable from zero through 24 hours. Run once if connectivity returns; otherwise record failure without accumulating duplicate runs.
- Keep an offline Mount present in a degraded state while rclone/VFS retries. Notify after a sustained-failure threshold and offer Remount or Unmount; do not automatically unmount.

## Locked session

- Lock sensitive UI when Windows locks, suspends, or the user explicitly locks Rclone UI.
- Permit already configured, enabled schedules to start while Windows remains logged in and locked, provided the Background Host was successfully unlocked earlier in that session.
- Permit active Transfer Tasks and Mounts to continue.
- Block new manual operations, configuration changes, and sensitive detail views until unlock.
- Retain only the operational material needed by the Background Host in memory and clear it on logout or Host exit.

## Explicit exit

- If work is active, show the affected Transfer Tasks, Mounts, and schedules before exiting.
- Offer: Return and Keep Running, Exit When Transfers Finish, or Cancel Transfers and Unmount Then Exit.
- Waiting for transfers does not wait indefinitely for Mounts; the user must choose what to do with each active Mount.
- Keep force termination in an advanced recovery surface with an explicit partial-results and mount-corruption warning.

## Windows logout and shutdown

- Stop admitting new work, persist the lifecycle journal, cooperatively cancel transfers, and unmount within the available shutdown window.
- Request only a short shutdown delay and never block Windows indefinitely.
- Mark operations that cannot finish as interrupted by the system and reconcile them on the next startup.

## Crashes and interrupted work

- If rclone exits while the Background Host survives, immediately mark affected operations interrupted, preserve diagnostics, and notify the user.
- If the Background Host exits unexpectedly, Job Object ownership terminates its rclone process tree.
- On restart, reconcile the lifecycle journal with observable filesystem and rclone state.
- Do not automatically rerun interrupted work by default. Present source, target, known progress, and interruption reason, then let the user retry or dismiss it.
- Only task types separately proven safe and resumable may opt into automatic recovery in the future.

## Updates

- Allow update discovery, download, verification, and staging while work is active.
- Defer replacement while Transfer Tasks or Mounts are active.
- Let the user wait for idle or explicitly cancel tasks and unmount before applying.
- The updater must use the Host shutdown protocol and must not blindly kill the Background Host or rclone.

## Notifications

- Notify by default for failures, required user action, missed schedules, degraded Mounts, and updates waiting for idle.
- Keep progress changes in the UI and tray without repeated notifications.
- Keep successful-task notifications disabled by default, with global and per-task opt-in.
