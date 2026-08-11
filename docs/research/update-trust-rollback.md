# Managed-component update trust and rollback model

Status: research recommendation for GitHub issue #4  
Sources reviewed: official documentation and first-party release material, current as of 2026-08-11.

## Decision summary

Treat the portable application, bundled `rclone.exe`, and system-installed WinFsp as three separately trusted and separately committed components. Never make “Update all” one cross-component transaction. It is an ordered orchestration of independent transactions, each of which must reach a verified, healthy state before the next starts:

1. update the portable application;
2. update the bundled rclone;
3. offer the WinFsp MSI update last, with an explicit UAC prompt.

The application and rclone are user-writable portable files and can use staged, same-volume replacement plus retained rollback slots. WinFsp is a privileged Windows Installer product and must be installed or upgraded only by its official MSI; the GUI must not copy drivers, edit its service/registry state, or attempt its own driver rollback.

The invariant is: **downloaded bytes are never executable until their identity and authenticity have both been established, and the previous known-good application/rclone remains recoverable until the new version passes a post-start health check.**

## Trust policy by component

| Component | Discovery/download authority | Required verification | Commit authority | Rollback owner |
|---|---|---|---|---|
| Portable app | GitHub Releases API for the fixed repository `RingoCaviar/Rclone_UI`; exact release asset endpoint | SHA-256 equals the release asset API `digest`; production releases must be immutable and have a GitHub artifact attestation bound to this repository/workflow | Unelevated updater helper in the portable directory | App updater helper |
| rclone | `https://downloads.rclone.org/` stable-version metadata and versioned Windows amd64 archive | SHA-256 archive digest from `SHA256SUMS`, and the embedded OpenPGP signature on that checksum file against a pinned rclone release key fingerprint | Unelevated updater helper in the portable directory | App updater helper |
| WinFsp | Stable, non-prerelease release from the fixed first-party repository `winfsp/winfsp` | Published SHA-256 build hash **and** successful Windows Authenticode verification of the MSI; require the expected WinFsp publisher identity | Official MSI through Windows Installer after user-approved UAC elevation | Windows Installer / WinFsp MSI |

GitHub's release-asset response exposes a `digest` such as `sha256:...`, and binary downloads can be a `200` response or a `302` redirect, so the downloader must handle both while hashing the final bytes ([GitHub release-assets API](https://docs.github.com/en/rest/releases/assets)). GitHub immutable releases lock the tag and assets and automatically produce a cryptographically verifiable release attestation containing the tag, commit, and assets ([GitHub immutable releases](https://docs.github.com/en/enterprise-cloud@latest/code-security/concepts/supply-chain-security/immutable-releases)). Artifact attestations bind artifacts to build provenance such as repository, workflow, commit SHA, and event, but GitHub explicitly notes that provenance does not prove the artifact is safe; verification and a local acceptance policy are still required ([GitHub artifact attestations](https://docs.github.com/en/actions/concepts/security/artifact-attestations)).

