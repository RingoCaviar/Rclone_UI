---
status: accepted
---

# Use Avalonia for the Windows desktop UI

Build Rclone UI with C# on the current supported .NET LTS and Avalonia UI, publishing a self-contained Windows x64 portable directory. The research and Windows 10/11 spike validated the required tray lifetime, explicit UAC boundary, child-process supervision, localization, DPI handling, accessibility hooks, Fluent theming, and updater handoff while avoiding WinUI 3 deployment complexity and Tauri's installer-oriented Windows updater; keep rclone, WinFsp, process management, and updating behind UI-framework-independent interfaces so WPF remains a viable fallback if a later Windows integration blocker appears.
