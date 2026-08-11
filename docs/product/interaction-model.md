# Interaction model

Rclone UI uses the **Task Home** model selected as Variant A in the ordinary-user UI prototype.

## Application shell

- Keep a persistent left sidebar with: Home, Remotes, Transfer Tasks, File Browser, Mounts, Schedules, Activity and Logs, and Settings.
- Keep application identity, portable-edition status, notifications, and the current user/session affordance in the top bar.
- Use Home as the default route and operational summary, not a file browser or setup wizard.

## Home

- Lead with system health and the number of currently running tasks.
- Make New Task the primary action.
- Present four ordinary-language shortcuts: Copy or Sync, Browse Files, Mount Drive, and Add Remote.
- Show active and recently completed Transfer Tasks with progress, speed, ETA, outcome, source, and destination.
- Show Remote health, capacity when available, encryption state when relevant, and authentication problems beside task activity.

## Progressive disclosure

- Use task-oriented labels before rclone terminology; retain precise rclone terms in advanced details and diagnostics.
- Keep advanced options reachable from each relevant flow rather than making them a top-level starting point.
- Explain destructive sync/move effects at preview and confirmation time.

## Rejected top-level models

- Do not make the three-step guided workspace from Variant B the permanent application shell; guided steps may still be used inside first-run, Remote setup, and task-creation flows.
- Do not make the dual-pane explorer from Variant C the permanent application shell; file management remains a dedicated sidebar destination.

## Primary source

The compared variants are preserved on the `prototype/ordinary-user-ui` branch under `prototypes/ordinary-user-ui/`.
