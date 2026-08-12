# Rclone UI 0.1.0-alpha.10 internal candidate

This Windows x64 portable candidate makes Background Host command outcomes visible in the current page instead of leaving a raw internal result code at the bottom of a form.

- Artifact: `artifacts/release/RcloneUI-0.1.0-alpha.10-win-x64.zip`
- SHA-256: `758B9814DCEBACEA81C8EC612F63967E8C0FB5D723789E47FD220C614F902170`

Remote setup now gives a clear Chinese/English message for successful validation, incomplete fields, SFTP host-key requirements, connection-test failures, locked Vaults, unavailable rclone, unavailable Mount prerequisites, and an unavailable Background Host. Partly filled FTP/FTPS/SFTP fields no longer fall through to the OAuth setup path when the server address is missing.

Automated build, formatting, vulnerability audit, architecture tests, contract tests, and integration tests passed. This unsigned internal candidate still requires the documented human Windows/WinFsp qualification before any public release.
