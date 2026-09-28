---
name: "revit-robot-preflight"
description: "Use at the start of any Revit or Robot Structural task, or when a Revit/Robot connection seems broken — checks and repairs both connections."
---

# Revit + Robot preflight

Machine: sulaco-ccorps. Mouse buttons are swapped for computer use (send right_click for a left click).

## Steps
1. Revit: call `revit-bridge` → `bridge_status`. Record Revit year/build, bridge version, active document and view.
   - `BRIDGE_UNAVAILABLE`: Revit isn't running, RevitBridge isn't installed, or CCorp Tools → Claude Bridge is Off. Tell Pablo which.
   - `AMBIGUOUS_INSTANCE`: several Revit sessions — `list_instances`, then `select_instance(pid)` for the one Pablo means.
   - `REVIT_BUSY`: a modal dialog or edit mode is open in Revit; ask Pablo to finish/cancel it, then retry once.
2. Robot: call `rsap_status`. If not connected, call `rsap_connect`. If it still fails, screenshot — a modal dialog in Robot usually blocks COM; close it and retry once. If rsap errors with `win32com.gen_py ... has no attribute`, the COM type cache is stale: move `%LOCALAPPDATA%\Temp\gen_py\<pyver>\F3A37BD0-AA2D-11D2-9844-0080C86BE4DFx0x1x0` aside and retry.
3. Units/codes: `rsap_preferences_get` and `rsap_active_codes`. Report units explicitly. (Revit side is always feet/radians internally.)
4. Report one line: `Revit ✅/❌ <year/doc> | Robot ✅/❌ <ver/project/units>`.
5. If anything is still ❌ after one repair attempt, tell Pablo exactly what to click/restart. Don't loop.
6. If versions or behavior differ from the project's CONNECTION LOG, output a short CONNECTION LOG UPDATE block.
