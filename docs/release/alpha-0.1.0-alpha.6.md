# Rclone UI 0.1.0-alpha.6 internal candidate

Unsigned Windows x64 portable candidate for real Windows Mount qualification.

- Artifact: `artifacts/release/RcloneUI-0.1.0-alpha.6-win-x64.zip`
- SHA-256: `A338AC3DC1A43602A722480047CC3BBB04217D6BF89EB5EF7B7762CB1294AD0C`

This candidate adds the user-approved stable WinFsp 2.1.25156 install/repair path: official MSI only, pinned SHA-256, Windows signature/publisher verification, explicit UAC and automatic re-detection. It preserves the visible warning that later public security fixes exist.

Fresh extraction passed release-manifest hash verification, root-launcher handoff and explicit Data Root endpoint creation. Driver installation was not run as part of smoke testing. Real Windows/WinFsp qualification remains required.
