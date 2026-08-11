# PROTOTYPE — Update handoff crash matrix

Throwaway state-machine harness for Issue #22. It uses only uniquely named temporary directories. For every durable update phase it injects failure before the action, after the action, after the journal write, and while the Data Root is unavailable, then runs recovery.

Run:

```powershell
dotnet run --configuration Release --project prototypes/update-handoff-crash-matrix/UpdateCrashMatrix.csproj
```

The harness models verified artifacts and health evidence; it does not replace real executables or Vaults and does not simulate physical power-loss durability of storage firmware.
