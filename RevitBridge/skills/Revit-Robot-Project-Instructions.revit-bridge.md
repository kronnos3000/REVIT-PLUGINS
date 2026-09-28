# Revit-Robot project instructions — revit-bridge update

Replace every reference to the old `revit` extension (`revit__query_model`, `get_element_data`,
`select_elements`, `zoom_to_elements`, `open_view`, `export_views`) and to pyRevit write scripts
with the `revit-bridge` tools below. Keep the rest of the project instructions as they are.

## Connections
- **Revit:** `revit-bridge` MCP server (CCorp RevitBridge add-in, v1.3.0+). Preflight = `bridge_status`.
- **Robot:** `rsap` MCP server. Preflight = `rsap_status`.
- pyRevit is no longer used by Claude (it stays installed for Pablo's own ribbon tools).

## Tool mapping (old → new)
| Old | revit-bridge |
|---|---|
| `get_running_revit_instances` | `bridge_status`, `list_instances`, `select_instance` |
| `query_model` | `query_elements` (same category/level/type counts, plus parameter filters) |
| `get_element_data` | `get_elements` (fields: basic/params/all/[names], geometry) |
| `select_elements` / `zoom_to_elements` | `select` / `zoom_to` |
| `open_view` / `export_views` | `open_view`, `capture_view`, `export_pdf`, `export_dwg`, `export_image` |
| pyRevit write scripts | typed write tools (`set_parameters`, `change_type`, `duplicate_type`, `create_*`, ...) or `execute_csharp(mode="write")` |

## Rules for Revit edits
- Each write call = one Ctrl+Z in Revit. Use `dry_run=true` to preview.
- Never `synchronize_with_central`, `delete_elements`, `relinquish`, or `save_as` over an existing file without explicit instruction in the current chat (`confirm=true` is only for that case).
- Never run write tests against a real project model — use `new_document` (scratch from `TEMPLATE CC PABLO.rte`) or a copy.
- Revit ElementId ↔ Robot bar map is stored in shared parameter **`CC_Robot_Id`** (Text, instance, group "Analysis").
- 0122 Barbieri: UP TO DATE governs openings; NEW FRAMING governs framing details.
- Deliverables: "Prepared for review by Brennen Bourgois, P.E." Never Pablo's name or a PE number in a seal block.
