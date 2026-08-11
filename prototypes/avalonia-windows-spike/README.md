# Avalonia Windows integration spike

> THROWAWAY PROTOTYPE — decision evidence for GitHub issue #8, not production code.

Run:

```powershell
dotnet run --project prototypes/avalonia-windows-spike/AvaloniaWindowsSpike.csproj
```

Publish the portable Windows x64 directory:

```powershell
dotnet publish prototypes/avalonia-windows-spike/AvaloniaWindowsSpike.csproj -c Release -r win-x64 --self-contained true -o artifacts/avalonia-windows-spike
```

The visible state panel reports runtime, architecture, render scaling, theme, culture, and accessibility setup. Buttons exercise tray lifetime, an explicit harmless UAC boundary, child-process-tree termination, bilingual content, theme switching, and the updater handoff contract.

Manual acceptance requires runs on both Windows 10 and Windows 11. The current development machine can establish build and Windows execution viability but cannot substitute for both OS test targets.
