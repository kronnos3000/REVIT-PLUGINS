# REVIT PLUGINS — repo notes

## Projects
This repo ships **one** plugin product distributed via a single GitHub release. Internally
it is split into two modules so changes can target each surface independently. Versions
are kept in lockstep — bump both csprojs together.

- **WindCalc** — `WindCalc/WindCalc/WindCalc.csproj`. .NET Framework 4.8, SDK-style csproj. Entry point `WindCalc.App : IExternalApplication`.
- **CCorpPrint** — `CCorpPrint/CCorpPrint/CCorpPrint.csproj`. Same shape.

## Build model (multi-year)

Both csprojs are parameterized by the `RevitYear` MSBuild property (default `2025`). Supported years from v1.2.0 onward: **2025, 2027**. Revit API DLLs are resolved via per-year env vars:

- `REVIT_2025_API_PATH`, `REVIT_2027_API_PATH`

Each should point at the folder containing `RevitAPI.dll` and `RevitAPIUI.dll` (typically `C:\Program Files\Autodesk\Revit <year>`).

Conditional compile symbol `REVIT<year>` is defined per build — use `#if REVIT2027` to guard API deltas.

### Common commands

```powershell
# Local dev iteration against one year (builds + deploys to %APPDATA%\Autodesk\Revit\Addins\<year>)
.\WindCalc\Deploy.ps1   -Year 2027 -Config Debug
.\CCorpPrint\Deploy.ps1 -Year 2027 -Config Debug

# Full release build for both modules → <module>/dist/<year>/
.\WindCalc\Build-All.ps1
.\CCorpPrint\Build-All.ps1

# Produce each installer (requires Inno Setup 6)
.\WindCalc\Installer\Build-Installer.ps1
.\CCorpPrint\Installer\Build-Installer.ps1
```

## Installer

One Inno Setup script per module: `WindCalc/Installer/WindCalc.iss` and
`CCorpPrint/Installer/CCorpPrint.iss`. Each produces `<Module>-Setup-<version>.exe` in its
own `<module>/dist/installer/`. At install time the user picks which Revit years to target;
checkboxes are disabled for years whose `%APPDATA%\Autodesk\Revit\Addins\<year>` folder
doesn't exist.

## Update channel

Releases are published to GitHub Releases on `kronnos3000/REVIT-PLUGINS`. Each release
carries **both** installer assets. Each module's `UpdateChecker` filters assets by name
prefix (`WindCalc-Setup` / `CCorpPrint-Setup`) so it picks the right one.

Plugins poll `releases/latest` on startup (background) and — **only when Revit is closing**
(via `ControlledApplication.ApplicationClosing`) — prompt the user if a newer version is
available. Do **not** use `IExternalApplication.OnShutdown` for shutdown-time UI; it fires
after the main window is torn down.

## Version source of truth

Assembly version is set from `<Version>` in each csproj. `<GenerateAssemblyInfo>` is true
— do not maintain a hand-written `AssemblyInfo.cs`. Bump both modules' `<Version>`
together; one git tag per release (e.g. `v1.2.0`).
