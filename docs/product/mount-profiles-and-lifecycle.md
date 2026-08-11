# Mount Profiles and lifecycle

## Model

- Store a reusable Mount Profile separately from an active Mount.
- Give each Profile a stable identity, independent display name, unique Windows volume label, Remote and optional subpath, preferred drive letter, drive type, cache preset, cache location, capacity policy, and lifecycle preferences.
- Preserve Profile identity and display name when its Remote is renamed.
- Test that a selected subpath exists and can be listed before first mount. If it later disappears, fail the Mount rather than falling back to the Remote root.

## Presets

Expose four presets:

### Read-only browsing

- Reject writes and minimize disk caching.
- Clearly label the drive read-only because some Windows applications may not present a useful write error.

### Standard read/write

- Default preset.
- Use rclone VFS writes caching to support normal filesystem write operations without caching all reads.
- Default cache capacity: 10 GiB.

### Maximum compatibility

- Use full VFS caching for reads and writes.
- Explain the improved random-I/O and application compatibility alongside increased local disk use.
- Default cache capacity: 20 GiB.

### Custom

- Expose the complete discovered VFS and Mount options to advanced users.
- Validate options and show deviations from a named preset.

## Windows drive presentation

- Default cloud Remotes to a network drive to avoid fixed-disk latency assumptions and unnecessary Explorer preview traffic.
- Offer fixed-disk compatibility mode for applications that reject network paths.
- Run the Background Host and Mount unelevated so the current user's Explorer can see the drive.
- Do not change linked-connections policy, run the Host as administrator, install a service, or mount as SYSTEM.
- Explain directory mounting as an alternative when an administrator-only application cannot see the user's drive mapping.

## Drive letter and volume name

- Allow automatic or explicitly chosen drive letters.
- After the first successful automatic assignment, remember that letter as the Profile preference.
- If the preferred letter is occupied, enter Drive Letter Conflict and ask the user; never silently select another letter.
- Require network share/volume names to be unique where Windows or rclone requires uniqueness.
- Never create a second Mount as an implicit recovery action.

## Cache location and capacity

- Default cache content to `cache/mounts/<profile-id>/` under the Data Root.
- When the Data Root is slow or removable, recommend an explicitly selected local high-speed directory and label it non-portable and rebuildable.
- Never silently redirect cache content to `%AppData%`.
- Warn when configured capacity conflicts with current free space and allow capacity or minimum-free-space policies.
- On soft capacity pressure, evict only uploaded, closed, eligible old cache entries.
- Never evict dirty, uploading, open, quarantined, or recovery-needed content.

## Offline and pending writes

- Do not advertise Mount as a complete offline synchronization system.
- Permit reads of already cached data according to rclone capability.
- Permit writes to remain pending during temporary disconnection only within the configured cache and retry limits.
- Show pending file count, bytes, oldest age, last upload attempt, and risk prominently.
- Warn before Background Host exit, cache-device removal, relocation, update, or shutdown while writes remain pending.

## Start and automatic mount

- Let each Profile opt into login-session automatic mount; keep it disabled by default.
- Before starting, require an unlocked operational session, healthy Remote test, compatible managed rclone endpoint and mount type, supported WinFsp installation, available preferred drive letter, existing subpath, and writable cache when required.
- On failure, keep the Profile stopped with one actionable status. Do not loop UAC prompts or repeatedly remount.
- A normal login or coordinated restart may honor auto-mount; a crash-interrupted Mount requires explicit recovery even when auto-mount is enabled.

## WinFsp

- Detect WinFsp presence, architecture, and compatible version before enabling Mount.
- If missing or incompatible, offer download and verification from the official source and launch only the official installer through explicit UAC.
- Keep detection and download unelevated. Never elevate the UI or Background Host.
- Re-detect after the installer completes and let the user explicitly retry the Mount.

## Sleep, connectivity, and crashes

- On suspend, retain the Profile and active state record without deliberately unmounting.
- On resume, reconcile the original rclone Mount and preserve the preferred drive letter.
- If recovery fails, enter Needs Remount, stop new write attempts, show pending-cache risk, and notify the user.
- During Remote outage, keep the drive in Degraded Connection while VFS retries; do not automatically unmount or change letters.
- If rclone or the Background Host crashes, record the Mount as interrupted. On restart, inspect the cache, Remote, mount endpoint, and drive letter before offering Check and Remount.

## Editing

- Apply display-only changes immediately.
- Require safe unmount before changing credentials, Remote path, preferred letter, fixed/network type, cache mode, cache location, VFS options, or other runtime behavior.
- Allow changes to be staged, then validate, apply, and optionally remount after the old Mount has stopped.
- Do not create a split state where the saved Profile differs silently from the active Mount.

## Safe unmount

- Enter Safe Unmount, reject new opens and writes where the platform permits, and report open files plus pending uploads.
- Wait for pending writes to complete before normal unmount.
- Offer Return, Continue Waiting, and an advanced Force Unmount.
- Explain that force can lose pending writes; preserve cache content for recovery after a forced operation.
- Never report successful safe unmount while unverified writes remain.

## Profile deletion

- Stop an active Mount before deleting its Profile.
- Show every pending or recovery-needed cache item.
- Offer Delete Profile and Cache only after proving that no unuploaded data remains.
- Otherwise preserve and identify the recovery cache; never silently delete it.

## Diagnostics and cache recovery

Show:

- Remote and subpath;
- drive letter, volume name, and fixed/network type;
- running time and connection state;
- managed rclone and WinFsp versions;
- cache preset, path, capacity, use, dirty files, and pending bytes;
- open files when discoverable;
- recent errors, retry state, and bandwidth state.

Provide Test Remote, Open Cache Directory, Export Redacted Diagnostics, Safe Remount, and Safe Unmount.

When cache state is corrupt or inconsistent, isolate it and stop new writes. Offer scan, export of recoverable files, re-verification, and user-confirmed cleanup. Never automatically clear a suspect cache.

## Updates

- Permit checking, downloading, and verifying application, rclone, and WinFsp updates while Mounts are active.
- Do not replace relevant components until affected Mounts complete safe unmount.
- Show affected Profiles and pending cache state.
- After updating, re-detect versions and capabilities, then let the user choose whether to remount. Do not force-close open files.
