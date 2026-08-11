---
status: accepted
---

# Use authenticated named pipes and Job-contained rclone

Connect the Avalonia UI to the per-user Background Host through a local duplex Windows named pipe restricted to the current logon SID, with explicit length-prefixed JSON framing, protocol/capability negotiation, HMAC challenge-response, sequence protection, and state revisions. Derive discovery from the authenticated Data Root identity, make the Host the sole lifecycle and persistence authority, and create rclone suspended so it can be assigned to a `KILL_ON_JOB_CLOSE` Job Object before execution; this Windows-specific design rejects the extra listener and browser-reachability surface of loopback HTTP while making UI crashes harmless, Host crashes contain descendants, and split-version updates fail closed through an authenticated handoff and health check.
