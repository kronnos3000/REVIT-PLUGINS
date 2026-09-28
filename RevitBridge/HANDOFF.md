# RevitBridge — Handoff to Claude Code (VS Code)

> **Goal:** give Claude full read/write control of Revit, with the same depth the `rsap` connector already has for Robot. When this is done, Pablo opens Claude (Cowork / desktop app) and a **`revit-bridge`** connector is live, with typed tools plus a C# escape hatch.
>
> Written 2026-09-26 from a Cowork session. Everything below marked **VERIFIED** was checked live that day.

---

## 0. Kickoff prompt (paste this into Claude Code in VS Code)

```
Read RevitBridge/HANDOFF.md end to end, then the root CLAUDE.md, then
C:\Users\jplor\source\rsap-mcp (the Robot MCP server) to copy its structure and
conventions. Build RevitBridge exactly as specified: the C# add-in module, the Python MCP
server, the deploy/build/installer scripts, and the tests. Work in the phase order in
section 9. After each phase, build and deploy to Revit and run the smoke test before
moving on. Don't stop to ask unless a decision in section 11 is still open. Update the
root CLAUDE.md for the new module when you're done.
```

---

## 1. Decision (final — don't relitigate)

**One solution:** a **custom C# Revit add-in** (in-process, owns the Revit API) plus a **Python MCP server** (stdio, a sibling of `rsap-mcp`) that talks to the add-in over a **Windows named pipe**.

Why this and not the alternatives:

| Option | Verdict |
|---|---|
| Existing `revit` extension (7 tools) | Read/select/navigate/export only. **Remove.** |
| `Revit Connector` extension | Fails at startup ("Connection closed"), no logs, unknown source. **Remove.** |
| pyRevit Routes as the bridge | IronPython/CPython limits, tied to pyRevit's release cycle. **Not used for Claude.** pyRevit stays installed only for Pablo's own ribbon tools (`/pyRevit/` is already git-ignored as unrelated). |
| **Custom C# add-in + MCP** | Full Revit API, native .NET, one codebase we own, same pattern as `rsap`. **Build this.** |

Why Revit needs an add-in when Robot didn't: Robot exposes an **out-of-process COM API**, so `rsap-mcp` drives it directly. The **Revit API only works inside Revit.exe, on Revit's main thread, from a valid API context.** So something has to live inside Revit and receive commands. That's the add-in.

---

## 2. Current state (VERIFIED 2026-09-26)

**Machine:** `sulaco-ccorps`, Windows x64, user `jplor`.

**Claude desktop local MCP servers:**

| Name | State | Source |
|---|---|---|
| `rsap` | working | `claude_desktop_config.json` → `C:\Users\jplor\source\rsap-mcp\.venv\Scripts\python.exe -m rsap_mcp`, `PYTHONPATH=...\rsap-mcp\src`, env `RSAP_VISIBLE/INTERACTIVE/AUTO_CONNECT=true`, `RSAP_LOG_LEVEL=INFO` |
| `revit` | working, read-only | Desktop **extension** (Settings → Extensions), not in the config file. Tools: `get_running_revit_instances`, `query_model`, `get_element_data`, `select_elements`, `zoom_to_elements`, `open_view`, `export_views` |
| `Revit Connector` | **failed**, "Connection closed" | Desktop extension. No log file found. |

**Current `claude_desktop_config.json`:**
```json
{
  "mcpServers": {
    "rsap": {
      "command": "C:\\Users\\jplor\\source\\rsap-mcp\\.venv\\Scripts\\python.exe",
      "args": ["-m", "rsap_mcp"],
      "env": {
        "PYTHONPATH": "C:\\Users\\jplor\\source\\rsap-mcp\\src",
        "RSAP_VISIBLE": "true",
        "RSAP_INTERACTIVE": "true",
        "RSAP_AUTO_CONNECT": "true",
        "RSAP_LOG_LEVEL": "INFO"
      }
    }
  }
}
```

