# WindCalc Installer

Produces `WindCalc-Setup-<version>.exe` for end users. Built with **Inno Setup 6**.

## Prerequisites

- Visual Studio 2022+ (MSBuild)
- Revit installs for every year you want to ship against, and matching env vars:
  - `REVIT_2024_API_PATH`, `REVIT_2025_API_PATH`, `REVIT_2026_API_PATH`, `REVIT_2027_API_PATH`
  - Each points at the folder with `RevitAPI.dll` (e.g., `C:\Program Files\Autodesk\Revit 2027`).
  - Years whose env var is unset are silently skipped.
- [Inno Setup 6](https://jrsoftware.org/isdl.php) (`iscc.exe` on PATH or at default `Program Files (x86)`)

## Cutting a release

1. Bump `<Version>` in `WindCalc/WindCalc/WindCalc.csproj`.
2. From the `WindCalc/` folder, run:
   ```powershell
   .\Installer\Build-Installer.ps1
   ```
   This runs `Build-All.ps1` to produce `dist/<year>/` payloads, then invokes Inno Setup to produce `dist/installer/WindCalc-Setup-<version>.exe`.
3. Create a GitHub Release tagged `v<version>` (e.g., `v1.1.0`). Upload the `.exe` as the release asset. Use the release body for changelog — it's surfaced in the shutdown prompt.
4. Existing installs will pick up the release on next Revit startup and prompt the user on next Revit shutdown.

## How the year-picker works

After the Welcome page, the wizard asks the user which Revit year(s) to install into. Each checkbox is enabled only if `%APPDATA%\Autodesk\Revit\Addins\<year>` exists — i.e., Revit for that year is installed and has loaded at least once. Selected years receive `WindCalc.dll`, `WindCalc.addin`, `Newtonsoft.Json.dll`, and the `Resources/` icons.

## Uninstall

Removes all files from every year's Addins folder that setup placed there. Does not touch the plugin's cached data (FEMA shapefiles etc.) under `%USERPROFILE%`.

## Revit-running guard

Setup refuses to run if `Revit.exe` is in the process list (detected via `tasklist`). This prevents silent DLL lock failures.

## Troubleshooting

- **"Revit API DLLs not found for year ..."** — `REVIT_<year>_API_PATH` env var not set or wrong.
- **Installer built but a year is empty** — the `dist/<year>/` folder didn't exist at compile time (no env var for that year). Set the env var, re-run.
- **Update check never fires** — GitHub API rate-limits unauthenticated requests at 60/hr/IP. For a CI job, set a `GITHUB_TOKEN` header (not yet wired up).
