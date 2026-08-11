# Rclone UI

A Windows desktop product that makes rclone storage operations accessible to ordinary users while retaining an advanced escape hatch.

## Language

**Remote**:
A named connection to a storage provider, represented by an rclone configuration entry.
_Avoid_: Account, cloud disk, connection

**Transfer Task**:
A user-defined copy, move, or sync operation between two locations, including its filters and runtime limits.
_Avoid_: Job, command, script

**Mount**:
A Remote exposed as a Windows drive or filesystem location for interactive access.
_Avoid_: Mapping, virtual disk

**Portable App**:
The distributable Rclone UI package that runs without installing the application itself and keeps its application-owned files together.
_Avoid_: Installer edition, installed app

**Managed Component**:
An external runtime dependency whose presence and version Rclone UI detects and can obtain or update for the user, such as rclone or WinFsp.
_Avoid_: Bundled tool, plugin
