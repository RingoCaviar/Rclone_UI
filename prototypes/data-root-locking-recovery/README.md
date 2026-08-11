# PROTOTYPE — Data Root locking and recovery

Destructive throwaway harness for Issue #17. It creates and deletes only a uniquely named directory under the process temporary directory. It probes canonical aliases, an exclusive `writer.lock`, stale-payload reacquisition, reparse and hard-link behavior, and copy-on-write selector crash boundaries.

Run:

```powershell
dotnet run --configuration Release --project prototypes/data-root-locking-recovery/DataRootPrototype.csproj
```

The harness does not emulate FAT/exFAT, physical removal, disk-full, cloud-sync clients, network-share locking, or power-loss durability. Those require dedicated disposable media/environments.
