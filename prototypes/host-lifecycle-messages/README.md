# PROTOTYPE — Host lifecycle messages

Throwaway Windows harness for Issue #21. It creates an invisible top-level window, registers for WTS session notifications, routes lifecycle messages through the real window procedure, performs a flushed journal checkpoint for each event, and reports the maximum handler duration.

Run the safe self-test:

```powershell
dotnet run --configuration Release --project prototypes/host-lifecycle-messages/HostLifecyclePrototype.csproj
```

The self-test sends messages only to its own window. It does not lock, suspend, log off, restart, or shut down Windows. Those disruptive system-delivery cases require the separate interactive VM matrix.
