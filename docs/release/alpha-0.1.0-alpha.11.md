# Rclone UI 0.1.0-alpha.11 internal candidate

This Windows x64 portable candidate separates server-based Remote setup from cloud OAuth setup and shows only fields relevant to the selected protocol.

- Artifact: `artifacts/release/RcloneUI-0.1.0-alpha.11-win-x64.zip`
- SHA-256: `E484265B2BCDD79F58754AD47C6BDD16AED63063CDD2D29304D3E3E7787A58D7`

For FTP/FTPS/SFTP, choose **服务器连接**. FTPS requires only a Remote name, server address, port, username, and password; certificate skipping remains optional and hidden unless FTPS is selected. The SFTP host-key fingerprint is visible and required only for SFTP. For Google Drive, OneDrive, or Dropbox, choose **云端 OAuth 令牌** instead.

The Remote error notification now identifies the required fields for the currently selected setup method. Automated build, formatting, vulnerability audit, architecture tests, contract tests, and integration tests passed. This unsigned internal candidate still requires the documented human Windows/WinFsp qualification before any public release.
