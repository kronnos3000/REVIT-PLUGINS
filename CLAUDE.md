# REVIT PLUGINS — repo notes

## Projects
- **WindCalc** — `WindCalc/WindCalc/WindCalc.csproj`. .NET Framework 4.8, SDK-style csproj. Entry point `WindCalc.App : IExternalApplication`.

## Build model (multi-year)

`WindCalc.csproj` is parameterized by the `RevitYear` MSBuild property (default `2025`). Supported years: **2024, 2025, 2026, 2027**. Revit API DLLs are resolved via per-year env vars:

- `REVIT_2024_API_PATH`, `REVIT_2025_API_PATH`, `REVIT_2026_API_PATH`, `REVIT_2027_API_PATH`

Each should point at the folder containing `RevitAPI.dll` and `RevitAPIUI.dll` (typically `C:\Program Files\Autodesk\Revit <year>`).

Conditional compile symbol `REVIT<year>` is defined per build — use `#if REVIT2027` to guard API deltas.

### Common commands

```powershell
# Local dev iteration against one year (builds + deploys to %APPDATA%\Autodesk\Revit\Addins\<year>)
.\WindCalc\Deploy.ps1 -Year 2027 -Config Debug

# Full release build for all years with API env vars set → WindCalc/dist/<year>/
.\WindCalc\Build-All.ps1

# Produce the installer (requires Inno Setup 6 on PATH as `iscc`)
.\WindCalc\Installer\Build-Installer.ps1
```

## Installer

Inno Setup script: `WindCalc/Installer/WindCalc.iss`. Produces `WindCalc-Setup-<version>.exe` in `WindCalc/dist/installer/`. At install time the user picks which Revit years to target; checkboxes are disabled for years whose `%APPDATA%\Autodesk\Revit\Addins\<year>` folder doesn't exist.

## Update channel

Releases are published to GitHub Releases. The plugin checks `api.github.com/repos/<owner>/<repo>/releases/latest` on startup (background) and — **only when Revit is closing** (via `ControlledApplication.ApplicationClosing`) — prompts the user if a newer version is available. Do **not** use `IExternalApplication.OnShutdown` for shutdown-time UI; it fires after the main window is torn down.

Repo owner/name is configured in `WindCalc/WindCalc/Services/UpdateChecker.cs` as a constant.

## Version source of truth

Assembly version is set from `<Version>` in `WindCalc.csproj`. `<GenerateAssemblyInfo>` is true — do not maintain a hand-written `AssemblyInfo.cs`.
