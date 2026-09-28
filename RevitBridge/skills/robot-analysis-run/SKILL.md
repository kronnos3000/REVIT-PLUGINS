---
name: "robot-analysis-run"
description: "Use when building, modifying, or analyzing a model in Robot Structural Analysis via the rsap tools — enforces build order, checks, saving, and EOR-ready reporting."
---

# Robot analysis run

Run revit-robot-preflight first if the connection hasn't been checked this chat. Geometry, sections and loads coming from Revit are read with the `revit-bridge` tools (see revit-robot-roundtrip); results go back to Revit only through `revit-bridge` (`set_parameters`, `change_type`, `duplicate_type`), never pyRevit.

## Build order
units → materials → sections → nodes (`rsap_add_nodes`) → bars/panels (`rsap_add_bars`, `rsap_add_panel`) → supports → releases/offsets → load cases with nature (`rsap_set_case_nature`) → loads → combinations (prefer `rsap_code_combinations`) → mesh if panels → `rsap_calculate` → `rsap_calculation_status`.

## Rules
- Default code basis: FBC 8th Ed. / ASCE 7-22 unless told otherwise. State wind Vult, exposure, risk category, enclosure before applying wind.
- Never invent loads or dimensions — pull from drawings/model or ask.
- Look up unknown API details with `rsap_api_interfaces` / `rsap_api_members` / `rsap_api_constants`.
- Save a new version (`_v2`, `_v3`) with `rsap_save_project` after each successful calc and before risky edits.

## Checks before reporting
- Sum of reactions (`rsap_reactions_table`) matches applied loads per case.
- Max deflection (`rsap_extreme`, `rsap_bar_deflection`) vs. limit (L/360, L/240, etc.).
- No instabilities/mechanisms in calc status.

## Output
Short table: member | governing combo | demand | capacity | ratio | pass/fail. Add `rsap_view_capture` images where helpful. Tables go to a Google Sheet. Every deliverable notes code basis, assumptions, software version, and "Prepared for review by Brennen Bourgois, P.E." Never put Pablo's name or a PE number in a seal block.