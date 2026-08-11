# Named-pipe ACL and identity validation

Validation date: 2026-08-11  
Issue: #19  
Prototype evidence: branch `prototype/named-pipe-acl`, commit `ffbecc5`

## Verdict

The managed Windows API surface is suitable for the Host pipe security boundary. Use `NamedPipeServerStreamAcl.Create` with a protected, non-inheriting DACL that explicitly denies `S-1-5-2` (`NETWORK`) and allows the current token's logon SID (`S-1-5-5-X-Y`). Do not grant the broader account SID, `Authenticated Users`, `Users`, or `Everyone`. Request `PipeOptions.FirstPipeInstance` for the first server instance and inspect every connected client under `RunAsClient` before accepting its application handshake.

The prototype validates these primitives on the available Windows/.NET 8 environment, including 100 concurrent same-session clients. It does not complete the required cross-account, Fast User Switching, elevation, remote, Windows 10, or .NET 10 matrix. Those cases remain a release gate; until they pass, the design is accepted but the IPC implementation is not release-qualified.

## Observations

On Windows NT `10.0.26200.0`, session 1, .NET 8.0.23:

- token-group enumeration located a logon SID distinct from the account SID;
- `PipeSecurity.SetAccessRuleProtection(true, false)` produced an explicit non-inheriting ACL;
- the ACL contained `DENY NETWORK FullControl` and `ALLOW <logon SID> FullControl`, with no broad allow rule;
- a competing creation using `PipeOptions.FirstPipeInstance` was rejected while the first server existed;
- 100 simultaneous clients connected to local server `.` and completed a byte round trip;
- every server-side impersonation observation matched both the expected account SID and logon SID.

This proves API availability and the expected current-session behavior. It does not prove a client from another session or machine is rejected until that client is actually attempted.

## Production contract

1. Derive the non-secret pipe name from product/protocol family, Data Root identity, and logon SID. Clients always connect to server `.`.
2. Enumerate the current process token groups and require exactly one enabled group carrying `SE_GROUP_LOGON_ID`; failure is a startup security error.
3. Build a protected DACL with explicit `NETWORK` deny and current logon-SID allow. Do not fall back to the default pipe ACL.
4. Create the initial instance with `FirstPipeInstance`; failure means another endpoint may own the name. Do not retry with weaker options.
5. Create additional server instances only from the established Host after it owns the startup mutex, Data Root exclusive lease, and first pipe instance.
6. On connection, impersonate briefly and capture account SID, logon SID, session identity, token elevation/type as diagnostic evidence. Revert before parsing messages. Reject account/logon/session mismatch before HMAC verification.
7. Then perform the existing incarnation-bound HMAC challenge, protocol negotiation, sequence validation, and frame limits. ACL/token checks do not replace application authentication.
8. Treat elevated and unelevated tokens in the same logon session as the same allowed session only if the interactive matrix confirms expected visibility; HMAC and Vault unlock policy still apply.
9. Never persist the challenge key in logs or diagnostics. On FAT/exFAT it cannot be considered confidential from other local accounts, so writable Data Root policy remains governed by ADR-0011 and its release gate.

## Startup convergence boundary

The initial Host still requires all three authorities:

- `Local\` named mutex for launch arbitration;
- exclusive Data Root `host.lock`/writer lease for persistence authority;
- secured first pipe instance for endpoint-name ownership.

No one signal is sufficient. A loser never publishes endpoint data or mutates the Data Root; it connects to the incumbent only after validating the published incarnation and completing identity/HMAC authentication. A 100-process cold-start race remains part of the interactive matrix because the current harness validated 100 clients against one established server rather than 100 independently launched Host candidates.

## Release-gated matrix

- another local Windows user;
- same account in a second Fast User Switching/RDP logon session;
- `runas` with different credentials;
- elevated and unelevated clients in the same and different logon sessions;
- remote `\\machine\pipe\...` attempt with the Server service enabled;
- 100 cold simultaneous UI/Host candidates converging on one Host incarnation;
- Windows 10 and 11 with the chosen .NET 10 LTS runtime/package combination;
- NTFS runtime directory ACL inspection and FAT/exFAT warning/no-secret-boundary behavior.

Every forbidden client must fail before privileged message parsing. Every successful client must present the expected account/logon/session identity and valid HMAC. Exactly one candidate may publish an endpoint and hold the Data Root lease.

## Prototype disposition

The harness remains off `main` as primary evidence on [`prototype/named-pipe-acl`](https://github.com/RingoCaviar/Rclone_UI/tree/prototype/named-pipe-acl/prototypes/named-pipe-acl) at commit `ffbecc5`.
