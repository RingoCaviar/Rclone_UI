# PROTOTYPE — Job Object containment

Throwaway Windows harness for Issue #20. It creates the prototype worker suspended, assigns it to a `KILL_ON_JOB_CLOSE` Job Object, resumes it, lets it start a harmless `PING.EXE` descendant, observes Job completion-port events, and closes the Job handle to verify both processes terminate.

Run:

```powershell
dotnet run --configuration Release --project prototypes/job-object-containment/JobObjectPrototype.csproj
```

This run uses the prototype executable rather than rclone and cannot cover every launcher/parent-Job environment. The real rclone and Windows 10/11 launcher matrix remains a release gate.
