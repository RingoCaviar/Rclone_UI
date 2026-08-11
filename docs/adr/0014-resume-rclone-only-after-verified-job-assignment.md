---
status: accepted
---

# Resume rclone only after verified Job assignment

Create the verified bundled rclone process suspended, assign it to the Host incarnation's unnamed `KILL_ON_JOB_CLOSE` Job Object, verify membership, and only then resume its primary thread. Associate the Job with an I/O completion port before launch, disable handle inheritance except an explicit allow-list, and never enable breakaway or silently retry with an uncontained process.

The prototype demonstrated suspended side-effect prevention, compatible nested-Job assignment, descendant inheritance, completion-port process notifications, and complete tree termination when the final Job handle closed. Release qualification remains conditional on the real rclone and Windows 10/11 launcher/parent-Job matrix.

Any failure before resume terminates the suspended child and returns a typed containment diagnostic. Graceful Host shutdown still asks rclone to stop and journals its result; `KILL_ON_JOB_CLOSE` is the final crash and forced-exit boundary, not a substitute for orderly cancellation.
