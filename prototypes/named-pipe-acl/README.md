# PROTOTYPE — Named-pipe ACL and identity

Throwaway Windows harness for Issue #19. It extracts the current token's logon SID, creates a protected DACL that denies NETWORK and allows only that logon SID, requests first-pipe-instance semantics, connects 100 concurrent local clients, and inspects each client identity through pipe impersonation.

Run:

```powershell
dotnet run --configuration Release --project prototypes/named-pipe-acl/NamedPipeAclPrototype.csproj
```

The one-command run covers only the current user/session/token and installed .NET SDK. The cross-user, Fast User Switching, runas, elevation, remote, Windows 10, .NET 10, and FAT/exFAT cases require an interactive machine matrix.
