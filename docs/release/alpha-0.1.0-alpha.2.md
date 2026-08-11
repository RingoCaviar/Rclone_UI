# Rclone UI 0.1.0-alpha.2 internal candidate

This unsigned internal Windows x64 portable candidate supersedes alpha.1.

## Artifact

- File: `artifacts/release/RcloneUI-0.1.0-alpha.2-win-x64.zip`
- SHA-256: `E82301C153962F1F470F4F3E078BD5BDA1DA6F98DA4C1383153CA81499F4DA96`
- Entry point: root-level `Rclone UI.exe`
- Bundled rclone: v1.75.0
- Bundled libargon2: reviewed 20190702 x64 candidate

## Fresh-extract smoke result

The root launcher exited successfully after starting the bundled Desktop, forwarded an explicit Data Root, and displayed no console. Desktop and Background Host remained alive, the endpoint was published beneath the selected Data Root, no default `desktop/data` directory was created, and the release manifest reported zero hash mismatches.

The launcher has no project or third-party dependencies. It resolves only `desktop/RcloneUI.exe` beneath its own package root and shows a native actionable error when extraction is incomplete.

## Known gates

This remains unsigned and is not a qualified public release. WinFsp version matrices, accessibility, elevated/unelevated sessions, destructive recovery, updater rollback and Windows 10/11 release qualification remain human-owned.
