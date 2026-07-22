# Precast Lintel Placer — Plugin & Family Specification

**Module:** Construction Corps Revit Plugin Suite
**Target:** Revit 2025 / 2026 / 2027, .NET 8
**Status:** Design spec — ready to build
**Engineering basis:** FBC 2023 (9th Ed.), TMS 402-22, ASCE 7-22, Florida precast lintel conventions (Cast-Crete / Oldcastle)

---

## 1. Problem

Construction Corps permit sets require precast concrete lintels to be called out in plan with their nominal length (e.g. `L86` = 86" cast piece). Standard precast bearing in Florida is 8" per side, so:

```
Lintel piece length = rough opening width + 16"
```

Plotting the lintel symbol directly over the opening in plan view clutters the drawing — the callout disappears behind hatch, door swings, dimension strings, and electrical symbols. The desired behavior:

1. A precast lintel symbol (filled region with concrete + rebar pattern) is placed in plan.
2. It is graphically offset from the host opening — pushed perpendicular to the wall, outside the building footprint — for legibility.
3. Its length auto-computes from the host window/door width + 16".
4. Its label reads `L<length-in-inches>` (integer, no decimals).
5. Every window and door on the active level gets one, in a single batch operation.

---

## 2. Architecture

Two-part deliverable:

### Part A — `CCorp_PrecastLintel.rfa`

A Detail Item family. Self-contained: given an `Opening_Width` value, it sizes itself, labels itself, and draws itself at a configurable offset from its insertion point. Usable manually (place + type width) without the plugin.

### Part B — `LintelPlacerCommand.cs`

An `IExternalCommand` in the existing plugin suite. Collects all windows and doors on the active level, computes their positions and host wall orientations, places `CCorp_PrecastLintel` instances at an offset along each wall's exterior normal, rotates each one to align with its wall, and pushes the host width into `Opening_Width`.

**Why this split:** Revit out of the box cannot auto-link an arbitrary detail/model family's parameter to a host window or door's width. Cross-family parameter binding only happens via tags, schedules, or scripting. The family handles all the formula logic; the command handles all the cross-element wiring.

---

## 3. Family Specification (`CCorp_PrecastLintel.rfa`)

### 3.1 Identity

| Property | Value |
|---|---|
| Family file | `CCorp_PrecastLintel.rfa` |
| Template | `Detail Item.rft` |
| Category | Detail Items |
| Storage | `\\<office-share>\Revit\Families\Detail\Structural\` |

### 3.2 Reference plane skeleton

| Plane | Orientation | Role |
|---|---|---|
| Center (L/R) | Vertical | Insertion X — origin column |
| Center (F/B) | Horizontal | Insertion Y — origin row |
| Offset Plane | Horizontal | Sits `Lateral_Offset` below Center F/B; lintel body anchors here |
| Lintel L End | Vertical | Left edge of lintel body, symmetric about Center L/R |
| Lintel R End | Vertical | Right edge of lintel body, symmetric about Center L/R |
| Lintel Top | Horizontal | Top edge of body, `Lintel_Depth_Plan/2` above Offset Plane |
| Lintel Bottom | Horizontal | Bottom edge of body, `Lintel_Depth_Plan/2` below Offset Plane |

**Constraints:**
- EQ between L End / Center L/R / R End
- EQ between Top / Offset Plane / Bottom

### 3.3 Parameters

**Type parameters:**

| Name | Type | Default | Notes |
|---|---|---|---|
| `Bearing` | Length | `8"` | Per-side bearing; total added length = 2 × Bearing |
| `Lintel_Depth_Plan` | Length | `8"` | Plan-view thickness of the cast piece |
| `Lateral_Offset` | Length | `2'-0"` | Distance from insertion point to lintel body |

**Instance parameters:**

| Name | Type | Default | Formula |
|---|---|---|---|
| `Opening_Width` | Length | `3'-0"` | (User/script-set) |
| `Length` | Length | — | `Opening_Width + Bearing * 2` |
| `Callout_Inches` | Integer | — | `Length / 1"` |

**Why `Integer` for `Callout_Inches`:** auto-rounds to whole inches, no decimal noise on the label. The `/ 1"` syntax is how Revit converts a length parameter into a unitless number representing inches.

**Case sensitivity reminder:** parameter names in formulas are case-sensitive. `LENGTH` ≠ `Length`. Use the exact casing shown.

### 3.4 Dimension wiring

1. L End ↔ R End → label `Length`
2. Top ↔ Bottom → label `Lintel_Depth_Plan`
3. Center F/B ↔ Offset Plane → label `Lateral_Offset`

**Flex each parameter** (change the value, hit Apply, watch the planes move) before drawing geometry. Catch constraint problems now, not after.

### 3.5 Geometry

1. **Filled region** bounded by L End / R End / Top / Bottom. Lock all four edges to reference planes.
2. **Pattern:** custom drafting pattern `CCorp_Precast` (concrete stipple + rebar dots).
   - Author `.pat` in text editor; store in `<office-share>\Revit\Patterns\CCorp_Precast.pat`.
   - Import via **Manage → Additional Settings → Fill Patterns → Drafting → New → Custom → Import**.
   - Revit's built-in patterns live at `C:\Program Files\Autodesk\Revit 2027\Data\revit.pat` — reference only; don't edit (overwritten on updates).
3. **Optional leader:** thin detail line from origin (Center L/R ∩ Center F/B) to lintel body, locked at both endpoints. Visually ties the symbol to its host opening.

### 3.6 Label

- Tool: **Label** (Create tab → Text panel) — note this is only available in annotation-bearing family categories; Detail Items qualify.
- Location: centered on filled region (intersection of Center L/R and Offset Plane).
- Parameter: `Callout_Inches`.
- Prefix: `L`
- Suffix: (none)
- Text type: Arial Narrow Bold, ~3/32" (legible at 1/4"=1'-0").

Result: label reads `L52`, `L86`, `L120`, etc. Auto-updates when `Opening_Width` changes.

### 3.7 Behavior summary

- User (or plugin) clicks at the host opening's center in plan.
- Lintel symbol appears `Lateral_Offset` distance away along the family's local Y axis.
- Setting `Opening_Width` triggers: `Length` recalculates → `Callout_Inches` recalculates → label text updates → filled region resizes.

---

## 4. Plugin Specification (`LintelPlacerCommand.cs`)

### 4.1 Command identity

| Property | Value |
|---|---|
| Class | `LintelPlacerCommand` |
| Namespace | `CCorp.Revit.Commands.Structural` |
| Ribbon | CCorp Tools tab → Structural panel → "Place Lintels" |
| Transaction mode | Manual |
| Regeneration | Manual |

### 4.2 Algorithm

1. Verify active view is a floor plan. Bail with `TaskDialog` if not.
2. Locate the `CCorp_PrecastLintel` family symbol; fail gracefully if unloaded.
3. Collect `FamilyInstance` of `OST_Windows` and `OST_Doors` on the active view's level.
4. For each opening:
   - Read width via `BuiltInParameter.FAMILY_WIDTH_PARAM` with fallback to `LookupParameter("Width")`.
   - Confirm host is a wall; skip and log otherwise.
   - Get insertion point from `LocationPoint.Point`.
   - Compute exterior normal from `Wall.Orientation`.
   - Compute placement point = insertion + (normal × `Lateral_Offset`).
   - Compute rotation angle from wall curve direction.
   - Place via `doc.Create.NewFamilyInstance(point, symbol, view)`.
   - Rotate placed instance about vertical axis through its origin.
   - Set `Opening_Width` on the new instance.
5. Wrap entire loop in one `Transaction`.
6. Report placed / skipped / error counts via `TaskDialog`.

### 4.3 Skeleton

```csharp
using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace CCorp.Revit.Commands.Structural
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class LintelPlacerCommand : IExternalCommand
    {
        private const string FamilyName = "CCorp_PrecastLintel";

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            View view = doc.ActiveView;

            if (view.ViewType != ViewType.FloorPlan)
            {
                TaskDialog.Show("Lintel Placer", "Run from a floor plan view.");
                return Result.Cancelled;
            }

            // 1. Locate the lintel symbol
            FamilySymbol lintelSymbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => s.FamilyName == FamilyName);

            if (lintelSymbol == null)
            {
                TaskDialog.Show("Lintel Placer",
                    $"{FamilyName} family not loaded in project.");
                return Result.Failed;
            }

            // 2. Collect openings on this view's level
            ElementId levelId = view.GenLevel?.Id;
            if (levelId == null)
            {
                TaskDialog.Show("Lintel Placer", "View has no associated level.");
                return Result.Cancelled;
            }

            var openings = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(new[]
                {
                    BuiltInCategory.OST_Windows,
                    BuiltInCategory.OST_Doors
                }))
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .Where(fi => fi.LevelId == levelId)
                .ToList();

            int placed = 0, skipped = 0;

            using (Transaction tx = new Transaction(doc, "Place Precast Lintels"))
            {
                tx.Start();

                if (!lintelSymbol.IsActive) lintelSymbol.Activate();
                doc.Regenerate();

                // Read offset from the type
                double lateralOffset = lintelSymbol
                    .LookupParameter("Lateral_Offset")?.AsDouble()
                    ?? UnitUtils.ConvertToInternalUnits(24.0, UnitTypeId.Inches);

                foreach (var opening in openings)
                {
                    if (!(opening.Host is Wall wall))         { skipped++; continue; }
                    if (!(opening.Location is LocationPoint lp)) { skipped++; continue; }

                    double width = opening
                        .get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM)
                        ?.AsDouble() ?? 0.0;

                    if (width <= 0)
                        width = opening.Symbol
                            .LookupParameter("Width")?.AsDouble() ?? 0.0;

                    if (width <= 0) { skipped++; continue; }

                    XYZ normal  = wall.Orientation;           // exterior-facing
                    XYZ placePt = lp.Point + normal.Multiply(lateralOffset);

                    FamilyInstance lintel = doc.Create
                        .NewFamilyInstance(placePt, lintelSymbol, view);

                    // Rotate to align with wall direction
                    if ((wall.Location as LocationCurve)?.Curve is Line wallLine)
                    {
                        XYZ dir = wallLine.Direction;
                        double angle = Math.Atan2(dir.Y, dir.X);
                        Line axis = Line.CreateBound(placePt, placePt + XYZ.BasisZ);
                        ElementTransformUtils.RotateElement(doc, lintel.Id, axis, angle);
                    }
                    // (Curved walls: see Edge Cases §4.4)

                    lintel.LookupParameter("Opening_Width")?.Set(width);

                    placed++;
                }

                tx.Commit();
            }

            TaskDialog.Show("Lintel Placer",
                $"Placed: {placed}\nSkipped: {skipped}");
            return Result.Succeeded;
        }
    }
}
```

### 4.4 Edge cases

| Case | Handling |
|---|---|
| Curved walls | `(wall.Location as LocationCurve).Curve` may be `Arc`. Use `Curve.ComputeDerivatives(param, normalized: true)` at the opening's parameter to get tangent vector; rotate from there. |
| Linked-model openings | Skip — cannot place hosted detail families that reference link elements. Log to results. |
| Openings below view cut plane | Generally still appear in plan, no special handling needed. |
| Re-run on same view | Pre-purge existing `CCorp_PrecastLintel` instances on the view, or expose a "Replace existing / Append" toggle via `TaskDialog.CommandLink`. |
| Width parameter not exposed | After `FAMILY_WIDTH_PARAM` and `LookupParameter("Width")` fall through, skip and log family name to a results dialog so user can patch. |
| Sloped/inclined walls | Out of scope for v1. Skip if `Wall.Orientation.Z != 0`. |

### 4.5 Manifest entry

Add to `CCorpTools.addin`:

```xml
<AddIn Type="Command">
  <Name>Place Precast Lintels</Name>
  <Assembly>CCorp.Revit.dll</Assembly>
  <FullClassName>CCorp.Revit.Commands.Structural.LintelPlacerCommand</FullClassName>
  <ClientId>{GENERATE-A-GUID}</ClientId>
  <VendorId>CCORP</VendorId>
  <VendorDescription>Construction Corps</VendorDescription>
</AddIn>
```

---

## 5. Engineering Basis

- **Bearing length.** TMS 402-22 §9.1.4 requires a minimum 4" bearing for masonry lintels. 8" per side is the de facto Florida convention for precast U-block lintels (Cast-Crete, Oldcastle) and matches manufacturer shop drawing standards. The `Bearing` type parameter defaults to 8" but is editable per project.
- **Code reference.** FBC 2023 (9th Edition) §2104, §2106 for masonry; lintel-specific provisions in §2107 referencing TMS 402-22.
- **Wind loads.** ASCE 7-22 reactions are not auto-calculated in v1; see §6.1 for the v2 extension.
- **Naming convention.** `L<inches>` is informal but consistent with Cast-Crete-style callouts on Florida residential permit sets. Formal designations (e.g. `8F8-1B/1T`) are out of scope for the auto-label and would require a lookup table — see §6.3.

---

## 6. Future Enhancements

1. **Reaction force lookup.** Extend `Opening_Width` to also calculate factored bearing reactions per ASCE 7-22 wind load cases and write to a `Reaction_Bearing` instance parameter. Feeds the wall framing engineer's bearing check in CorpCalc.
2. **Schedule integration.** Add shared parameter `Lintel_Mark` (Text) populated as `"L" + Callout_Inches`, schedulable in a project-wide Lintel Schedule on the structural sheets.
3. **Cast-Crete designation lookup.** Small table mapping (opening width × wall type) → standard Cast-Crete part number. Populate as second label line.
4. **Dynamic Model Update (DMU) updater.** `IUpdater` subscribed to `WINDOW_WIDTH` / `DOOR_WIDTH` changes; re-runs sizing for the matching lintel automatically. Eliminates need to re-run the command after geometry edits.
5. **Header coordination with wood framing engine.** When wall is wood-framed instead of CMU, hand off to WoodFramingPlugin's header sizer instead of placing a precast lintel.
6. **Sloped wall / non-orthogonal handling.** Generalize the rotation logic to handle non-horizontal wall curves and inclined walls.

---

## 7. Open Questions

- Is `2'-0"` the right default `Lateral_Offset`, or should it scale with view scale / wall thickness?
- Should the lintel symbol appear on Roof Plan and Foundation Plan as well, or be limited to dedicated Floor Plan views?
- Cast-Crete part number lookup — defer to v2, or scope into v1?
- Should the command optionally write the placed lintels to a dedicated workset (`S-LINTELS`) for visibility control on architectural sheets?

---

## 8. Build Checklist

- [ ] Create `CCorp_PrecastLintel.rfa` from `Detail Item.rft`
- [ ] Set up reference plane skeleton per §3.2
- [ ] Add parameters per §3.3
- [ ] Wire dimensions per §3.4 and flex each parameter
- [ ] Author and import `CCorp_Precast.pat` drafting pattern
- [ ] Draw and lock filled region per §3.5
- [ ] Place and configure label per §3.6
- [ ] Save to office content library
- [ ] Scaffold `LintelPlacerCommand.cs` in plugin solution
- [ ] Implement algorithm per §4.2 using skeleton in §4.3
- [ ] Add curved-wall handling per §4.4
- [ ] Register in `CCorpTools.addin` manifest
- [ ] Test on a 1-bedroom plan (5–10 openings)
- [ ] Test on a multi-family plan (50+ openings) for performance
- [ ] Test on a project with curved exterior walls
- [ ] Document in CCorp internal SOP
