# revit-bridge MCP server

MCP server that lets Claude read and edit Autodesk Revit through the **CCorp RevitBridge**
add-in. The add-in runs inside `Revit.exe` and executes every call on Revit's main thread;
this Python package is a thin, typed front end that talks to it over a Windows named pipe
(`\\.\pipe\ccorp-revitbridge-<RevitPID>`, current user only).

```
Claude ──MCP/stdio──> revit_bridge_mcp ──JSON lines/named pipe──> RevitBridge.dll (in Revit)
```

The Revit installer (`RevitBridge-Setup-<ver>.exe`) installs only the add-in. This server
is a dev/Claude-side component and is set up by hand, once per machine.

## Requirements

| | |
|---|---|
| OS | Windows 10/11 x64 |
| Revit | 2025 or 2027 with the RevitBridge add-in (CCorp Tools → Claude Bridge → **On**) |
| Python | 3.12 x64 (`py -3.12`) |

## Install

```powershell
cd "C:\Users\jplor\OneDrive\Business\Documents\CODE\REVIT PLUGINS\RevitBridge\mcp"
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -e ".[dev]"
.\.venv\Scripts\python.exe -m revit_bridge_mcp --selfcheck   # lists tools, no Revit needed
```

> OneDrive can lock files inside `.venv` mid-sync. If installs or startup fail with
> access-denied errors, create the venv at `C:\Users\jplor\source\revit-bridge-venv`
> instead and point `command` below at that `python.exe` (keep `PYTHONPATH` as is).

## Register with Claude Desktop

Add this block under `mcpServers` in `%APPDATA%\Claude\claude_desktop_config.json`
(keep the existing `rsap` entry), then **fully quit** and reopen Claude Desktop:

```json
"revit-bridge": {
  "command": "C:\\Users\\jplor\\OneDrive\\Business\\Documents\\CODE\\REVIT PLUGINS\\RevitBridge\\mcp\\.venv\\Scripts\\python.exe",
  "args": ["-m", "revit_bridge_mcp"],
  "env": {
    "PYTHONPATH": "C:\\Users\\jplor\\OneDrive\\Business\\Documents\\CODE\\REVIT PLUGINS\\RevitBridge\\mcp\\src",
    "REVIT_BRIDGE_TIMEOUT_S": "60",
    "REVIT_BRIDGE_LOG_LEVEL": "INFO"
  }
}
```

Log: `%APPDATA%\Claude\logs\mcp-server-revit-bridge.log`. The add-in's own audit log
(one line per call) is `%APPDATA%\CCorp\RevitBridge\logs\yyyy-MM-dd.log`.

## Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `REVIT_BRIDGE_PID` | – | Pin to one Revit process. Otherwise: the only instance, else the one with an active document, else an error listing instances. |
| `REVIT_BRIDGE_TIMEOUT_S` | `60` | Default per-call timeout. Tools that take `timeout_s` override it. |
| `REVIT_BRIDGE_LOG_LEVEL` | `INFO` | stderr log level. |
| `REVIT_BRIDGE_LOG_FILE` | – | Optional rotating log file. |

## Safety model

- **One call = one undo.** Every write runs in a `TransactionGroup` named `Claude: <tool>`.
- `dry_run=true` runs a write, reports `changed`, and rolls back.
- `delete_elements`, `save_as` over an existing file, `synchronize_with_central`,
  `relinquish`, and closing a modified document need `confirm=true`; without it they return
  a preview. Any call touching more than 5,000 elements also needs `confirm=true`.
- The client **never retries a write**. `REVIT_BUSY` (Revit didn't pick the call up within
  5 s) guarantees nothing ran; `TIMEOUT` means it may still be running.

## Tests

```powershell
.\.venv\Scripts\python.exe -m pytest -q          # fake pipe server; no Revit needed
$env:REVIT_BRIDGE_PID = "<pid of a scratch Revit>"
.\.venv\Scripts\python.exe tools\smoke.py        # live smoke test (scratch doc only)
```

`tools/smoke.py` refuses to write to a document unless it is one the smoke test created
itself under `%TEMP%`.

## Layout

```
mcp/
  src/revit_bridge_mcp/
    server.py        FastMCP entry point (+ --selfcheck)
    config.py        env settings, stderr logging
    pipe_client.py   discovery, instance selection, pipe transport, no-retry-on-write
    models/          pydantic argument models
    tools/           one module per tool group; names match the add-in's commands
  tests/             pytest + fake pipe server
  tools/smoke.py     live end-to-end smoke test
```
