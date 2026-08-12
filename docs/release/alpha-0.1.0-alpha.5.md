# Rclone UI 0.1.0-alpha.5 internal candidate

This unsigned Windows x64 portable candidate completes the automated Mount usability milestone. It is not a qualified public release and does not replace the Windows/WinFsp human qualification gate.

- Artifact: `artifacts/release/RcloneUI-0.1.0-alpha.5-win-x64.zip`
- SHA-256: `A17DAB395E6F91923AB68700456CD861234B01122AA08CE37A32140386C3494D`
- Entry point: root-level `Rclone UI.exe`

Mount Profiles now persist truthful lifecycle state across Host restart. An interrupted Mount is offered only for explicit remount; an unexplained existing namespace requires manual recovery and is never removed automatically. Mount details show the resolved target, uptime and component versions. The only executable cache preset remains **Read-only browsing**. Standard read/write and maximum compatibility are visible but unavailable until VFS queue, clean-drain, cache preservation and recovery evidence is implemented.

Automated verification passed: 3 architecture tests, 25 contract tests and 203 integration tests; one existing hardware-bound Argon2 test was skipped; build warnings were zero and the dependency audit was clean. The focused Mount failure suite passed 62 tests.

Fresh-extract smoke passed from an isolated directory: every release-manifest file hash was verified, the root launcher returned success, Desktop/Host/rclone started, the endpoint was created only beneath the supplied explicit Data Root, no default package `data/` directory was created, and the bundled rclone `v1.75.0` plus libargon2 `20190702` manifests were present. Real Windows 10/11 and WinFsp/Explorer Mount qualification remains Issue #58.
