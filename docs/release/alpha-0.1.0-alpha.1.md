# Rclone UI 0.1.0-alpha.1 internal candidate

This is an unsigned internal Windows x64 portable candidate. It is not a qualified public release.

## Artifact

- File: `artifacts/release/RcloneUI-0.1.0-alpha.1-win-x64.zip`
- SHA-256: `829543826C48D688AEB97F0AF188699DB6D0056E3FFB7E1866CF180266FD7889`
- Bundled rclone: v1.75.0
- Bundled libargon2: reviewed 20190702 x64 candidate
- Minimum declared OS: Windows 10 22H2 x64

## Fresh-extract smoke result

The candidate was extracted to a new unique directory and launched with an explicit Data Root. Desktop remained alive, the Background Host published its endpoint and remained alive, no default `desktop/data` directory was created, both component manifests were present, and every file listed by the release manifest matched its SHA-256 digest.

The first smoke run exposed an initialization-order crash: XAML selected the initial navigation item before `DesktopShellState` had been assigned. The state is now created before `InitializeComponent`; the same packaged launch loop passed afterward.

## Known gates

- The ZIP and executables are not Authenticode-signed.
- WinFsp is detected/managed separately and is not embedded in this ZIP.
- Windows 10/11, accessibility, elevation, suspend/crash, removable-media, updater rollback and destructive recovery matrices remain human qualification gates.
- This artifact should be rebuilt from the tagged commit before any GitHub prerelease is published.