rclone publishes signed `SHA256SUMS` beside each release and documents verifying its OpenPGP signature before comparing the archive hash. Its built-in `selfupdate` verifies only `SHA256SUMS`; the GUI should implement the stronger documented signature-plus-hash flow because it is a long-lived update agent ([rclone release signing](https://rclone.org/release_signing/)). Pin the full fingerprint documented by rclone in application code/configuration, allow an explicit signed key-rotation policy in a future app release, and reject an unknown replacement key rather than fetching trust material opportunistically from the same channel as the archive.

WinFsp's first-party releases publish SHA-256 build hashes for each MSI, identify prereleases, and document that 2.x installers upgrade earlier 2.x releases, while upgrading from legacy 1.x can require uninstall, reboot, and reinstall ([WinFsp releases](https://github.com/winfsp/winfsp/releases)). Authenticode uses a signature and certificate trust policy to establish publisher origin and whether signed code was modified; `WinVerifyTrust` with `WINTRUST_ACTION_GENERIC_VERIFY_V2` is the Windows API for this check, and **only zero is success** ([WinVerifyTrust](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrustex)). A hash alone protects against accidental corruption but, when sourced from the same page/channel as the MSI, is not an independent publisher-authentication mechanism; therefore require both checks.

## Release and anti-downgrade policy

- Default channel is stable only. Reject drafts and prereleases for all components unless the user explicitly opts into a separately labelled preview channel.
- Compare parsed semantic/component versions, not filenames or lexicographic strings. Never auto-install a version lower than the installed known-good version. A manual downgrade requires a dedicated advanced action and the same verification policy.
- Resolve only allowlisted HTTPS origins and fixed owners/repositories. Reject cross-origin redirects except the documented GitHub asset delivery hosts reached from the API redirect; never send credentials to redirect targets.
- Put explicit network timeouts, maximum asset sizes, archive entry-count/size limits, and cancellation on every transfer. Download to a newly created staging directory under the portable application's volume, not `%TEMP%`, so final renames do not cross volumes.
- Refuse archive paths that are absolute, contain `..`, target reparse points, or escape the staging root. For rclone, allowlist the expected archive layout and extract only the required executable plus first-party notices/licenses.
- Persist a small update journal after every state transition: component, old/new version, source URL/asset ID, expected and observed digest, staging path, rollback path, phase, retry count, and timestamp. Write a new journal file and replace the old one; do not rely on memory across process or power failure.
- Do not silently fall back to an older release when latest-version verification fails. Keep the current version and show a security failure. This prevents a compromised or inconsistent feed from inducing downgrade.

## Application and rclone transaction

### Stage (current process remains running)

1. Acquire a single-instance update lock. Reject an update while another update/repair is active.
2. Discover the candidate from its fixed authority and freeze immutable identifiers (version/tag and GitHub asset ID, or rclone versioned URL). Do not repeat “latest” resolution midway through the transaction.
3. Stream into `updates/staging/<transaction-id>/`, enforcing limits and computing SHA-256. Flush and close every file before verification.
4. Verify according to the component policy above. Record verification evidence in the journal. A verification failure deletes/quarantines staging, never changes active files, and is not user-overridable.
5. Extract into a second staging subtree, validate paths and expected files, then launch the staged binary in a non-destructive probe mode. At minimum verify it starts, reports the expected version/architecture, and exits successfully. For rclone use its version command without loading or rewriting user configuration.
6. Stop new jobs. For rclone, allow existing transfers/mounts to finish or require explicit user consent to stop them; never replace an executable still in use. Persist application state, then start the updater helper with a random one-time transaction token and exit.

### Commit (minimal updater helper)

The helper must be a small, separately signed/versioned executable already shipped with the current application. It receives only a validated transaction ID/token, opens the journal from the known updates directory, waits for the parent PID to exit, and independently repeats digest/path checks before touching active files.

For each active file, call Windows `ReplaceFileW(active, staged, backup, ...)` on the same volume, producing a backup of the original. Windows documents `ReplaceFile` as replacing one file with another with an optional backup while preserving attributes such as ACLs; files must be closed before move/replacement ([Microsoft, Moving and Replacing Files](https://learn.microsoft.com/en-us/windows/win32/fileio/moving-and-replacing-files)). Do not claim multi-file atomicity: each file replacement is atomic-sized work, but an application bundle is recovered through the journal.

Commit a bundle in this order:

1. data/runtime files that are not the entry point;
2. the main executable last;
3. write the committed manifest/version marker only after all replacements succeed;
4. launch the new main executable with `--update-health-check <transaction-token>`.

The new process validates its manifest, loads essential libraries/resources, opens the settings database read-only or performs a reversible schema readiness check, verifies bundled rclone can report its version, and signals success through a transaction-specific named event/file. It must not run ordinary jobs during this check. The helper waits with a finite timeout.

### Rollback and retention

On any replacement error, launch failure, health-check failure, or timeout, the helper reverses every completed replacement in reverse order using the retained backups, restores the previous manifest marker, and launches the previous application with a recovery notice. If rollback itself partially fails, leave both version trees/backups untouched, write `recovery-required`, and show/manual-launch a recovery helper on next start; never repeatedly alternate versions.

Keep the immediately previous **known-good** app and rclone until the newer version has completed its health check and at least one normal clean startup. Retain at most two known-good rollback generations subject to a configurable disk cap. Never include mutable user data (`rclone.conf`, settings, task definitions, logs) in binary rollback slots. Database/config migrations must be backward compatible or use their own pre-migration backup and reversible migration; otherwise the release is not eligible for automatic update.

The updater should be able to repair three startup observations deterministically:

- `staged/verified` but not committed: discard or resume staging; active version is authoritative.
- `committing` with journal entries: inspect actual file digests, finish only if all remaining inputs still verify; otherwise roll back completed entries.
- `awaiting-health`: try the new binary once; after failure/timeout roll back. Cap automatic attempts to prevent a boot loop.

## WinFsp privileged boundary

All discovery, download, SHA-256 hashing, and Authenticode/publisher verification happen in the unelevated GUI. Only after verification does the GUI present the exact current/candidate versions, publisher, source, impact on active mounts, and likely reboot requirement. If the user accepts, close/unmount affected rclone mounts cleanly, then use Shell execution with the `runas` verb to start the official MSI/`msiexec`; Windows documents that `runas` triggers UAC consent or administrator credentials ([Microsoft, launching applications](https://learn.microsoft.com/en-us/windows/win32/shell/launch)). The main GUI must remain unelevated.

Pass the already verified absolute MSI path, request normal/reduced visible installer UI, capture a verbose MSI log in the diagnostics directory, and avoid silent elevation. Do not embed arbitrary commands, URLs, or user-controlled arguments in the elevated launch. Re-open and re-hash the file immediately before elevation to close the verification/use race as far as practical.

Windows Installer generates rollback data and restores original state when installation fails by default ([Microsoft, rollback installation](https://learn.microsoft.com/en-us/windows/win32/msi/rollback-installation)). Let MSI own that transaction. Never disable MSI rollback, delete the Windows Installer cache, or emulate rollback by copying driver files. `msiexec` documents that files in use may require a reboot and supports explicit restart policy and verbose logging ([Microsoft, msiexec](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/msiexec)). Use a no-forced-restart policy: report “restart required” and let the user choose when to reboot.

Special case: if an installed WinFsp 1.x is detected, do not present the operation as an ordinary one-click upgrade. The first-party release notes say migration to 2.x may require uninstall, reboot, then install. Present a guided flow with a recovery checkpoint and no promise of automatic rollback across the uninstall/reboot boundary. A UAC cancellation is a normal cancellation: leave WinFsp and all other components unchanged and keep mount features in their previous availability state.

After MSI returns, do not trust exit code alone. Re-detect the installed product/version, verify required service/driver presence using supported system queries, and perform a benign mount-capability probe only when safe. If installation failed, surface the MSI log and re-detected state. If Windows Installer rolled back, retain the previous state. If state is indeterminate or reboot is pending, disable new mounts, preserve existing user configuration, and offer retry after reboot/diagnostic export rather than repeatedly invoking elevation.

## “Update all” orchestration and user experience

- Preflight disk space, write permission to the portable directory, active jobs/mounts, network availability, and whether a restart is pending.
- Show independent rows and states: `checking`, `downloading`, `verifying`, `ready`, `waiting for jobs`, `installing`, `restart required`, `rolled back`, `failed securely`.
- Updating one component must not erase another component's successful result. If the app update succeeds and rclone fails verification, retain the new app and old rclone and report that exact combination.
- Never begin WinFsp elevation while an app/rclone transaction is unresolved. Never self-update the GUI while an MSI/UAC prompt is active.
- Allow cancellation during discovery/download/staging. Once replacement begins, cancellation becomes “finish or roll back.” Once MSI is elevated, cancellation belongs to Windows Installer.
- Offline or TLS failure means “no update information”; it must not be interpreted as “up to date.” Keep cached metadata for display only, never as authority for installing bytes not already fully verified.

## Failure matrix

| Failure | Required result |
|---|---|
| Network interruption / HTTP error | Remove partial file or retain it only as non-executable resumable data; active version unchanged |
| Hash, signature, attestation, publisher, or version mismatch | Security failure; quarantine/delete candidate; no override; active version unchanged |
| Disk full during staging | Clean incomplete staging; active version unchanged |
| Process/file lock at commit | Abort before replacement where possible; otherwise journal and reverse completed replacements |
| Power loss during commit | On next start helper reconciles journal and file digests, then finishes or rolls back deterministically |
| New app crashes or cannot load | Health timeout, automatic rollback, preserve diagnostics and failed candidate metadata |
| New rclone cannot report expected version | Roll back only rclone; app remains usable |
| UAC refused | Mark WinFsp update cancelled; no repeated prompts; mounting remains at prior capability |
| MSI fails and rolls back | Re-detect previous WinFsp, keep it; expose MSI log |
| MSI requests reboot | Record pending state, prohibit new mount operations until reboot/re-detection; never force restart |
| WinFsp state indeterminate | Disable new mounts, retain configs, provide diagnostic export and guided repair |

## Release-pipeline requirements

For application releases, build in GitHub Actions from a protected tag, produce deterministic asset names and a machine-readable manifest, calculate SHA-256, code-sign Windows executables, generate a GitHub artifact attestation for the exact downloadable archive, upload all assets to a draft, and publish only when complete. Enable GitHub immutable releases so published tags/assets cannot be mutated. The updater acceptance policy binds the attestation to `RingoCaviar/Rclone_UI` and the designated release workflow/environment, not merely to “some GitHub Actions build.”

Ship the updater's pinned rclone signing-key fingerprint and expected WinFsp publisher policy as reviewed code, not remotely mutable settings. Changes to either trust root require an application release signed/proven under the existing application trust chain, with a clearly documented transition. Log verification outcomes and certificate/signing metadata, but never secrets, tokens, rclone configuration contents, or credentials.

## Acceptance criteria for implementation

1. A tampered app, rclone archive, checksum file, or WinFsp MSI is rejected before execution/elevation.
2. App/rclone updates survive forced termination at every journal state and converge to either the old or new complete known-good version.
3. A failed new-version health check automatically restores the prior app/rclone and does not alter user configuration.
4. The normal GUI never runs elevated; only the already verified official WinFsp MSI is launched through a visible UAC boundary.
5. UAC denial, MSI failure, and reboot-required outcomes are distinguishable and do not disable non-mount rclone features.
6. WinFsp 1.x-to-2.x migration is presented as a guided exceptional flow, not an atomic upgrade.
7. Update logs provide component/version, phase, source identifier, hashes, signature/provenance result, MSI exit/reboot status, and recovery action without exposing credentials.

## Open implementation decisions

- Select the concrete app packaging layout (versioned directories plus a stable launcher, or per-file `ReplaceFileW`). Versioned directories simplify bundle consistency and should be preferred if framework/runtime constraints allow it.
- Define the application code-signing publisher certificate and renewal/rotation policy. GitHub provenance protects the download pipeline, while Authenticode improves Windows publisher identity and SmartScreen experience; both should be release requirements.
- Confirm the exact stable WinFsp publisher certificate subject/chain from a current verified stable MSI during implementation and encode an identity policy that tolerates legitimate certificate renewal without accepting an unrelated publisher.
- Decide whether attestation verification is implemented in-process or by a small vendored verifier. Do not require end users to install `gh`; GitHub documents that attestations can be downloaded and verified offline with trusted-root material, which is useful for test vectors and verifier design ([GitHub offline attestation verification](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/verify-attestations-offline)).
