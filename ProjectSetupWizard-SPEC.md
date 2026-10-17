# Project Setup Wizard — Specification

> A Revit plugin command that, on a **new project created from `TEMPLATE CC PABLO`**, asks a few
> questions and configures the model skeleton (levels, default wall, roof plan, project info)
> **before any drawing happens**. All logic reads template-embedded shared parameters — **no
> hard-coded level names or elevations**.

_Last updated: 2026-08-08. Companion to the flood/wind shared-param work in the same template._

---

## 1. Design principle: template holds the max, wizard deletes down

The template (`TEMPLATE CC PABLO.rte`) ships the **maximal** level stack (3 stories). The wizard
**deletes** levels the project doesn't need. Deleting is safe and cascades to dependent views;
creating levels + views + templates at runtime is not, so we avoid it. The **one exception** is the
**loft/mezzanine**, which is create-on-demand (its position is story-relative and project-specific).

---

## 2. Shared parameters (already in the template)

Firm shared-param file: `G:\Shared drives\4-10A   Design & Planning\AXS_ARCHITECTURAL (RESOURCES)\CC_SharedParameters.txt`
Group **"Project Setup"**. Look up by **name** (like WindCalc does) — GUIDs listed for title-block / robustness.

| Param | GUID | Spec | Binds to | Values |
|---|---|---|---|---|
| `Level_Role` | 4b56194a-c3cc-4579-b175-c3c550bfc554 | Text | Levels (inst) | GRADE, FOUNDATION, STORY_FLOOR, STORY_PLATE, LOFT_FLOOR, LOFT_PLATE, RIDGE, FLOOD |
| `Story_Number` | 1d9ef137-1719-4fc8-9349-226a4c1b1085 | Integer | Levels (inst) | 0=grade, 1..N story, blank=ridge/flood |
| `Wall_Role` | 35d344f4-af53-4d3e-8e5b-ccba25443d64 | Text | Wall **types** (type) | EXTERIOR, INTERIOR, FOUNDATION |
| `View_Role` | ac86ce4d-2768-4f52-952d-910238bb9c4c | Text | Views (inst) | ROOF_PLAN (extend as needed) |
| `Number_of_Stories` | bdb23866-c3fc-44f0-8c1c-0e2b20571076 | Integer | Project Info (inst) | wizard answer |
| `Foundation_Type` | 91827bdf-b1fc-4767-ac57-a83bf3238bf0 | Text | Project Info (inst) | SLAB, STEM_WALL, CRAWL |

Also present (flood/wind, group "Wind Analysis") and consumed by WindCalc: `FLOOD_BFE_FT`,
`FLOOD_DFE_FT`, `FLOOD_LAG_FT`, `FLOOD_ZONE`, `SITE_ELEVATION_FT`. Browser grouping: `Browser Category` (group "View Data").

