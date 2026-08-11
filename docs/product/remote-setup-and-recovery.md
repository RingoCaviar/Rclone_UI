# Remote setup and recovery

Rclone UI uses the **Provider Wizard** selected as Variant A in the Remote setup prototype.

## Entry points

- Open the wizard from Home > Add Remote and Remotes > Add Remote.
- Keep Remotes as a dedicated sidebar destination for connection health, repair, editing, import/export, and deletion.
- Use ordinary-language provider names and icons before exposing rclone backend identifiers.

## Add Remote

Use three visible steps and keep a persistent summary beside them:

1. **Choose Provider** — show searchable provider choices, recommended providers first, and Other for the complete runtime-discovered rclone provider list.
2. **Sign In and Configure** — collect a unique display name, run provider authorization or required credentials, apply recommended defaults, and keep advanced rclone options collapsed.
3. **Test and Save** — verify that encrypted credentials can be used, connect to the Remote, list its root, discover capabilities, and show a precise failure location before saving.

Back and forward navigation must preserve in-memory answers. Do not persist credentials until the user successfully saves the Remote.

## Provider presentation

Give these v1 provider families bespoke names, icons, explanations, recommended defaults, and authorization guidance:

- OneDrive
- Google Drive
- Dropbox
- S3 and S3-compatible storage
- SFTP
- WebDAV

Show local filesystem locations as a source or destination choice rather than presenting them as a cloud account.

Support every other provider reported by the managed rclone binary through a generic form driven by runtime provider and option metadata. Generic support must preserve required, advanced, sensitive, exclusive, default, example, and non-interactive continuation semantics rather than hard-coding a reduced option set.

## Authorization

- Prefer authorization in the provider's official browser page and explain that Rclone UI does not receive the account password.
- Support rclone's alternative/headless authorization continuation when a browser cannot be opened on the same computer.
- Never show tokens or sensitive option values after submission; show only presence, validity, and last-tested status.
- Keep a cancelled or failed authorization entirely in memory unless the user explicitly saves a draft without credentials.

## Repair

- Treat expired or rejected authorization as a repair of the existing Remote, not creation of a replacement.
- Preserve the Remote identity, display name, Transfer Task references, schedules, and Mount definitions.
- Execute repair as: reauthorize or replace credentials, test connection and capabilities, then offer to resume paused schedules.
- If repair fails, retain the previous encrypted configuration and show diagnostics without reporting the Remote as healthy.

## Edit and test

- Separate ordinary settings from Advanced rclone Options.
- Re-run connection and capability tests after a material configuration change.
- Apply edits through a verified temporary configuration and switch only after success, retaining a recoverable encrypted snapshot according to the portable-state specification.
- Let users run a non-mutating Test Connection at any time.

## Import and export

- Import an existing `rclone.conf` only after previewing Remote names, backend types, encryption status, conflicts, and unsupported or duplicate names.
- Never overwrite a conflicting Remote silently; require rename, replace with impact review, or skip.
- Export through the complete encrypted Data Root backup flow by default.
- Offer advanced export of an encrypted rclone configuration with an explicit explanation that application tasks, schedules, history, and display metadata are not included.

## Delete Remote

- Before deletion, show every Transfer Task, schedule, and Mount that references the Remote.
- Require active Mounts to be unmounted and running tasks to finish or be cancelled through their normal lifecycle.
- Offer Cancel, Delete Remote and Disable Dependents, or Delete Remote and Delete Selected Dependents; never cascade silently.
- Create an encrypted snapshot before applying deletion.

## Error presentation

- Identify the failed stage: authorization, credential decryption, network connection, root listing, or capability discovery.
- Lead with a plain-language recovery action and keep raw rclone details in an expandable diagnostic section.
- Distinguish authentication failure, permission denial, missing network, provider throttling, invalid options, and unsupported capability when rclone exposes enough evidence.

## Primary source

The compared variants are preserved on the `prototype/remote-setup-journey` branch under `prototypes/remote-setup-journey/`.
