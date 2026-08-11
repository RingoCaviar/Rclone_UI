# Third-party notices

The portable distribution includes notices and license texts supplied with every bundled component. Release packaging must fail when a required notice is absent.

- Avalonia UI — MIT License; see the upstream Avalonia repository and the NuGet package license metadata.
- .NET Runtime — MIT License and third-party notices distributed by Microsoft.
- rclone — MIT License. The packaged rclone archive must retain its upstream `COPYING` file.
- WinFsp — GPLv3 with a special exception. WinFsp is installed through its official MSI and is not repackaged into the portable application archive.
- Microsoft.Data.Sqlite / SQLitePCLRaw — MIT and SQLite public-domain notices from their packages.
- Konscious.Security.Cryptography.Argon2 — MIT License. Native libargon2 redistribution must include its CC0/Apache-2.0 notice as applicable to the selected build.

Exact versions and hashes are recorded in the generated release manifest. This summary is not a substitute for the license files shipped with each artifact.
