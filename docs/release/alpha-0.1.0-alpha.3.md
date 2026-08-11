# Rclone UI 0.1.0-alpha.3 internal candidate

This unsigned Windows x64 portable candidate adds the first ordinary-user download workflow.

- Artifact: `artifacts/release/RcloneUI-0.1.0-alpha.3-win-x64.zip`
- SHA-256: `B7CCE8E53FF77F66C88A23BA58B23E3F0247F6E4F285C2E92940C3862A7544AD`
- Entry point: root-level `Rclone UI.exe`

The Transfer page now defaults to Download: select a source Remote, optionally enter its path, and choose a local destination with the system folder picker. The Host requires an existing absolute local directory and maps it directly to the managed rclone runtime. Remote-to-remote Copy remains available as an advanced transfer mode.

Full verification passed with 209 tests, one intentionally skipped hardware-bound Argon2 test, zero warnings, and a clean dependency audit. Fresh-extract launch smoke also passed: launcher exit 0, Desktop and Host alive, endpoint under the explicit Data Root, and no default data directory created.

Accepted-preview confirmation, pause/cancel, multi-run queue controls, browser-based remote path selection, and richer completion/error presentation remain later Alpha work.
