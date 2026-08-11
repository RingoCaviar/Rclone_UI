# Windows desktop stack decision

**Issue:** [#2 — Choose the Windows desktop technology stack](https://github.com/RingoCaviar/Rclone_UI/issues/2)  
**Research date:** 2026-08-11  
**Decision status:** Recommended, with a short packaging/interop spike before implementation

## Recommendation

Use **Avalonia UI with C# on the current .NET LTS**, published as a Windows x64 self-contained portable directory (and optionally zipped for download).

Keep rclone, the application updater, and WinFsp installation behind framework-independent service interfaces. This preserves the option to move the UI to another .NET desktop framework or add another OS later without coupling process supervision and update safety to Avalonia.

Avalonia is the best overall fit because it combines:

- a built-in, application-lifetime `TrayIcon` with a native Windows menu;
- self-contained .NET publishing without installing a UI runtime;
- a maintained Fluent-inspired light/dark theme suitable for both Windows 10 and 11;
- first-class Windows UI Automation support for accessibility;
- direct access to the mature .NET process, filesystem, HTTP, cryptography, and Win32 interop surface needed for rclone and WinFsp;
- an escape hatch to other desktop operating systems, at little cost to the Windows-first release.

WinUI 3 has the strongest Microsoft-native visual surface, but its unpackaged/self-contained deployment carries Windows App SDK complexity and a larger runtime payload, while tray integration still needs a separate Win32-style solution. WPF is the lowest-risk Windows-only fallback and has excellent .NET/Win32 interoperability, but needs WinForms or Win32 tray interop and more deliberate styling to reach the requested modern UI. Tauri has excellent sidecar and signed-updater primitives, but its official Windows updater is installer-oriented, not portable-directory-oriented, and it introduces a Rust/web frontend split for an application dominated by native process and filesystem orchestration.

## Requirements used for the decision

The repository's product discovery establishes these constraints:

- Windows 10/11 x64 first release, portable only.
- rclone bundled and managed as a child process.
- WinFsp detected; its official installer downloaded, verified, and launched with explicit UAC.
- application/rclone updates are atomic and recoverable; WinFsp updates use its installer.
- tray operation, scheduled work during a logged-in session, Chinese and English localization, accessible UI, and light/dark Fluent styling.
- ordinary users get task-oriented screens; advanced rclone settings remain available.

“Portable” means no framework or application installer is required before first launch. It does not mean one physical EXE: rclone, resources, rollback slots, and persistent data already require a managed directory layout.

## Comparison

| Criterion | .NET/WPF | WinUI 3 | Avalonia | Tauri 2 |
|---|---|---|---|---|
| Windows 10/11 | Excellent; Windows-only | Excellent; Microsoft's recommended native Windows UI | Excellent; supported Windows target | Good; UI hosted in WebView2 |
| Portable deployment | Excellent; standard self-contained/single-file .NET publishing | Feasible, but unpackaged apps must bundle/install Windows App SDK runtime; self-contained output is larger | Excellent; standard self-contained .NET publishing | Mixed; runnable binaries are possible, but official distribution/update path centers on MSI/NSIS and WebView2 availability |
| Fluent-quality UI | Possible, but substantial application styling or an extra UI library | Best native Fluent controls and Windows behavior | Strong built-in Fluent-inspired light/dark theme; not native WinUI controls | Highly flexible HTML/CSS; Windows-native behavior must be recreated |
| Tray | Stable WinForms `NotifyIcon` or Win32 interop, not WPF-native | Win32/WinForms/community interop; no first-class WinUI tray control found in the evaluated Microsoft surface | Built-in `TrayIcon` and native menu; full Windows support | Built-in tray API |
| rclone/process work | Excellent .NET process/IO APIs | Excellent .NET process/IO APIs | Excellent .NET process/IO APIs | Strong Rust process layer and official sidecar support, but commands must cross the webview IPC boundary |
| UAC/WinFsp | Straightforward Win32/.NET shell launch | Straightforward, with packaging/identity caveats avoided by unpackaged deployment | Straightforward Win32/.NET shell launch | Implement in Rust/Windows APIs; shell permissions must be scoped |
| Accessibility | Mature WPF automation peers/UIA | Native Windows controls and UIA | Built-in automation peers; docs report full Windows UIA support | Web accessibility plus WebView2; quality depends heavily on frontend semantics and focus management |
| Localization | Mature .NET resources/XAML | PRI/XAML resources | .NET resources plus bindings | Web i18n library plus Rust-side messages; two localization surfaces |
| App updater | Must be built; portable atomic swap is fully controllable | Must be built for the portable model; unpackaged apps lack App Installer/Store auto-update | Must be built; portable atomic swap is fully controllable | Official updater enforces signatures and supports GitHub-hosted metadata, but Windows artifacts are MSI/NSIS installers |
| Long-term fit | Very strong for Windows-only | Strong, but Windows App SDK deployment/lifecycle adds moving parts | Strong; one language/runtime for native orchestration and UI, cross-platform option retained | Strong ecosystem, but two languages/toolchains and browser/native boundary increase project complexity |

## Evidence and analysis

### Common .NET foundation: WPF, WinUI 3, and Avalonia

.NET supports both self-contained and single-file publication. A self-contained single file is larger because it includes the runtime, and trimming is only safe for trim-compatible code ([Microsoft: single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)). For this product, a zipped self-contained directory is preferable to optimizing for one EXE: it makes bundled rclone, localization resources, diagnostic symbols, and atomic version directories explicit.

All three .NET choices can use `System.Diagnostics.Process` for redirected stdout/stderr, cancellation, and exit tracking, and can use the Windows `runas` shell verb when explicit elevation is required. Those are platform/runtime capabilities rather than differentiators between the three UI frameworks ([Microsoft: ProcessStartInfo](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo), [Microsoft: ProcessStartInfo.Verb](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.verb)).

### WPF

WPF is a Windows-only, vector-rendered .NET desktop UI framework with XAML, data binding, styles, templates, routed events, and established accessibility support ([Microsoft: WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)). It benefits from the simplest mature .NET-to-Win32 path of the candidates and standard .NET portable publishing.

Its main product-fit cost is surface integration. The official notification-area component is `System.Windows.Forms.NotifyIcon`, so a WPF application uses WinForms or direct Win32 interop for tray behavior ([Microsoft: NotifyIcon](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon)). Modern Fluent appearance is achievable, but WPF itself is not the current native Fluent control set. A third-party control library would become an additional long-term dependency and must be separately evaluated for accessibility, localization, licensing, and maintenance.

**Verdict:** safest fallback if an Avalonia spike reveals a Windows-specific blocker. It is not the first choice because the product explicitly values a polished modern UI and tray operation, where extra integration starts on day one.

### WinUI 3 / Windows App SDK

Microsoft recommends WinUI 3 with Windows App SDK for new native Windows applications; it targets Windows 10 and 11 ([Microsoft: Windows app platform guidance](https://learn.microsoft.com/en-us/windows/apps/), [Microsoft: WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)). It provides the most authentic current Windows controls and system backdrop integration; Windows 11 supports Mica, while Acrylic is the documented fallback for Windows 10 ([Microsoft: system backdrops](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)).

Portable distribution is now technically viable but has qualifications. An unpackaged app has no package identity and therefore no App Installer/Store automatic updates or manifest-based background tasks. It must either install the Windows App SDK runtime or bundle it; bundling significantly increases output size. Unpackaged, self-contained apps can use single-file publishing, but dependencies are extracted to a temporary directory at first launch ([Microsoft: unpackaged WinUI 3 distribution](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app), [Microsoft: Windows App SDK self-contained deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)). The portable application would therefore still need its own atomic updater.

The evaluated Microsoft UI surface does not provide a WinUI equivalent of `TrayIcon`; tray support consequently becomes Win32/WinForms interop. That is feasible, but it removes one of WinUI's “native out of the box” advantages for this particular always-on utility.

**Verdict:** visually strongest, but higher deployment and interop complexity than justified for a portable tray utility. Reconsider if a pixel-authentic Windows 11 UI becomes more important than portable simplicity.

### Avalonia

Avalonia is an open-source .NET XAML UI framework using its own renderer across desktop platforms ([Avalonia: getting started and framework overview](https://docs.avaloniaui.net/docs/get-started/)). Its built-in Fluent theme provides dark and light variants, compact density, customizable palettes, and platform accent-color integration ([Avalonia: themes](https://docs.avaloniaui.net/docs/styling/themes)). It is Fluent-inspired rather than the actual WinUI control implementation, which should be made visible in design review rather than described as “native WinUI.”

For this application's operational shell, Avalonia has a direct advantage: `TrayIcon` is application-scoped, persists without an open window, and has full Windows support with a native menu ([Avalonia: TrayIcon](https://docs.avaloniaui.net/controls/navigation/trayicon), [Avalonia: Windows platform guide](https://docs.avaloniaui.net/docs/platform-specific-guides/windows/)). Accessibility uses automation peers and maps to UI Automation on Windows; the official documentation reports full Windows support and documents Narrator landmarks, keyboard order, live regions, and metadata for custom content ([Avalonia: accessibility](https://docs.avaloniaui.net/docs/app-development/accessibility)).

Avalonia desktop apps use normal .NET publication, so a Windows x64 self-contained output can be copied and launched without an Avalonia or .NET installer. The framework's own rendering/native libraries increase size compared with framework-dependent output, but do not create machine-global runtime state. The application can call all ordinary .NET and Windows interop APIs directly.

**Verdict:** best balance. It minimizes special-case tray/deployment work, keeps native orchestration in one language/runtime, meets the visual target with an official theme, and preserves a future cross-platform path without requiring it now.

### Tauri 2

Tauri has first-class APIs for a system tray and for embedding external binaries (“sidecars”), which is a natural representation of rclone ([Tauri: system tray](https://v2.tauri.app/learn/system-tray/), [Tauri: external binaries](https://v2.tauri.app/develop/sidecar/)). Its capability model can narrowly scope which frontend windows may execute commands, which is valuable when an HTML UI can reach local files or processes ([Tauri: capabilities](https://v2.tauri.app/security/capabilities/)). It uses the operating system webview rather than bundling an entire browser; Tauri documents that application size is largely the Rust binary and frontend assets ([Tauri: app size](https://v2.tauri.app/concept/size/)).

On Windows, the UI depends on WebView2. Tauri's installer can download the bootstrapper, embed it, use an offline installer, or embed a fixed runtime; the fixed runtime adds roughly 180 MB according to its documentation ([Tauri: Windows installer and WebView2 modes](https://v2.tauri.app/distribute/windows-installer/)). Relying on the system evergreen runtime keeps downloads small, but a strictly self-sufficient portable bundle must handle machines where WebView2 is absent or unhealthy.

The official updater has an excellent mandatory signature check and can read static metadata hosted with GitHub Releases. However, on Windows it creates and installs MSI or NSIS update artifacts ([Tauri: updater](https://v2.tauri.app/plugin/updater/)). That conflicts with the product decision to ship only a portable version; a custom portable updater would discard much of this advantage. The native supervisor/update code would also live in Rust while UI state and localization live in a web stack, increasing toolchain and IPC surface.

**Verdict:** capable, especially for a web-skilled team, but not the simplest long-term model for this native, portable process supervisor.

## Proposed architecture consequence

Select Avalonia only for the presentation shell:

```text
Avalonia views/view-models
        |
application use cases (browse, copy, sync, mount, update)
        |
domain state + interfaces
        |
.NET adapters: rclone process/RC, filesystem, scheduler, updater, WinFsp/Win32
```

Do not invoke rclone directly from view models. A dedicated supervisor should own process trees, stdout/stderr parsing, cancellation, forced termination, mount lifetime, and shutdown policy. A separate bootstrapper/updater executable should replace version directories only after signature/hash verification and health-check rollback. These choices are independent of Avalonia and remain testable without the UI.

## Required spike before locking the ADR

Build a throwaway Avalonia Windows x64 prototype and require all of the following to pass:

1. Run from an arbitrary writable folder on clean Windows 10 and Windows 11 machines with no separately installed .NET runtime.
2. Minimize to tray, close/reopen the window, survive Explorer restart, and exit while cleaning up a mock mount child process.
3. Launch a signed mock installer with visible UAC using `runas`; declining UAC must return a useful non-fatal result.
4. Start a bundled mock `rclone.exe`, stream UTF-8/legacy-code-page output, cancel it, and terminate its complete process tree.
5. Verify Narrator, keyboard-only navigation, 100–200% scaling, high contrast, and Chinese/English runtime switching on the main navigation and one complex task form.
6. Measure cold start, working set, and zipped self-contained size; record them as baselines, not pass/fail marketing targets.
7. Demonstrate update staging and rollback while the main executable is running from the portable directory.

If items 1–5 cannot be resolved without unstable native hooks or inaccessible custom controls, fall back to WPF on the same .NET application/domain layers. WinUI 3 should be the fallback only when native Fluent fidelity is explicitly elevated above portable-deployment simplicity.

## Decision summary

**Choose Avalonia + C# + current .NET LTS for the first implementation.** It has the fewest mismatches with the full requirement set. The recommendation is conditional only on a small Windows deployment/accessibility/process-management spike; it does not depend on future cross-platform shipping.

