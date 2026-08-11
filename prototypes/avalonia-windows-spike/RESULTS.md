# Spike results

## Automated evidence

- Environment: Windows build 10.0.26200, win-x64.
- SDK used: .NET SDK 8.0.417.
- UI package: Avalonia 12.1.0 from official NuGet packages.
- Release build: passed.
- Self-contained `win-x64` publish: passed.
- Published directory: 219 files, 206,934,777 bytes before compression.
- Published executable startup: passed; process remained alive after four seconds and exposed the expected main-window title.

## Implemented probes

- Application-lifetime tray icon with native Show and Exit menu items.
- Close-to-tray behavior with explicit application exit.
- Explicit UAC launch boundary using a harmless elevated process.
- Owned child-process-tree termination.
- Chinese/English content switch.
- Fluent light/dark theme switch.
- Render-scaling and runtime state display.
- UI Automation names and a polite live status region.
- Simulated external updater handoff contract.

## Remaining manual acceptance

- Run the published directory on a supported Windows 10 x64 machine.
- Run it on a supported Windows 11 x64 machine.
- Exercise each visible probe, including accepting and rejecting UAC.
- Verify tray restore after closing the window.
- Verify keyboard-only operation and inspect the controls with Narrator.
- Verify 100%, 150%, and 200% display scaling and both themes.
- Verify Chinese and English layouts do not clip.

The technology decision remains conditional until both operating-system runs complete. Build and startup evidence support Avalonia; they do not yet justify closing issue #8.
