# Rclone UI

Portable Windows 10/11 rclone desktop application. The production baseline targets .NET 10 LTS, Avalonia 12.1, and Windows x64.

## Prerequisites

- .NET SDK 10.0.100 or newer feature band in the .NET 10 line.
- Windows 10 22H2 x64 or Windows 11 for supported development and validation.

## Verify

From PowerShell:

```powershell
./scripts/verify.ps1
```

This restores locked dependencies, verifies formatting, builds with warnings as errors, and runs contract, architecture, and integration tests.

## Portable output

```powershell
./scripts/publish-portable.ps1
```

The command creates a self-contained `win-x64` layout, deterministic ZIP, release manifest, third-party notices and SHA-256 checksum under `artifacts/release/`. Pass `-SigningThumbprint` only in the protected release environment to activate the Authenticode signing hook.

Automated artifacts are release candidates only. Windows 10/11, accessibility, WinFsp and destructive recovery qualification remains human-owned; see [release qualification](docs/release/qualification.md).

See [module boundaries](docs/architecture/module-boundaries.md) for dependency and state ownership rules.
