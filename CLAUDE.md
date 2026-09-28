# REVIT PLUGINS — repo notes

## Projects
This repo ships **one** plugin product distributed via a single GitHub release. Internally
it is split into three modules so changes can target each surface independently. Versions
are kept in lockstep — bump all three csprojs together.

- **WindCalc** — `WindCalc/WindCalc/WindCalc.csproj`. .NET Framework 4.8, SDK-style csproj. Entry point `WindCalc.App : IExternalApplication`.
- **CCorpPrint** — `CCorpPrint/CCorpPrint/CCorpPrint.csproj`. Same shape.
- **RevitBridge** — `RevitBridge/RevitBridge/RevitBridge.csproj`. Entry point `RevitBridge.App`.
  Gives Claude read/write control of Revit: an in-process add-in serving a named pipe
  (`\\.\pipe\ccorp-revitbridge-<RevitPID>`, current user only) plus a Python MCP server
  in `RevitBridge/mcp/` (package `revit_bridge_mcp`, modelled on `C:\Users\jplor\source\rsap-mcp`).
  Design and rationale: `RevitBridge/HANDOFF.md`.
  - **Deliberate exception to net48:** it targets the runtime Revit itself runs on —
    `net8.0-windows` for 2025, `net10.0-windows` for 2027 (the TFM switches on `RevitYear`).
    `execute_csharp` compiles with Roslyn (`Microsoft.CodeAnalysis.CSharp` 4.11, not
    `.Scripting`) into collectible `AssemblyLoadContext`s, which only works on the native runtime.
  - Threading: the pipe thread never touches the API; requests queue to an `ExternalEvent`
    and run on Revit's main thread. Not picked up in 5 s → `REVIT_BUSY` (nothing ran).
  - Every write command runs in a `TransactionGroup` `"Claude: <method>"` → one Ctrl+Z per call;
    `dry_run` rolls back; destructive ops (`delete_elements`, overwriting `save_as`, sync,
    relinquish, closing a modified doc) and >5,000-element calls need `confirm=true`.
  - Command names are identical on both sides: C# `[BridgeCommand("name")]` = MCP tool name.
    New command = static method in `RevitBridge/RevitBridge/Commands/*.cs` + a tool in
    `RevitBridge/mcp/src/revit_bridge_mcp/tools/*.py`.
  - Payload installs into an `Addins\<year>\RevitBridge\` subfolder (Roslyn DLLs stay out
    of the shared add-ins folder); the `.addin` sits next to it.
  - Audit log: `%APPDATA%\CCorp\RevitBridge\logs\yyyy-MM-dd.log`; live sessions:
    `%APPDATA%\CCorp\RevitBridge\sessions\<pid>.json`.

## Build model (multi-year)

All csprojs are parameterized by the `RevitYear` MSBuild property (default `2025`). Supported years from v1.2.0 onward: **2025, 2027**. Revit API DLLs are resolved via per-year env vars:

- `REVIT_2025_API_PATH`, `REVIT_2027_API_PATH`

Each should point at the folder containing `RevitAPI.dll` and `RevitAPIUI.dll` (typically `C:\Program Files\Autodesk\Revit <year>`).

Conditional compile symbol `REVIT<year>` is defined per build — use `#if REVIT2027` to guard API deltas.

### Common commands

```powershell
# Local dev iteration against one year (builds + deploys to %APPDATA%\Autodesk\Revit\Addins\<year>)
.\WindCalc\Deploy.ps1    -Year 2027 -Config Debug
.\CCorpPrint\Deploy.ps1  -Year 2027 -Config Debug
.\RevitBridge\Deploy.ps1 -Year 2027 -Config Debug   # also pre-approves its ClientIds ("Always Load")

# Full release build for each module → <module>/dist/<year>/
.\WindCalc\Build-All.ps1
.\CCorpPrint\Build-All.ps1
.\RevitBridge\Build-All.ps1

# Produce each installer (requires Inno Setup 6)
.\WindCalc\Installer\Build-Installer.ps1
.\CCorpPrint\Installer\Build-Installer.ps1
.\RevitBridge\Installer\Build-Installer.ps1

# RevitBridge MCP server (Python 3.12; dev/Claude-side, not in the installer)
cd RevitBridge\mcp
py -3.12 -m venv .venv; .\.venv\Scripts\python.exe -m pip install -e ".[dev]"
.\.venv\Scripts\python.exe -m revit_bridge_mcp --selfcheck    # lists tools, no Revit needed
.\.venv\Scripts\python.exe -m pytest -q                       # fake-pipe tests, no Revit needed
$env:REVIT_BRIDGE_PID="<pid>"; .\.venv\Scripts\python.exe tools\smoke.py --undo-check  # live, scratch doc only
```

Revit 2027 **hot-loads new `.addin` files** into running sessions, so a deploy can be picked up
by (and lock DLLs in) an already-open Revit. `RevitBridge\Deploy.ps1` renames locked DLLs
aside before copying and only rewrites the manifest when it changed. Never
`New-Item -Force` an existing registry key: it wipes the values (e.g. every add-in's
"Always Load" approval under `HKCU\...\Autodesk Revit <year>\CodeSigning`).

**Never run bridge write tests against a real project model** — use `new_document`
(scratch project from `TEMPLATE CC PABLO.rte`, saved under `%TEMP%`) or a copy.

## Installer

One Inno Setup script per module: `WindCalc/Installer/WindCalc.iss`,
`CCorpPrint/Installer/CCorpPrint.iss` and `RevitBridge/Installer/RevitBridge.iss`. Each
produces `<Module>-Setup-<version>.exe` in its own `<module>/dist/installer/`. At install
time the user picks which Revit years to target; checkboxes are disabled for years whose
`%APPDATA%\Autodesk\Revit\Addins\<year>` folder doesn't exist. RevitBridge's payload adds
`Microsoft.CodeAnalysis*.dll`; its installer does not install the Python MCP server
(see `RevitBridge/mcp/README.md`).

## Update channel

Releases are published to GitHub Releases on `kronnos3000/REVIT-PLUGINS`. Each release
carries **all three** installer assets. Each module's `UpdateChecker` filters assets by name
prefix (`WindCalc-Setup` / `CCorpPrint-Setup` / `RevitBridge-Setup`) so it picks the right one.

Plugins poll `releases/latest` on startup (background) and — **only when Revit is closing**
(via `ControlledApplication.ApplicationClosing`) — prompt the user if a newer version is
available. Do **not** use `IExternalApplication.OnShutdown` for shutdown-time UI; it fires
after the main window is torn down.

## Version source of truth

Assembly version is set from `<Version>` in each csproj. `<GenerateAssemblyInfo>` is true
— do not maintain a hand-written `AssemblyInfo.cs`. Bump all three modules' `<Version>`
(and `RevitBridge/mcp/pyproject.toml` + `revit_bridge_mcp/__init__.py`) together; one git
tag per release (e.g. `v1.3.0`).
