# Prototype result

Environment: Windows NT 10.0.26200.0, .NET 8.0.23. The prototype Host process was already inside an external Job.

- The worker was created suspended and produced no marker side effect before resume.
- Assignment to the nested `KILL_ON_JOB_CLOSE` Job succeeded before resume.
- The resumed worker launched `PING.EXE`; the descendant automatically inherited the Job.
- The associated I/O completion port delivered `JOB_OBJECT_MSG_NEW_PROCESS` (`6`) for both PIDs with the configured completion key.
- Closing the only prototype Job handle terminated both worker and descendant within five seconds.

The run proves one compatible external-parent-Job environment, not all launchers. It did not use the real rclone binary or test Windows 10, .NET 10, Explorer, Windows Terminal, debugger, CI runner, updater, or an intentionally incompatible parent Job.
