# Rclone UI 0.1.0-alpha.4 internal candidate

This unsigned Windows x64 portable candidate packages the first UI-correction and read-only Mount milestone after Alpha 3.

- Artifact: `artifacts/release/RcloneUI-0.1.0-alpha.4-win-x64.zip`
- SHA-256: `A623DD1AF827ACD6C0F69A86EAB7E5CD3161CE179D94400D091CC2D0D5958DEA`
- Entry point: root-level `Rclone UI.exe`

The complete shell now switches between Chinese and English. Persistent sidebar indicators distinguish the automatically connected Background Host from the Vault lock state. Every new Desktop session locks the Vault by default and an explicit **Lock now** action is available after unlock. Advanced options expand only where working controls exist.

The Mount page now offers the first production-connected preset: select a Remote, optional subpath, volume name, and preferred drive letter, then mount it as a read-only Windows network drive. The Host rejects occupied drive letters and stale rclone capability bindings, and reports Ready only after the drive namespace is visible. Safe unmount reports success only after the namespace disappears.

Full verification passed with 215 tests, one intentionally skipped hardware-bound Argon2 test, zero warnings, and a clean dependency audit. Fresh-extract launch smoke passed: the root launcher handed off successfully, Desktop and Host stayed alive, the endpoint was created under the explicit Data Root, no default `data/` directory was created, and the pinned rclone v1.75.0 plus libargon2 20190702 components were present.

The read-only Mount path still requires the Windows 10/11 and real WinFsp/Explorer qualification matrix in Issue #58. Writable VFS caching, forced unmount, saved Mount profiles, and automatic remount remain disabled until their production evidence paths are complete.
