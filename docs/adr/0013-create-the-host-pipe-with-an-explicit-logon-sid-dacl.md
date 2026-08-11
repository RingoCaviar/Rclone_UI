---
status: accepted
---

# Create the Host pipe with an explicit logon-SID DACL

Create the Background Host named pipe through `NamedPipeServerStreamAcl.Create` using a protected, non-inheriting DACL that denies the Windows `NETWORK` SID and allows only the current token's logon SID. Do not grant the broader account SID or default user groups. Request `PipeOptions.FirstPipeInstance` for the first instance and fail closed if endpoint-name ownership cannot be established.

Before parsing a client frame, use pipe impersonation to verify the expected account, logon session, and session identity, then revert and require the incarnation-bound HMAC handshake. ACL and token identity are complementary to, not replacements for, the application authentication protocol.

Additional pipe instances may be created only by the Host that already owns the launch mutex, Data Root exclusive lease, and secured first instance. The design is accepted from the managed-API prototype, but release qualification remains conditional on cross-user/session/elevation/remote tests, a 100-process cold-start race, and the Windows 10/11 .NET LTS matrix.