**Revit:** one instance running (PID 755804) with `G:\Shared drives\4-10A   Design & Planning\CONSTRUCTION CORPS\0122 - BARBIERI - 1976 ARVIS CIR E\Design\Barbieri  1976 Arvis Circle E_UP TO DATE.rvt`. A read test returned 398 elements (walls 222, framing 83, doors 50, windows 43). Note that model paths live on the **G: shared drive** and contain double spaces and `&`, so quote every path.

**Repo (`C:\Users\jplor\OneDrive\Business\Documents\CODE\REVIT PLUGINS`, remote `kronnos3000/REVIT-PLUGINS`):**
- Modules `WindCalc` and `CCorpPrint`, both SDK-style csproj, `net48`, x64, LangVersion 9, `<Version>1.2.0</Version>` in lockstep.
- `RevitYear` MSBuild property (default 2025); supported years **2025 and 2027**; `REVIT_<year>_API_PATH` env vars; `#if REVIT2027` for API deltas (e.g. `ElementId.Value` vs `IntegerValue`; see `CCorpPrint/Engine/ElementIdCompat.cs`).
- MSBuild at `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`.
- Shared ribbon tab `"CCorp Tools"`; each module adds its own panel.
- `Deploy.ps1 -Year -Config` → `%APPDATA%\Autodesk\Revit\Addins\<year>`; `Build-All.ps1` → `dist\<year>\`; Inno Setup 6 installer per module (`<Module>-Setup-<ver>.exe`, per-user, year checkboxes, refuses to run while Revit is open).
- `UpdateChecker` polls GitHub Releases, filters by asset prefix, and prompts **only on `ApplicationClosing`**, subscribed via reflection because the args type is internal in 2025+. Never put shutdown UI in `OnShutdown`.
- Signing: self-signed `ConstructionCorps.pfx` (git-ignored) plus `.cer` and a trust script under `WindCalc/Installer/signing/`.
- Firm shared-parameter file: `G:\Shared drives\4-10A   Design & Planning\AXS_ARCHITECTURAL (RESOURCES)\CC_SharedParameters.txt`. Correct spec IDs: Number = `SpecTypeId.Number`, Integer = `SpecTypeId.Int.Integer`, Text = `SpecTypeId.String.Text`. **WindCalc's `SharedParamWriter` has Number wrong; don't copy that bug.**
- Existing validated-changeset pattern in `pyRevit/Extensions/ccorp.extension/lib/ccorp/qa` (dry run → audit CSV → apply). Reuse the idea for bulk writes.

---

## 3. Architecture

```
Claude (Cowork / desktop / Claude Code)
        │  MCP over stdio
        ▼
revit-bridge MCP server (Python, FastMCP)     ← RevitBridge/mcp/
        │  JSON-RPC, newline-delimited, over named pipe
        │  \\.\pipe\ccorp-revitbridge-<RevitPID>
        ▼
