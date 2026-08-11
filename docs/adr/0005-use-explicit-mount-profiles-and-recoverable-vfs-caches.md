---
status: accepted
---

# Use explicit Mount Profiles and recoverable VFS caches

Represent saved Mount Profiles separately from active Mounts, default cloud storage to unelevated network drives with a standard writes-cache preset, and treat VFS cache content as recoverable plaintext state that must never be silently discarded. Mount startup, configuration changes, component updates, and shutdown therefore pass through explicit validation and safe-unmount states, while crash-interrupted Mounts require inspection rather than automatic remount; this trades automatic recovery and minimal local storage for predictable drive identity and protection of pending user writes.
