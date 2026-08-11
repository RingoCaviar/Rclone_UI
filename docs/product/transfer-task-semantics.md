# Transfer Task execution and safety semantics

## Operation types

### Copy

- Copy source files that are absent or changed at the target.
- Do not delete source files or target-only files.
- When a target file is newer, preserve it and record a conflict by default.
- When the source is newer, or timestamps match but verified content differs, replace the target through the safety-copy policy.

### Move

- Copy and verify each selected source file before deleting that source file.
- Preserve failed, unverified, filtered, and cancelled source files.
- Apply the Copy conflict policy before deciding whether a source file may be removed.

### Mirror Sync

- Treat the source as authoritative and make the target match it in one direction.
- Copy or replace changed source content and remove target-only content according to preview and safety-copy rules.
- Never describe Mirror Sync as bidirectional synchronization or use a bidirectional arrow for it.

Keep the complete source and target visible throughout creation, preview, confirmation, execution, and history. Reversing direction requires editing the task and invalidates its previous preview.

## Preview contract

- Require a dry-run before the first Move or Mirror Sync execution and after every material configuration change.
- Require a new dry-run after enabling deletion, forced overwrite, `delete-excluded`, raw filters, hard cutoff, or another high-risk option.
- Let ordinary Copy run without a mandatory preview while always offering Preview Changes.
- Bind a preview to a canonical task-configuration version, source, target, managed-rclone version, and generation time.
- Do not enable a schedule unless its accepted preview still matches the task version.
- Detect invalid names, target encoding limits, case-only collisions, source/target overlap, and known path conflicts during preview. Block until resolved or an explicit supported mapping is selected.

## Conflict policy

- Preserve a newer target file during Copy and Move and record it as a conflict.
- Let Mirror Sync replace differing target content because the source is authoritative, but list every replacement in preview.
- Provide advanced policies: Source Always Wins, Skip Existing, and Stop on Conflict.
- Never silently collapse two source names that the target treats as identical.
- Continue other safe files after a conflict by default. Let schedules opt into Stop on First Conflict.
- Use the terminal result Completed with Conflicts whenever conflicts were skipped or preserved.

## Safety copies and deletion

- Before replacing or deleting target content, move it to a timestamped safety-copy location on the target Remote by default.
- Retain safety copies for seven days by default and expose their location, capacity implications, and cleanup status.
- When the backend cannot perform a reliable move, offer: Cancel; Copy to Safety Location Then Delete; or Direct Delete after an elevated warning.
- Perform Mirror Sync deletion after transfers and verification by default.
- Do not perform target deletion after source-read, transfer, or verification errors.
- Keep Ignore Errors for Deletion and early deletion ordering in advanced settings with a high-risk warning and mandatory re-preview.
- Treat filtered files as untouched by default. `delete-excluded` is a separate high-risk option whose preview and confirmation list filter-caused deletion independently.

## Verification

- Automatically choose the strongest hash shared by the source and target when practical.
- Fall back to size and modification time when no common reliable hash exists and label the result Basic Verification.
- Offer High-Assurance Verification that performs a complete post-transfer check.
- Show verification strength in preview, progress, result, and history.
- Never delete a Move source file until its target file passes the selected verification policy.

## Filters

- Provide visual rules for path, filename, type, size, modification age, and common exclusions.
- Show example paths with Included or Excluded status and the rule responsible.
- Preserve rclone rule ordering when generating filters.
- Offer raw rclone filter rules in Advanced mode and validate them before execution.
- Switch from raw to visual mode only when the raw rules can be represented without changing behavior.

## Retries and connectivity

- Retry network timeouts, throttling, and identified transient provider/service failures with exponential backoff, for three high-level rounds by default.
- Show the reason, current round, and next retry time.
- Do not blindly retry authentication, permission, quota, invalid-name, configuration, or verification failures.
- Re-running an interrupted task skips completed files that still satisfy the selected equality and verification rules.
- Describe this as Retry Unfinished Content. Do not promise universal byte-level resume; the current file may restart depending on rclone and backend capability.

## Cancellation and stopping

- Use cooperative rclone cancellation and display Cancelling until the job reaches a terminal state.
- Stop admitting new file work and prevent not-yet-started deletion once cancellation begins.
- Do not roll back files already copied, moved, replaced, or deleted.
- Report Cancelled with Partial Results and provide completed, skipped, conflicted, failed, and possibly affected paths.
- Allow queued tasks to pause.
- Do not present a running-task Pause button unless the managed rclone version and operation provide a verified pause contract.
- Show Finish Current File Then Stop only when it can be implemented reliably; otherwise show Cancel.

## Limits

- Default to unlimited bandwidth and no transfer or duration cap.
- Support global bandwidth limits, scheduled bandwidth windows, and per-task limits. The strictest applicable limit wins.
- Include Mount traffic under the global bandwidth policy.
- When a transfer or duration cap is reached, finish the current file by default and stop before starting another.
- Mark the result Stopped by Limit with unfinished content.
- Keep hard cutoff as an advanced option and warn that the current file may need full retransmission.

## Confirmation

For a dangerous execution, show:

- full source, target, and direction;
- Copy, Move, or Mirror Sync meaning;
- counts and sizes for copy, replace, delete, skip, conflict, and filter-caused delete;
- safety-copy location and retention;
- verification strength;
- applied filters, bandwidth, and cutoff limits;
- preview time and the task configuration version it represents.

When deletion is possible, require the user to type the target Remote name. Do not offer permanent suppression of destructive confirmation. Invalidate confirmation when the task configuration changes or the preview expires under the future staleness policy.

## Terminal results

Use mutually exclusive terminal results:

- Succeeded
- Completed with Conflicts
- Failed
- Cancelled with Partial Results
- Interrupted by System or Crash
- Not Executed

Derive the business result from rclone outcome, conflict and verification records, cancellation state, and lifecycle journal. A zero process or job exit code alone is insufficient.
