---
name: "revit-robot-roundtrip"
description: "Use when moving structural data between Revit and Robot — extracting framing/loads from Revit into Robot, or pushing Robot results (sizes, reactions, status) back into Revit."
---

# Revit ↔ Robot round trip

All Revit access goes through the `revit-bridge` tools. Lengths are feet; Robot (rsap) is SI metres/N — convert ft × 0.3048 = m. Every Revit write is one undo step ("Claude: <tool>").

1. Preflight (revit-robot-preflight) if not done this chat.
2. Confirm which Revit file is authoritative (check project notes; e.g., 0122 Barbieri: UP TO DATE governs openings, NEW FRAMING governs framing). `list_documents` / `activate_document` to target it.
3. Extract from Revit:
   - `list_analytical_members` — curve, section, material, releases, physical element, `cc_robot_id`.
   - If framing has no analytical members (Revit 2023+ doesn't make them automatically), `create_analytical_members(ids)` first — use `dry_run=true` to preview, and only on models Pablo approved for edits.
   - `query_elements` (category 'Structural Framing' / 'Structural Columns' / 'Walls', fields, geometry='location') for anything else; `list_load_cases` for loads.
   Build a structured table; show counts.
4. Build/update Robot with `rsap_*` batch tools. Keep the map Revit ElementId ↔ Robot node/bar number.
5. Run robot-analysis-run steps and checks.
6. Push back to Revit — one call per change set so each is one Ctrl+Z:
   - `set_parameters` (batch) — write `CC_Robot_Id` (bar number) and results (ratio, Design Status, reactions) to the mapped elements.
   - `duplicate_type` + `change_type` for resized members (e.g. new 'W10X22' or '2x12 SYP #2' type).
   - `CC_Robot_Id` must be bound first: `bind_shared_parameter(name="CC_Robot_Id", categories=["Structural Framing","Structural Columns"], binding="instance")` (it lives in the firm shared-parameter file, group "Analysis").
   - Use `dry_run=true` first for large change sets; anything over 5,000 elements needs `confirm=true`.
7. Verify with `get_elements` and `select` / `zoom_to` so Pablo can see the changes.
8. Report what changed in both models. Never `delete_elements` (confirm), `synchronize_with_central` or `save_as` over a file without Pablo's explicit instruction in this chat.
9. `execute_csharp` only when no typed tool fits; if the same script is needed twice, say so so it can become a typed tool.
