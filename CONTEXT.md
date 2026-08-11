# Rclone UI

A Windows desktop product that makes rclone storage operations accessible to ordinary users while retaining an advanced escape hatch.

## Language

**Remote**:
A named connection to a storage provider, represented by an rclone configuration entry.
_Avoid_: Account, cloud disk, connection

**Transfer Task**:
A user-defined copy, move, or sync operation between two locations, including its filters and runtime limits.
_Avoid_: Job, command, script

**Mirror Sync**:
A one-way Transfer Task that makes the target match the source, including removal of target content absent from the source. It is not a bidirectional merge.
_Avoid_: Two-way sync, backup, mirror copy

**Mount**:
A Remote exposed as a Windows drive or filesystem location for interactive access.
_Avoid_: Mapping, virtual disk

**Mount Profile**:
A saved definition for creating a Mount, including its Remote path, preferred drive letter, Windows drive type, cache preset, and lifecycle preferences. A Mount Profile can exist while no Mount is active.
_Avoid_: Mount, drive, mount task

**Portable App**:
The distributable Rclone UI package that runs without installing the application itself and keeps its application-owned files together.
_Avoid_: Installer edition, installed app

**Managed Component**:
An external runtime dependency whose presence and version Rclone UI detects and can obtain or update for the user, such as rclone or WinFsp.
_Avoid_: Bundled tool, plugin

**Data Root**:
The user-chosen portable directory containing all mutable state owned by Rclone UI. It defaults to `data/` beside the Portable App and is never silently redirected elsewhere.
_Avoid_: Profile, AppData, workspace

**Master Password**:
The user-held secret that unlocks an encrypted rclone configuration. Remembering it for one Windows user on one computer is an optional convenience and is not part of the portable data.
_Avoid_: PIN, account password, encryption key

**Vault**:
The encrypted portion of the Data Root containing Remote metadata, Transfer Task definitions, schedules, and activity history. It is unlocked with the Master Password but remains distinct from rclone's encrypted configuration.
_Avoid_: Database, secure storage, rclone config

**Background Host**:
The per-user, logged-in-session process that owns scheduling, running Transfer Tasks, Mounts, and the managed rclone process independently of the visible UI. It is part of the Portable App and is not a Windows service.
_Avoid_: Service, daemon, tray app

**Accepted Preview**:
An immutable, time-bounded Transfer Task preview whose exact configuration and capability evidence has been confirmed for a destructive execution.
_Avoid_: Dry-run log, confirmation token

**Transfer Run**:
One admitted execution of a specific Transfer Task revision, including its phases, manifests, verification evidence, and terminal result.
_Avoid_: Job, process, rclone command

**Safety Generation**:
A run-specific retained set of target objects that would otherwise have been replaced or deleted by a Transfer Run.
_Avoid_: Backup, trash, recycle bin

**Recovery Cache**:
Preserved Mount cache content whose remote-write state cannot be proved clean and therefore requires inspection or recovery before reuse or deletion.
_Avoid_: Temporary cache, disposable cache