**Correct spec identifiers in this Revit** (WindCalc's `SharedParamWriter` has the number one wrong):
Number = `SpecTypeId.Number` (`autodesk.spec.aec:number-2.0.0`), Integer = `SpecTypeId.Int.Integer`,
Text = `SpecTypeId.String.Text` (`autodesk.spec:spec.string-2.0.0`).

---

## 3. Baseline level stack (as shipped in template)

| Level | Role | Story | Elev | Building Story |
|---|---|---|---|---|
| 00_GRADE | GRADE | 0 | -3'-0" | ref |
| 01_1ST FLOOR | STORY_FLOOR | 1 | 0'-0" | STORY |
| 02_1ST FLR TOP OF PLATE | STORY_PLATE | 1 | 8'-0" | ref |
| 03_2ND FLOOR | STORY_FLOOR | 2 | 10'-0" | STORY |
| 04_2ND FLR TOP OF PLATE | STORY_PLATE | 2 | 18'-0" | ref |
| 05_3RD FLOOR | STORY_FLOOR | 3 | 20'-0" | STORY |
| 06_3RD FLR TOP OF PLATE | STORY_PLATE | 3 | 28'-0" | ref |
| 07_TOP OF RIDGE | RIDGE | — | 35'-8" | ref |
| BFE / DFE / LAG | FLOOD | — | -1'-6" / -0'-6" / -2'-6" | ref (NAVD head) |

Pattern: **8'-0" plate height, 2'-0" floor assembly → 10'-0" floor-to-floor.**

---

## 4. Level engine (pure functions of the params)

- **N stories** → `doc.Delete(level)` for every level where `Level_Role in {STORY_FLOOR, STORY_PLATE}` and `Story_Number > N`. (1-story deletes levels with story 2 and 3.)
- **Ceiling height per story h** → for each kept story s: `STORY_PLATE(s).Elevation = STORY_FLOOR(s).Elevation + h`; ripple the next `STORY_FLOOR` = plate + assembly depth.
- **Ridge** → `RIDGE` is never deleted; keep it a fixed offset above the highest kept `STORY_PLATE`.
- **Foundation type** → SLAB collapses `00_GRADE` to `01_1ST FLOOR` elevation (or deletes grade); STEM_WALL/CRAWL keep grade below.
- **Flood** → if project is not in a flood zone, delete the `FLOOD` levels + clear the flood Project Info params.

**Roof plan** → stays associated with `RIDGE` (per firm decision 2026-08-08). Because RIDGE is never
deleted and always the top, the roof plan self-adjusts; the wizard does **not** recreate it. Views
tagged `View_Role = ROOF_PLAN`: `ROOF PLAN_EXISTING`, `ROOF PLAN_PROPOSED` (the latter is on a sheet).

---

## 5. Wall types

- Wizard's **exterior dropdown** = wall types where `Wall_Role = EXTERIOR` (20 tagged: the `Exterior - *` assemblies).
- Set the chosen one as default for the Wall tool: `doc.SetDefaultElementTypeId(ElementTypeGroup.WallType, id)`.
- **Known limitation:** 2 **stacked** exterior types (`Exterior - 8" Over 10" Concrete Wall`,
  `Exterior - Brick Over CMU w Metal Stud`) can't hold `Wall_Role` — stacked/curtain types reject
  type-param bindings. **Fallback:** name-match `Exterior - *` for stacked/curtain kinds.
- Interior = 14 tagged (`Interior - *`); Foundation = 5 (`Foundation - *`, `Retaining - *`).
- Generic/default clutter already purged from the template (68→54 types).

---

## 6. Wizard questions → actions

| # | Question | Action |
|---|---|---|
| 1 | Number of stories (1–3) | level engine delete-down; write `Number_of_Stories` |
| 2 | Ceiling height per story | set plate/floor elevations |
| 3 | Loft/mezzanine? which story | create `LOFT_FLOOR`+`LOFT_PLATE` inside that story |
| 4 | Foundation type | grade/footing levels; write `Foundation_Type` |
| 5 | Exterior wall type | set default wall type (list = `Wall_Role=EXTERIOR`) |
| 6 | Flood project? BFE/DFE/LAG | keep/purge FLOOD levels; write flood Project Info |
| 7 | Address / client / risk cat | Project Info (hand off to WindCalc) |

---

## 7. Implementation notes

- **UI:** WPF window, same pattern as WindCalc's `WindCalcWindow` / `SettingsWindow`. New external
  command in the plugins solution (sibling to WindCalc), or a pyRevit command for prototyping.
- **Trigger:** ribbon button; optionally auto-launch via `IExternalApplication` subscribing to
  `DocumentCreated`/`DocumentOpened` and detecting the template.
- **All model edits** wrapped in a single `Transaction`.
- **View templates control custom params by default** — the template already has `View_Role` and
  `Browser Category` set to *non-controlled* in all 83 view templates. Any NEW view param the wizard
  relies on must be added to `SetNonControlledTemplateParameterIds` or it'll be read-only on templated views.
- **Naming:** phase suffix convention is `_EXISTING` / `_PROPOSED` on graphic design views.
- Related WindCalc fix (separate task): `SharedParamWriter._numType` uses an invalid spec string, so
  all numeric shared params silently fail to create — see the flagged task.
