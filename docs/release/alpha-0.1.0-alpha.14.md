# Rclone UI 0.1.0-alpha.14 internal candidate

This Windows x64 portable candidate repairs the WinFsp installation flow.

- Artifact: `artifacts/release/RcloneUI-0.1.0-alpha.14-win-x64.zip`
- SHA-256: `317DA31B8ED2C729FFA9897D2FC29C8DFE69EB1D5407780C21BD03B76BCF0094`

Clicking **Install/repair WinFsp** now immediately reports download/UAC progress, then presents clear success, restart-required, UAC-cancelled, download, hash, signature, and installer-exit results. The installer verifies the MSI with Windows Authenticode `WinVerifyTrust` after its pinned SHA-256 check; it no longer attempts to load the MSI itself as a certificate, which prevented the elevated installer from starting.

Automated build, formatting, vulnerability audit, architecture tests, contract tests, and integration tests passed. This unsigned internal candidate still requires the documented human Windows/WinFsp qualification before any public release.
