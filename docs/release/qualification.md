# Release qualification

Automated CI may produce a candidate, but it cannot mark the following `ready-for-human` gates passed. A named human records date, machine/VM image, artifact SHA-256, evidence link, and result for each supported release candidate.

## Human-only Windows matrix

| Gate | Windows 10 22H2 x64 | Windows 11 supported release |
|---|---|---|
| Portable launch, high DPI 100/150/200%, light/dark theme | Required | Required |
| Keyboard-only navigation, focus visibility, Narrator names/status | Required | Required |
| Host reconnect, lock/unlock, tray/exit and notification behavior | Required | Required |
| Real rclone transfer/Mount and WinFsp install/UAC/reboot behavior | Required | Required |
| Update handoff, health timeout, rollback and retained pair | Required | Required |

The release owner must also complete Issues #32–#36 where destructive hardware, cross-session, shutdown, and paired-migration evidence is required. Automation must report these as `NOT_RUN_HUMAN_GATE`, never success.

## Automated candidate gates

- locked restore, formatting, warnings-as-errors build, all tests and architecture rules;
- NuGet vulnerability audit;
- deterministic `win-x64` self-contained portable layout, manifest and archive;
- diagnostic redaction tests and a secret scan of the artifact;
- signing hook invocation in release mode and signature verification after signing;
- update metadata binds version, artifact name, size, SHA-256 and minimum supported OS.

## Support policy

Supported v1 systems are Windows 10 22H2 x64 and supported Windows 11 x64 releases for the portable edition. Users should attach only the redaction-previewed diagnostic archive. Recovery Cache contents, Vault files, `rclone.conf`, credentials, access tokens, full local paths, and filenames are excluded by default. Security reports must not be filed in public Issues; the repository security contact/process must be used when enabled.