RevitBridge.dll (C# add-in inside Revit.exe)  ← RevitBridge/RevitBridge/
   PipeServer (background thread)
        → RequestQueue → ExternalEvent.Raise()
        → BridgeHandler.Execute(UIApplication)  [Revit main thread]
              → CommandRouter → Commands/*  (typed ops)
                             → ScriptRunner (execute_csharp, Roslyn)
        ← TaskCompletionSource → JSON response
```

**Why a named pipe:** no port, no URL ACL or admin, no firewall prompt, scoped to the current user, and one pipe per Revit process (multiple Revit instances work naturally). Python opens it with `open(r'\\.\pipe\...', 'r+b', buffering=0)` or `pywin32`. Discovery works by listing `\\.\pipe\` and filtering the prefix.

---

## 4. C# add-in — `RevitBridge/RevitBridge/`

### 4.1 Project
- **`net8.0-windows`**, x64, `UseWPF`, LangVersion latest. **Deliberate deviation** from the repo's `net48`: Revit 2025+ runs on .NET 8, and Roslyn compile-and-load (4.6) is clean only on the native runtime. Keep the same `RevitYear` / `REVIT_<year>_API_PATH` / `#if REVIT2027` scheme and the same `VerifyRevitApiPath` target (supported: 2025, 2027).
- Reuse the repo's csproj shape, `.addin` shape (new GUIDs, `VendorId=CCorp`), ribbon helpers, `Logger` pattern, and `UpdateChecker` (asset prefix `RevitBridge-Setup`).
- Packages: `Newtonsoft.Json` 13.0.3 (match the repo) and `Microsoft.CodeAnalysis.CSharp` (not `.Scripting`; see 4.6).

### 4.2 Startup (`App : IExternalApplication`)
- Create the `ExternalEvent` plus handler, start `PipeServer` on a background thread, and add panel **"Claude Bridge"** to the `"CCorp Tools"` tab with:
  - **Bridge On/Off** toggle, showing its state
  - **Status** dialog: pipe name, PID, active doc, last 20 calls
  - **Open Log**
- Write `%APPDATA%\CCorp\RevitBridge\sessions\<pid>.json` with `{pid, revitYear, versionBuild, pipe, startedUtc}`. Delete it on `ApplicationClosing`.
- Subscribe to `ApplicationClosing` the same way CCorpPrint does, for the update prompt.

### 4.3 Threading (the part that has to be right)
- `PipeServer` accepts connections (`NamedPipeServerStream`, `PipeOptions.Asynchronous`, multiple instances), reads a line, parses `{id, method, params}`, and enqueues a `BridgeRequest` with a `TaskCompletionSource`.
- It calls `ExternalEvent.Raise()`. `BridgeHandler.Execute(UIApplication app)` drains the queue **on the main thread**, runs each request, and completes its TCS.
- Timeouts:
  - Default **60 s**, overridable per call.
  - If not picked up within **5 s**, return `{"error":"REVIT_BUSY"}` with a hint (a modal dialog or edit mode is open).
  - The calc side keeps running, so the Python side must not retry a write blindly.
- Never touch the Revit API off the main thread. Never block the main thread waiting on the pipe.

### 4.4 Transactions, warnings, errors
- Every write command runs in a **`TransactionGroup` named `"Claude: <method>"`** and `Assimilate()`s on success, so **one Ctrl+Z in Revit undoes one Claude call**. Roll back on any exception.
- An `IFailuresPreprocessor` collects warnings (returned in the response as `warnings[]`), deletes warnings when `params.dismiss_warnings=true`, and rolls back on errors, returning `errors[]`.
- Common response envelope:
  `{ok, result, warnings[], errors[], changed:{created[],modified[],deleted[]}, tx_name, elapsed_ms, doc}`
  Report ElementIds as `long` via the `AsLong()` compat helper.

### 4.5 Safety rails (enforced in C#, not only in Python)
- `delete_elements`, `save_as` over an existing file, `synchronize_with_central`, `relinquish`, and closing a modified doc all require `confirm=true`. Without it they return a **preview** (counts, sample of 20, dependent elements via `Document.GetDependentElements`).
- Every write tool accepts `dry_run=true`: it runs inside the transaction, collects the `changed` set, then **rolls back**.
- Never call `SynchronizeWithCentral` unless `confirm=true` **and** the method is `synchronize_with_central`.
- Audit log (append-only, daily): `%APPDATA%\CCorp\RevitBridge\logs\yyyy-MM-dd.log`, one line per call: time, method, doc, params hash, ok/err, changed counts.
- Size guard: refuse any single call that would touch more than 5,000 elements unless `confirm=true`.

### 4.6 `execute_csharp`, the escape hatch (full API access)
- Compile with **`Microsoft.CodeAnalysis.CSharp`** (`CSharpCompilation` → emit to a MemoryStream → `Assembly.Load(bytes)`, or a collectible `AssemblyLoadContext` so repeated scripts don't leak). Don't use `CSharpScript`; it pulls in more dependencies and has conflicted with Dynamo's Roslyn in the past.
- References: the loaded `RevitAPI`, `RevitAPIUI`, System.*, Newtonsoft, and the bridge's own `ScriptContext`.
- User code is wrapped as:
  ```csharp
  public static class __Script { public static object Run(ScriptContext ctx) { <body> } }
  ```
  `ctx` exposes `Doc`, `UIDoc`, `UIApp`, `App`, `Log(string)`, `ToFeet(double, string unit)`, and `ctx.Result` helpers.
- `mode: "read"` (no transaction; a write throws) or `"write"` (wrapped in the standard TransactionGroup, with failure handling and rollback on exception).
- Return value is serialized with Newtonsoft using a converter for `ElementId`, `XYZ`, `BoundingBoxXYZ` and `Element` (id/name/category). Compile errors come back with line/column.
- Cache compiled assemblies by SHA-256 of the source.

### 4.7 Units
Revit internal units are **feet** and **radians**. Every length param accepts a number (feet) or a string with units (`"12'-6\""`, `"150 in"`, `"3.2 m"`) parsed by one `UnitParser`. Responses are in feet, plus a display string using the document's format options.

---

## 5. Command set (typed tools)

Names are identical on both sides (C# `CommandRouter` key = MCP tool name). All take an optional `doc` (title or path; defaults to active) and, for writes, `dry_run` / `confirm` / `dismiss_warnings`.

**Session**
`bridge_status` · `list_instances` (reads the sessions folder plus pipe liveness) · `list_documents` · `activate_document` · `open_document(path, detach?, audit?)` · `save_document` · `save_as(path, confirm)` (default is a new versioned name `_v2`, `_v3`) · `close_document(confirm)` · `synchronize_with_central(confirm)` · `get_warnings` · `get_project_info`

**Query**
`query_elements` (category, class, level, view, bbox, parameter filters, name filters, max_results, plus an analysis breakdown like the current `revit.query_model`) · `get_elements(ids, fields: basic|params|all|specific[], geometry: none|bbox|location|solid_summary)` · `list_types(category)` · `list_families(category)` · `list_levels` · `list_grids` · `list_views(type)` · `list_sheets` · `list_schedules` · `get_schedule_data(id)` · `get_selection` · `list_worksets` · `list_phases` · `list_design_options` · `list_links`

**UI / navigation / export**
`select(ids)` · `zoom_to(ids)` · `open_view(id|name)` · `capture_view(view, width, format)`, which returns an image path (and base64 if small) · `export_pdf(sheets|views, combine, naming)` · `export_dwg(views, setup)` · `export_ifc(options)` · `export_image`

**Create**
`create_levels` · `create_grids` · `create_walls(type, curves|points, level, top_level|height, flip?, structural?)` · `create_floor(type, boundary, level)` · `create_roof_footprint` · `place_family_instances(family, type, points, level, host?, rotation?, structural_type?)` (doors, windows, columns, foundations, generic) · `create_beams(type, lines, level, z_justification?)` · `create_columns(type, points, base_level, top_level)` · `create_braces` · `create_view(plan|ceiling|section|elevation|3d|drafting, params)` · `duplicate_view` · `create_sheet(titleblock, number, name)` · `place_viewport(sheet, view, point)` · `create_schedule(category, fields[], filters[], sorting[])` · `create_text_note` · `create_tag` · `create_dimension(refs)` (best-effort)

**Modify**
`set_parameters([{id, name|builtin|guid, value}])` (batch, unit-aware) · `set_type_parameters` · `change_type(ids, type)` · `duplicate_type(type, new_name, params)` (e.g. create a W10x22 or 2x12 SYP #2 type) · `move` · `rotate` · `copy` · `mirror` · `array` · `delete_elements(ids, confirm)` · `join_geometry` · `set_workset` · `set_phase` · `load_family(path)` · `bind_shared_parameter(name, group, categories, instance|type)` (uses the firm file and correct SpecTypeIds)

**Structural / analytical (for the Robot round trip)**
`list_analytical_members` (Revit 2023+ `AnalyticalMember`: curve, section, material, releases, linked physical id) · `set_analytical_releases` · `create_point_load` / `create_line_load` / `create_area_load` · `list_load_cases` / `create_load_case` · `get_structural_usage` · `set_structural_usage`

**Escape hatch**
`execute_csharp(code, mode, timeout_s)`

**Rule of thumb for Claude:** use typed tools first. Use `execute_csharp` when no typed tool fits, and when the same `execute_csharp` pattern shows up twice, promote it to a typed tool.

---

## 6. Python MCP server — `RevitBridge/mcp/`

- **Mirror `C:\Users\jplor\source\rsap-mcp` exactly** (read it first): `src/` layout, `.venv`, `-m` entry point, env-var config, logging style, tool naming style. Package: `revit_bridge_mcp`. Use the official `mcp` SDK (FastMCP).
- `pipe_client.py`:
  - Discovers `\\.\pipe\ccorp-revitbridge-*` and picks an instance: env `REVIT_BRIDGE_PID`, else the one with the active document, else the only one, else an error listing instances.
  - Sends newline JSON and reads the reply. Reconnects on a broken pipe.
  - **Never auto-retries write methods.**
- One MCP tool per command in section 5, with typed args (pydantic), docstrings that state units (feet) and safety flags. The tool description should say that each call is one Ctrl+Z in Revit.
- Env vars: `REVIT_BRIDGE_PID` (optional), `REVIT_BRIDGE_TIMEOUT_S` (default 60), `REVIT_BRIDGE_LOG_LEVEL` (default INFO).
- `RevitBridge/mcp/tests/`: pytest with a fake pipe server covering discovery, envelope parsing, busy/timeout handling, and no-retry on writes.

**Final `claude_desktop_config.json`** (keep `rsap` as is and add this; the path is inside the OneDrive repo):
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
> OneDrive can lock files mid-sync inside `.venv`. If that causes trouble, move the venv to `C:\Users\jplor\source\revit-bridge-venv` and point `command` there. Add `RevitBridge/mcp/.venv/` to `.gitignore` either way.

---

## 7. Repo integration

- New module folder `RevitBridge/`, same shape as the others: `RevitBridge.sln`, `RevitBridge/RevitBridge.csproj`, `RevitBridge.addin`, `Deploy.ps1`, `Build-All.ps1`, `Installer/RevitBridge.iss` + `Build-Installer.ps1` (copy CCorpPrint's; new AppId GUID; the payload adds `Microsoft.CodeAnalysis*.dll`), and `mcp/`.
- The installer does **not** install the Python server. It's a dev and Claude-side component. Document its setup in `RevitBridge/mcp/README.md` (`py -3.12 -m venv .venv`, `pip install -e .`).
- **Lockstep version:** bump all three csprojs to `1.3.0` for the first release, with one tag `v1.3.0` and three installer assets.
- Update the **root `CLAUDE.md`**: add RevitBridge to Projects, note the `net8.0-windows` exception and why, and add its commands.
- `.gitignore`: add `RevitBridge/mcp/.venv/`, `**/__pycache__/`, `.pytest_cache/`.

---

## 8. Cleanup (Pablo does these in the Claude app; they can't be scripted)

1. **Settings → Extensions → remove `Revit Connector`.**
2. **Settings → Extensions → remove `revit`** (after RevitBridge passes acceptance; `revit-bridge` replaces it).
3. Add the `revit-bridge` block to `%APPDATA%\Claude\claude_desktop_config.json`, then **fully quit and reopen** the Claude desktop app.
4. In Cowork, update these three saved skills to use `revit-bridge` tools instead of the `revit` extension and pyRevit: `revit-robot-preflight`, `revit-robot-roundtrip`, `robot-analysis-run`. Also update the project instructions file `Revit-Robot-Project-Instructions.md` the same way.

---

## 9. Build phases (each ends with deploy → smoke test)

| # | Phase | Done when |
|---|---|---|
| 1 | Skeleton: csproj, addin, App, ribbon, pipe server, ExternalEvent queue, `bridge_status`, `list_documents` | Python `bridge_status` returns the PID, year and active doc from a live Revit |
| 2 | Envelope, TransactionGroup, failure preprocessor, audit log, dry_run/confirm | A `dry_run` wall create returns `changed.created` and the model is unchanged |
| 3 | Query + UI tools (parity with the old `revit` extension, then beyond) | Barbieri UP TO DATE: counts match 222/83/50/43 |
| 4 | Create + modify tools | Script creates levels, grids, walls, beams, columns, a sheet with a viewport in a **scratch copy**; each call is one Ctrl+Z |
| 5 | `execute_csharp` | Read and write scripts work; compile errors return line/col; 100 runs without memory growth |
| 6 | Structural/analytical tools | Analytical members listed with sections; a round trip into `rsap` works (section 10) |
| 7 | Installer, update checker, docs, CLAUDE.md, v1.3.0 | Installer installs to 2025/2027; release assets named correctly |

**Smoke test:** `RevitBridge/mcp/tools/smoke.py` runs status → list_documents → query → dry-run create → real create in a scratch doc → verify → delete with confirm. **Never run write tests against a real project model.** Use `File → Save As` to a scratch copy under `%TEMP%` or a template-based new doc (`TEMPLATE CC PABLO.rte`).

---

## 10. How Claude will use it (context for tool design)

- **Preflight** (every Cowork chat): `revit-bridge.bridge_status` + `rsap_status`, giving one status line.
- **Revit → Robot:** `list_analytical_members` / `query_elements` → build a table → `rsap_add_nodes` / `rsap_add_bars` → map Revit ElementId ↔ Robot bar id. Store the map in shared param **`CC_Robot_Id`** (Text, instance; add it to the firm shared-param file under group "Analysis") so the link survives sessions.
- **Robot → Revit:** results (sizes, ratios, reactions) → `set_parameters` / `change_type` / `duplicate_type`, in one call each so each is one undo.
- **Project rules** Claude follows:
  - For 0122 Barbieri, **UP TO DATE** governs openings and **NEW FRAMING** governs framing details.
  - Never sync to central or delete without explicit instruction.
  - Engineering deliverables are "Prepared for review by Brennen Bourgois, P.E." **Never put Pablo's name or a PE number in a seal block.**

---

## 11. Open items (the only questions for Pablo)

1. **Daily Revit year:** 2025 or 2027? Claude Code can confirm by reading `%APPDATA%\Autodesk\Revit\Addins\` and the running `Revit.exe` file version. Build that year first.
2. **Confirm `net8.0-windows`** for RevitBridge only. Default: yes. The other modules stay `net48` untouched.

---

## 12. Acceptance (definition of done)

- [ ] In a fresh Cowork chat, `revit-bridge` tools appear and `bridge_status` reports the live Revit doc.
- [ ] Claude can create, modify and delete (with confirm) elements; each call is one Ctrl+Z.
- [ ] `execute_csharp` runs arbitrary Revit API code in read and write modes.
- [ ] A Revit → Robot → Revit round trip works on a scratch model.
- [ ] `Revit Connector` and `revit` extensions are removed; there are no duplicate or dead Revit connectors.
- [ ] Repo builds for 2025 and 2027; installer and release are v1.3.0; root CLAUDE.md is updated.
