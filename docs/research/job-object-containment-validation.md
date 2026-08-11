# Job Object containment validation

Validation date: 2026-08-11  
Issue: #20  
Prototype evidence: branch `prototype/job-object-containment`, commit `b5fc2f0`

## Verdict

The accepted suspended-create and Job-assignment sequence works in the tested Windows environment, including beneath the compatible external Job already containing the Codex process. A worker created with `CREATE_SUSPENDED` performed no application side effect before assignment and resume; its subsequently created descendant inherited the nested Job; the completion port reported both process creations; closing the only Job handle with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` terminated the complete tree.

Use this sequence for rclone, failing closed before resume whenever process creation, Job configuration, completion-port association, assignment, identity recording, or thread resume fails. The prototype used its own worker plus `PING.EXE`, not the bundled rclone binary, so the implementation remains release-gated on the real executable and launcher matrix.

## Observations

Environment: Windows NT `10.0.26200.0`, .NET 8.0.23.

- `IsProcessInJob(current, NULL)` reported that the prototype Host was already in an external Job.
- Creating and configuring a nested unnamed Job succeeded in that environment.
- The worker was created suspended; its marker did not exist before assignment/resume.
- `AssignProcessToJobObject` succeeded and `IsProcessInJob(worker, job)` confirmed membership before resume.
- After resume, the worker started a harmless descendant and `IsProcessInJob(descendant, job)` confirmed inherited membership.
- The associated completion port produced `JOB_OBJECT_MSG_NEW_PROCESS` for both PIDs and the expected completion key.
- Closing the Job handle terminated worker and descendant within the five-second observation window.

This establishes one compatible nested-Job case. It does not establish that every debugger, terminal, CI runner, updater, sandbox, or intentionally restrictive parent Job permits the required nested assignment.

## Production launch contract

1. Create one unnamed Job per Host incarnation; do not publish or inherit its handle.
2. Set `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. Do not enable breakaway or silent breakaway.
3. Associate an I/O completion port before starting rclone and begin draining it for lifecycle evidence.
4. Create only the verified bundled `rclone.exe` with `CREATE_SUSPENDED`, a fully resolved application path, explicit argument list, explicit working directory/environment, and handle inheritance disabled except a reviewed allow-list.
5. Record launch intent before `CreateProcess`; after creation, record PID plus process creation time while it is still suspended.
6. Assign to the Host Job and verify membership. Only then resume the primary thread and record `process-started`.
7. Any pre-resume failure terminates the suspended process, closes its handles, and returns `RcloneContainmentUnavailable`; never retry uncontained.
8. Treat completion-port notifications as prompt lifecycle signals while retained process handles and exit codes remain authoritative. Journal descendant creation/exit without assuming an unknown descendant is safe.
9. On graceful shutdown, stop admission, request rclone cancellation/unmount, wait to deadline, and explicitly terminate remaining work before closing the Job. `KILL_ON_JOB_CLOSE` is the final crash/forced-exit containment layer.
10. Keep the updater outside the Host Job so it can survive an authenticated cooperative handoff. Never pass it the Job handle.

## Fail-closed diagnostics

Expose stable categories without leaking full command lines or secrets:

- `ProcessCreateFailed`
- `JobCreateFailed`
- `JobPolicyConfigurationFailed`
- `CompletionPortAssociationFailed`
- `HostEnvironmentJobConflict`
- `AssignToJobFailed`
- `MembershipVerificationFailed`
- `ResumeFailed`
- `UnexpectedDescendantObserved`
- `ContainmentTerminationTimedOut`

Diagnostics record Windows error code, Host external-Job observation, launcher category, component versions/digests, PID/start time when created, and sanitized phase. They never fall back to an ordinary `Process.Start` execution.

## Release-gated matrix

- verified bundled rclone starting `rcd`, plus any documented child-process behavior;
- Explorer, Windows Terminal, debugger, CI runner, updater, compatible external Job, and deliberately incompatible Job restrictions;
- Windows 10 and 11 with the selected .NET LTS runtime;
- Host graceful exit, abrupt termination, handle leak simulation, rclone crash, child/grandchild creation, and forced Job termination;
- completion-port new/exit/active-zero ordering and journal reconciliation;
- proof that no inheritable duplicate Job handle lets the tree survive Host death.

Each incompatible environment must fail before rclone resumes, with `HostEnvironmentJobConflict` or a narrower category. It must never silently weaken containment.

## Prototype disposition

The harness remains off `main` as primary evidence on [`prototype/job-object-containment`](https://github.com/RingoCaviar/Rclone_UI/tree/prototype/job-object-containment/prototypes/job-object-containment) at commit `b5fc2f0`.
