# Prototype result

Environment: Windows NT 10.0.26200.0, session 1, .NET 8.0.23.

- Extracted the token logon SID (`S-1-5-5-*`) independently from the account SID.
- Created a protected, non-inheriting pipe DACL with `DENY NETWORK` and `ALLOW <logon SID>` only.
- A competing server requesting `PipeOptions.FirstPipeInstance` was rejected.
- 100 concurrent local clients connected and round-tripped one byte.
- Server-side `RunAsClient` observations matched both the expected account SID and logon SID for all clients.

The current run did not have authority/environment to test another Windows account, Fast User Switching, `runas`, elevated versus unelevated clients, remote SMB pipe access, Windows 10, .NET 10, or runtime-secret exposure on FAT/exFAT. Those cases remain a release-gated interactive matrix.
