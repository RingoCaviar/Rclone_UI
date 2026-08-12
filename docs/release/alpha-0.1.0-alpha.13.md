# Rclone UI 0.1.0-alpha.13 internal candidate

This Windows x64 portable candidate loads staged Remote configuration into the running rclone process before testing it.

- Artifact: `artifacts/release/RcloneUI-0.1.0-alpha.13-win-x64.zip`
- SHA-256: `E7749D9E5EBF882EB9326D82DA63DEAF197DF2FF93583551D4BAAD6792FA3440`

The Background Host now uses rclone's authenticated `config/create` API before the first test of a new Remote, then retains the same configuration in its temporary file. A failed test removes the staged Remote from both rclone and the temporary file. This prevents the test from using a stale in-memory rclone configuration after the Desktop form writes an FTP, FTPS, SFTP, or OAuth Remote.

For explicit FTPS, rclone's documented per-Remote settings are `host`, `port`, `user`, `pass`, and `explicit_tls = true`; non-standard port `3587` remains a valid direct port value and must not be appended to the address field.

Automated build, formatting, vulnerability audit, architecture tests, contract tests, and integration tests passed. This unsigned internal candidate still requires the documented human Windows/WinFsp qualification before any public release.
