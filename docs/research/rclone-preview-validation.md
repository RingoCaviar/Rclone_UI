# rclone RC preview validation

Issue: [#24](https://github.com/RingoCaviar/Rclone_UI/issues/24)

## Decision

The application cannot obtain a complete deterministic Copy or Mirror dry-run change set from `sync/copy` or `sync/sync` RC responses. The async job result contains completion and error state, but no authoritative per-path change collection. Command-specific logger parameters such as `combined` are not exposed by these RC calls as a usable per-job artifact.

The Background Host must therefore fail closed unless it can reconcile three independent evidence classes: a bounded preview artifact, complete before/after listings, and structured `operations/check` results. Logger output alone is never authoritative.

## Reproducible matrix

The Windows harness is `scripts/validate-rclone-preview.ps1`; `.github/workflows/rclone-preview-validation.yml` downloads official archives, verifies their published SHA-256 digest, and runs this matrix:

| Dimension | Values |
|---|---|
| rclone | 1.74.4 baseline, 1.75.0 current |
| operation | `sync/copy`, `sync/sync` |
| backend class | local, crypt wrapper, same-remote memory |

All 12 cells passed in GitHub Actions run [31478202381](https://github.com/RingoCaviar/Rclone_UI/actions/runs/31478202381).

The fixtures cover local hash availability, crypt without plaintext hashes, Windows case-differing names, a same-remote server-side-operation candidate, target immutability during dry-run, and an invalid-remote error that must finish unsuccessfully with an explicit error.

## Observed contract

- `job/status` does not return combined/differ/missing/match/dest-after change arrays.
- Passing `combined` to the RC sync call does not create the command logger file.
- `operations/check` returns structured difference fields.
- `operations/list` can return hashes where the backend supports them; crypt does not expose them.
- Independent before/after listings prove the accepted dry-run did not mutate the target.
- Invalid backend configuration produces a failed async job with a non-empty error and is treated as blocked evidence.

## Product consequence

`RclonePreviewEvidencePolicy` remains the safety authority. Known incomplete modes—including hard cutoff, retries above one, compare/copy-dest, server-side directory moves, logger errors, incomplete listings, and missing check evidence—must produce stable blocked outcomes rather than an accepted preview.
