# Rclone UI 0.1.0-alpha.9 internal candidate

This Windows x64 portable candidate adds connection setup for FTP, explicit/implicit FTPS, and SFTP before the existing read-only Mount workflow.

- Artifact: `artifacts/release/RcloneUI-0.1.0-alpha.9-win-x64.zip`
- SHA-256: `D723ACFBB1782B7619713872203C14381D942AFC60D766ABDD38D9A876F463C8`

In **Remotes → Advanced options**, enter a display name and select FTP, FTPS Explicit TLS, FTPS Implicit TLS, or SFTP. Credentials are sent only to the Host, stored in the encrypted Vault after a successful connection test, and cleared from the Desktop form. FTP/FTPS passwords are obscured using the managed rclone `core/obscure` endpoint before the temporary rclone configuration is written. FTPS validates certificates by default; bypassing certificate validation is an explicit insecure option. SFTP requires a host-key fingerprint.

Fresh extraction passed archive-manifest hash verification and root-launcher startup using an explicit Data Root. This candidate is ready for isolated FTP/FTPS/SFTP connection and read-only Mount qualification.
