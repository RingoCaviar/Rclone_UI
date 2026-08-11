# PROTOTYPE — Host IPC protocol contract

Throwaway harness for Issue #23. It emits a deterministic golden frame and validates framing, strict JSON parsing, HMAC, sequence, major/minor negotiation, idempotency, deadline, and state-revision behavior.

Run:

```powershell
dotnet run --configuration Release --project prototypes/ipc-protocol-contract/IpcProtocolPrototype.csproj
```

The prototype intentionally keeps the HMAC outside the JSON payload: `uint32-le length | JSON bytes | 32-byte HMAC(length || JSON)`. This authenticates unknown additive fields without requiring JSON canonicalization or a self-referential MAC field.
