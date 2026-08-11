# PROTOTYPE — Argon2id validation

Throwaway harness for Issue #16. It validates the RFC 9106 Argon2id vector and records local latency plus bounded-concurrency memory observations for BouncyCastle.Cryptography 2.6.2.

Run:

```powershell
dotnet run --configuration Release --project prototypes/argon2id-validation/Argon2idValidation.csproj
```

This is evidence, not production code. It deliberately lives only on `prototype/argon2id-validation`.
