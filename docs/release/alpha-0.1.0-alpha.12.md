# Rclone UI 0.1.0-alpha.12 internal candidate

This Windows x64 portable candidate preserves the password in the Desktop form when an FTP, FTPS, or SFTP connection test fails.

- Artifact: `artifacts/release/RcloneUI-0.1.0-alpha.12-win-x64.zip`
- SHA-256: `577228EDF039B6BB7F2A3D1D0F914532CCB039AA6A2719910CF92117B34D21F5`

The sent temporary password bytes are still cleared regardless of outcome. The visible form password is cleared only after a verified `remote-added` result, so a failed test can be corrected and retried without retyping the password.

Automated build, formatting, vulnerability audit, architecture tests, contract tests, and integration tests passed. This unsigned internal candidate still requires the documented human Windows/WinFsp qualification before any public release.
