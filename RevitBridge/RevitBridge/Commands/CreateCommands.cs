using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using RevitBridge.Bridge;
using RevitBridge.Services;

namespace RevitBridge.Commands
{
    /// <summary>Element creation. All run inside the per-call TransactionGroup (one undo).</summary>
    internal static class CreateCommands
    {
        private static JObject Created(IEnumerable<ElementId> ids, JObject extra = null)
        {
            var list = ids.ToList();
            var o = new JObject { ["ids"] = Describe.Ids(list), ["count"] = list.Count };
            if (extra != null) foreach (var kv in extra) o[kv.Key] = kv.Value;
            return o;
        }

        private static void Activate(FamilySymbol s)
        {
            if (!s.IsActive) { s.Activate(); s.Document.Regenerate(); }
        }

        // ── Datum ────────────────────────────────────────────────────────────

        /// <summary>levels: [{name?, elevation}] (elevation in feet or unit string).</summary>
        [BridgeCommand("create_levels", CommandKind.Write)]
        public static object CreateLevels(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var specs = ctx.Arr("levels");
            if (specs.Count == 0) throw new BridgeException("BAD_PARAMS", "Pass levels: [{name, elevation}].");
            var makePlans = ctx.Bool("create_plan_views", true);
            var ids = ctx.Tx("Create levels", () =>
            {
                var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .FirstOrDefault(v => v.ViewFamily == ViewFamily.FloorPlan);
                var list = new List<ElementId>();
                foreach (var s in specs)
                {
                    var lvl = Level.Create(doc, UnitParser.ToFeet(s["elevation"]));
                    var name = (string)s["name"];
                    if (!string.IsNullOrEmpty(name)) lvl.Name = name;
                    list.Add(lvl.Id);
                    if (makePlans && vft != null) ViewPlan.Create(doc, vft.Id, lvl.Id);
                }
                return list;
            });
            return Created(ids);
        }

        /// <summary>grids: [{name?, start, end}].</summary>
        [BridgeCommand("create_grids", CommandKind.Write)]
        public static object CreateGrids(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var specs = ctx.Arr("grids");
            if (specs.Count == 0) throw new BridgeException("BAD_PARAMS", "Pass grids: [{name, start, end}].");
            var ids = ctx.Tx("Create grids", () =>
            {
                var list = new List<ElementId>();
                foreach (var s in specs)
                {
                    var g = Grid.Create(doc, CommandContext.ToLine(s));
                    var name = (string)s["name"];
                    if (!string.IsNullOrEmpty(name)) g.Name = name;
                    list.Add(g.Id);
                }
                return list;
            });
            return Created(ids);
        }

        // ── Walls, floors, roofs ─────────────────────────────────────────────

        /// <summary>
        /// Walls from explicit lines ("curves": [{start,end}]) or a polyline ("points", optional "closed").
        /// Height from "top_level" (top constraint) or "height" (unconnected, default 10').
        /// </summary>
        [BridgeCommand("create_walls", CommandKind.Write)]
        public static object CreateWalls(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var level = ctx.LevelParam("level");
            var wallType = ctx.Has("type")
                ? Lookup.Type<WallType>(doc, ctx.Str("type"))
                : new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(w => w.Kind == WallKind.Basic);
            var top = ctx.LevelOpt("top_level");
            var height = ctx.LengthOpt("height") ?? 10.0;
            var baseOffset = ctx.LengthOpt("base_offset") ?? 0.0;
            var topOffset = ctx.LengthOpt("top_offset") ?? 0.0;
            var flip = ctx.Bool("flip", false);
            var structural = ctx.Bool("structural", false);
            var z = level.Elevation;

            var curves = new List<Curve>();
            if (ctx.Has("curves"))
                curves.AddRange(ctx.Arr("curves").Select(t => (Curve)CommandContext.ToLine(t, z)));
            else if (ctx.Has("points"))
            {
                var pts = ctx.Arr("points").Select(p => CommandContext.ToXyz(p, z)).ToList();
                if (pts.Count < 2) throw new BridgeException("BAD_PARAMS", "points needs at least 2 points.");
                for (int i = 0; i + 1 < pts.Count; i++) curves.Add(Line.CreateBound(pts[i], pts[i + 1]));
                if (ctx.Bool("closed", false) && pts.Count > 2 && !pts[0].IsAlmostEqualTo(pts[pts.Count - 1]))
                    curves.Add(Line.CreateBound(pts[pts.Count - 1], pts[0]));
            }
            else throw new BridgeException("BAD_PARAMS", "Pass 'curves' (list of {start,end}) or 'points' (polyline).");
            ctx.GuardSize(curves.Count, "create_walls");

            var ids = ctx.Tx("Create walls", () =>
            {
                var created = new List<ElementId>();
                foreach (var c in curves)
                {
                    var w = Wall.Create(doc, c, wallType.Id, level.Id, height, baseOffset, flip, structural);
                    if (top != null)
                    {
                        w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(top.Id);
                        w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).Set(topOffset);
                    }
                    created.Add(w.Id);
                }
                return created;
            });
            return Created(ids, new JObject { ["type"] = $"{wallType.FamilyName}: {wallType.Name}", ["level"] = level.Name });
        }

        /// <summary>Floor from a closed boundary (points or lines). Optional offset from level.</summary>
        [BridgeCommand("create_floor", CommandKind.Write)]
        public static object CreateFloor(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var level = ctx.LevelParam("level");
            var type = ctx.Has("type")
                ? Lookup.Type<FloorType>(doc, ctx.Str("type"))
                : new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First(f => f.Category?.Id.AsLong() == (long)BuiltInCategory.OST_Floors);
            var loop = CommandContext.ToLoop(ctx.Tok("boundary"), level.Elevation);
            var offset = ctx.LengthOpt("offset");
            var id = ctx.Tx("Create floor", () =>
            {
                var f = Floor.Create(doc, new List<CurveLoop> { loop }, type.Id, level.Id, ctx.Bool("structural", false), null, 0);
                if (offset.HasValue) f.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(offset.Value);
                return f.Id;
            });
            return Created(new[] { id }, new JObject { ["type"] = $"{type.FamilyName}: {type.Name}" });
        }

        /// <summary>
        /// Footprint roof. boundary = closed points/lines; slope_deg applies to every edge unless
        /// sloped_edges (0-based edge indexes) limits it (e.g. gable: [0,2]).
        /// </summary>
        [BridgeCommand("create_roof_footprint", CommandKind.Write)]
        public static object CreateRoofFootprint(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var level = ctx.LevelParam("level");
            var type = ctx.Has("type")
                ? Lookup.Type<RoofType>(doc, ctx.Str("type"))
                : new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().First();
            var loop = CommandContext.ToLoop(ctx.Tok("boundary"), level.Elevation);
            var slope = ctx.Has("slope_deg") ? ctx.Dbl("slope_deg", 0) * Math.PI / 180 : (double?)null;
            var sloped = ctx.Has("sloped_edges") ? new HashSet<int>(ctx.Arr("sloped_edges").Select(t => (int)t)) : null;
            var offset = ctx.LengthOpt("base_offset");
            var overhang = ctx.LengthOpt("overhang");
            // Overhang: Revit only supports per-edge overhang on pick-walls roofs, so for a
            // sketched footprint we offset the whole boundary outward instead.
            var original = loop.Select(c => c).ToList(); // sloped_edges index into the edges as given
            if (!loop.IsCounterclockwise(XYZ.BasisZ)) loop.Flip();
            if (overhang.HasValue && Math.Abs(overhang.Value) > 1e-9)
                loop = CurveLoop.CreateViaOffset(loop, overhang.Value, XYZ.BasisZ);
            var id = ctx.Tx("Create roof", () =>
            {
                var ca = new CurveArray();
                foreach (var c in loop) ca.Append(c);
                // The mapping array must be pre-allocated: Revit fills it (C++/CLI by-ref), it doesn't create it.
                var mca = new ModelCurveArray();
                var roof = doc.Create.NewFootPrintRoof(ca, level, type, out mca);
                foreach (ModelCurve mc in mca)
                {
                    bool defines = slope.HasValue && (sloped == null || sloped.Contains(OriginalEdgeIndex(original, mc.GeometryCurve)));
                    roof.set_DefinesSlope(mc, defines);
                    if (defines) roof.set_SlopeAngle(mc, Math.Tan(slope.Value));
                }
                if (offset.HasValue) roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.Set(offset.Value);
                return roof.Id;
            });
            return Created(new[] { id }, new JObject { ["type"] = $"{type.FamilyName}: {type.Name}" });
        }

        /// <summary>The given edge that is parallel to <paramref name="c"/> and nearest to it (flip/offset keep edges parallel).</summary>
        private static int OriginalEdgeIndex(List<Curve> original, Curve c)
        {
            XYZ Dir(Curve k) => (k.GetEndPoint(1) - k.GetEndPoint(0)).Normalize();
            XYZ Mid(Curve k) => (k.GetEndPoint(0) + k.GetEndPoint(1)) / 2;
            var d = Dir(c); var m = Mid(c);
            int best = -1; double bestDist = double.MaxValue;
            for (int i = 0; i < original.Count; i++)
            {
                if (Math.Abs(Math.Abs(Dir(original[i]).DotProduct(d)) - 1) > 1e-6) continue;
                var dist = new XYZ(Mid(original[i]).X - m.X, Mid(original[i]).Y - m.Y, 0).GetLength();
                if (dist < bestDist) { bestDist = dist; best = i; }
            }
            return best;
        }

        // ── Family instances ─────────────────────────────────────────────────

        private static StructuralType ParseStructuralType(string s)
        {
            if (string.IsNullOrEmpty(s)) return StructuralType.NonStructural;
            if (Enum.TryParse(s.Replace("_", ""), true, out StructuralType st)) return st;
            throw new BridgeException("BAD_PARAMS", $"structural_type '{s}' invalid. Use NonStructural, Beam, Brace, Column, Footing, UnknownFraming.");
        }

        /// <summary>
        /// Point-based families (doors, windows, columns, foundations, generic, furniture).
        /// placements: [{point, rotation?, host?}] or points: [[x,y,z],...] with shared rotation/host.
        /// Doors/windows: pass host (wall id) or the nearest wall at the point on the level is used.
        /// </summary>
        [BridgeCommand("place_family_instances", CommandKind.Write)]
        public static object PlaceFamilyInstances(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var key = ctx.Has("family") && ctx.Has("type") ? $"{ctx.Str("family")}: {ctx.Str("type")}" : ctx.Str("type") ?? ctx.StrReq("family");
            var symbol = Lookup.Symbol(doc, key);
            var level = ctx.LevelOpt("level");
            var st = ParseStructuralType(ctx.Str("structural_type"));
            var shared = new JObject { ["rotation"] = ctx.Tok("rotation"), ["host"] = ctx.Tok("host") };
            var specs = ctx.Has("placements")
                ? ctx.Arr("placements").Cast<JObject>().ToList()
                : ctx.Arr("points").Select(p => new JObject { ["point"] = p, ["rotation"] = shared["rotation"], ["host"] = shared["host"] }).ToList();
            if (specs.Count == 0) throw new BridgeException("BAD_PARAMS", "Pass placements [{point, rotation?, host?}] or points.");
            ctx.GuardSize(specs.Count, "place_family_instances");
            var cat = symbol.Category?.Id.AsLong();
            bool wallHosted = cat == (long)BuiltInCategory.OST_Doors || cat == (long)BuiltInCategory.OST_Windows;

            var ids = ctx.Tx("Place family instances", () =>
            {
                Activate(symbol);
                var list = new List<ElementId>();
                foreach (var s in specs)
                {
                    var pt = CommandContext.ToXyz(s["point"], level?.Elevation ?? 0);
                    Element host = s["host"] != null && s["host"].Type != JTokenType.Null ? ctx.Element(s["host"]) : null;
                    if (host == null && wallHosted) host = NearestWall(doc, pt, level);
                    FamilyInstance fi;
                    if (host != null)
                        fi = doc.Create.NewFamilyInstance(pt, symbol, host, level ?? doc.GetElement(host.LevelId) as Level, st);
                    else if (level != null)
                        fi = doc.Create.NewFamilyInstance(pt, symbol, level, st);
                    else
                        fi = doc.Create.NewFamilyInstance(pt, symbol, st);
                    var rot = s["rotation"];
                    if (rot != null && rot.Type != JTokenType.Null)
                    {
                        var ang = UnitParser.ToRadians(rot);
                        if (Math.Abs(ang) > 1e-9)
                            ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(pt, pt + XYZ.BasisZ), ang);
                    }
                    list.Add(fi.Id);
                }
                return list;
            });
            return Created(ids, new JObject { ["type"] = $"{symbol.FamilyName}: {symbol.Name}" });
        }

        private static Wall NearestWall(Document doc, XYZ pt, Level level)
        {
            var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>();
            if (level != null) walls = walls.Where(w => w.LevelId == level.Id);
            Wall best = null; double bestD = 3.0; // within 3 ft
            foreach (var w in walls)
            {
                if (!(w.Location is LocationCurve lc)) continue;
                var flat = new XYZ(pt.X, pt.Y, lc.Curve.GetEndPoint(0).Z);
                var d = lc.Curve.Distance(flat);
                if (d < bestD) { bestD = d; best = w; }
            }
            return best ?? throw new BridgeException("NOT_FOUND", $"No wall within 3 ft of {pt} to host the door/window.", "Pass host=<wall id>.");
        }

        /// <summary>Beams: lines [{start,end}] on a reference level; z_justification top|center|origin|bottom.</summary>
        [BridgeCommand("create_beams", CommandKind.Write)]
        public static object CreateBeams(CommandContext ctx) => CreateFraming(ctx, StructuralType.Beam, "Create beams");

        [BridgeCommand("create_braces", CommandKind.Write)]
        public static object CreateBraces(CommandContext ctx) => CreateFraming(ctx, StructuralType.Brace, "Create braces");

        private static object CreateFraming(CommandContext ctx, StructuralType st, string txName)
        {
            var doc = ctx.Doc;
            var symbol = Lookup.Symbol(doc, ctx.StrReq("type"), BuiltInCategory.OST_StructuralFraming);
            var level = ctx.LevelParam("level");
            var lines = ctx.Arr("lines");
            if (lines.Count == 0) throw new BridgeException("BAD_PARAMS", "Pass lines: [{start, end}].");
            ctx.GuardSize(lines.Count, txName);
            var zj = ctx.Str("z_justification");
            var ids = ctx.Tx(txName, () =>
            {
                Activate(symbol);
                var list = new List<ElementId>();
                foreach (var l in lines)
                {
                    var fi = doc.Create.NewFamilyInstance(CommandContext.ToLine(l, level.Elevation), symbol, level, st);
                    if (zj != null)
                    {
                        var map = new Dictionary<string, int> { ["top"] = 0, ["center"] = 1, ["origin"] = 2, ["bottom"] = 3 };
                        if (!map.TryGetValue(zj.ToLowerInvariant(), out var v))
                            throw new BridgeException("BAD_PARAMS", "z_justification must be top, center, origin or bottom.");
                        fi.get_Parameter(BuiltInParameter.Z_JUSTIFICATION)?.Set(v);
                    }
                    list.Add(fi.Id);
                }
                return list;
            });
            return Created(ids, new JObject { ["type"] = $"{symbol.FamilyName}: {symbol.Name}", ["level"] = level.Name });
        }

        /// <summary>Vertical structural columns at points from base_level to top_level.</summary>
        [BridgeCommand("create_columns", CommandKind.Write)]
        public static object CreateColumns(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var symbol = Lookup.Symbol(doc, ctx.StrReq("type"));
            var baseLevel = ctx.LevelParam("base_level");
            var topLevel = ctx.LevelOpt("top_level");
            var topOffset = ctx.LengthOpt("top_offset");
            var baseOffset = ctx.LengthOpt("base_offset");
            var pts = ctx.Arr("points");
            if (pts.Count == 0) throw new BridgeException("BAD_PARAMS", "Pass points: [[x,y], ...].");
            ctx.GuardSize(pts.Count, "create_columns");
            var st = symbol.Category?.Id.AsLong() == (long)BuiltInCategory.OST_StructuralColumns ? StructuralType.Column : StructuralType.NonStructural;
            var ids = ctx.Tx("Create columns", () =>
            {
                Activate(symbol);
                var list = new List<ElementId>();
                foreach (var p in pts)
                {
                    var fi = doc.Create.NewFamilyInstance(CommandContext.ToXyz(p, baseLevel.Elevation), symbol, baseLevel, st);
                    if (topLevel != null) fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(topLevel.Id);
                    if (topOffset.HasValue) fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.Set(topOffset.Value);
                    if (baseOffset.HasValue) fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.Set(baseOffset.Value);
                    list.Add(fi.Id);
                }
                return list;
            });
            return Created(ids, new JObject { ["type"] = $"{symbol.FamilyName}: {symbol.Name}" });
        }

        // ── Views & sheets ───────────────────────────────────────────────────

        private static ViewFamilyType Vft(Document doc, ViewFamily fam, string name = null)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().Where(v => v.ViewFamily == fam).ToList();
            var hit = name == null ? all.FirstOrDefault() : all.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            return hit ?? throw new BridgeException("NOT_FOUND", $"No view type for {fam}" + (name != null ? $" named '{name}'" : "") + ".",
                "Available: " + string.Join(", ", all.Select(v => v.Name)));
        }

        /// <summary>
        /// kind: plan | ceiling | structural_plan | section | elevation | 3d | drafting.
        /// plan/ceiling/structural_plan: level. section: box {min,max} + optional direction ([x,y] view direction).
        /// elevation: point + plan_view (host plan) + index (0..3 = W,N,E,S facing). name, scale, template optional.
        /// </summary>
        [BridgeCommand("create_view", CommandKind.Write)]
        public static object CreateView(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var kind = ctx.StrReq("kind").ToLowerInvariant();
            var vftName = ctx.Str("view_type");
            var id = ctx.Tx("Create view", () =>
            {
                View v;
                switch (kind)
                {
                    case "plan": case "floor_plan":
                        v = ViewPlan.Create(doc, Vft(doc, ViewFamily.FloorPlan, vftName).Id, ctx.LevelParam("level").Id); break;
                    case "ceiling": case "ceiling_plan":
                        v = ViewPlan.Create(doc, Vft(doc, ViewFamily.CeilingPlan, vftName).Id, ctx.LevelParam("level").Id); break;
                    case "structural_plan":
                        v = ViewPlan.Create(doc, Vft(doc, ViewFamily.StructuralPlan, vftName).Id, ctx.LevelParam("level").Id); break;
                    case "3d":
                        v = View3D.CreateIsometric(doc, Vft(doc, ViewFamily.ThreeDimensional, vftName).Id); break;
                    case "drafting":
                        v = ViewDrafting.Create(doc, Vft(doc, ViewFamily.Drafting, vftName).Id); break;
                    case "section":
                        v = ViewSection.CreateSection(doc, Vft(doc, ViewFamily.Section, vftName).Id, SectionBox(ctx)); break;
                    case "elevation":
                    {
                        var plan = Lookup.View(doc, ctx.StrReq("plan_view"));
                        var marker = ElevationMarker.CreateElevationMarker(doc, Vft(doc, ViewFamily.Elevation, vftName).Id, ctx.Point("point"), ctx.Int("scale", 96));
                        v = marker.CreateElevation(doc, plan.Id, ctx.Int("index", 1));
                        break;
                    }
                    default:
                        throw new BridgeException("BAD_PARAMS", "kind must be plan, ceiling, structural_plan, section, elevation, 3d or drafting.");
                }
                if (ctx.Has("name")) v.Name = ctx.Str("name");
                if (ctx.Has("scale") && kind != "3d") v.Scale = ctx.Int("scale", 96);
                if (ctx.Has("template")) v.ViewTemplateId = Lookup.View(doc, ctx.Str("template")).Id;
                return v.Id;
            });
            var view = (View)doc.GetElement(id);
            return new JObject { ["id"] = id.AsLong(), ["name"] = view.Name, ["type"] = view.ViewType.ToString() };
        }

        /// <summary>Section box from {min,max} (a model-space box) looking along "direction" (default +Y).</summary>
        private static BoundingBoxXYZ SectionBox(CommandContext ctx)
        {
            var box = ctx.Tok("box") as JObject ?? throw new BridgeException("BAD_PARAMS", "section needs box: {min:[x,y,z], max:[x,y,z]}.");
            var min = CommandContext.ToXyz(box["min"]);
            var max = CommandContext.ToXyz(box["max"]);
            var dir = ctx.Has("direction") ? CommandContext.ToXyz(ctx.Tok("direction")).Normalize() : XYZ.BasisY;
            dir = new XYZ(dir.X, dir.Y, 0).Normalize();
            var center = (min + max) / 2;
            var up = XYZ.BasisZ;
            var right = up.CrossProduct(dir); // viewer right-hand when looking along dir
            var t = Transform.Identity;
            t.Origin = center; t.BasisX = right; t.BasisY = up; t.BasisZ = dir;
            // half extents projected into the section frame
            var half = (max - min) / 2;
            double w = Math.Abs(half.X * right.X) + Math.Abs(half.Y * right.Y);
            double d = Math.Abs(half.X * dir.X) + Math.Abs(half.Y * dir.Y);
            return new BoundingBoxXYZ
            {
                Transform = t,
                Min = new XYZ(-Math.Max(w, 1), -Math.Max(half.Z, 1), -Math.Max(d, 0.5)),
                Max = new XYZ(Math.Max(w, 1), Math.Max(half.Z, 1), Math.Max(d, 0.5)),
            };
        }

        /// <summary>option: duplicate | with_detailing | as_dependent.</summary>
        [BridgeCommand("duplicate_view", CommandKind.Write)]
        public static object DuplicateView(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var v = Lookup.View(doc, ctx.Str("view") ?? ctx.StrReq("id"));
            var opt = (ctx.Str("option", "duplicate")).ToLowerInvariant();
            var o = opt == "with_detailing" ? ViewDuplicateOption.WithDetailing : opt == "as_dependent" ? ViewDuplicateOption.AsDependent : ViewDuplicateOption.Duplicate;
            if (!v.CanViewBeDuplicated(o)) throw new BridgeException("UNSUPPORTED", $"View '{v.Name}' cannot be duplicated with {opt}.");
            var id = ctx.Tx("Duplicate view", () =>
            {
                var nid = v.Duplicate(o);
                if (ctx.Has("name")) ((View)doc.GetElement(nid)).Name = ctx.Str("name");
                return nid;
            });
            return new JObject { ["id"] = id.AsLong(), ["name"] = doc.GetElement(id).Name };
        }

        [BridgeCommand("create_sheet", CommandKind.Write)]
        public static object CreateSheet(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var tb = ctx.Has("titleblock")
                ? Lookup.Symbol(doc, ctx.Str("titleblock"), BuiltInCategory.OST_TitleBlocks)
                : new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().Cast<FamilySymbol>().FirstOrDefault();
            var id = ctx.Tx("Create sheet", () =>
            {
                var s = ViewSheet.Create(doc, tb?.Id ?? ElementId.InvalidElementId);
                if (ctx.Has("number")) s.SheetNumber = ctx.Str("number");
                if (ctx.Has("name")) s.Name = ctx.Str("name");
                return s.Id;
            });
            var sheet = (ViewSheet)doc.GetElement(id);
            return new JObject { ["id"] = id.AsLong(), ["number"] = sheet.SheetNumber, ["name"] = sheet.Name, ["titleblock"] = tb == null ? null : $"{tb.FamilyName}: {tb.Name}" };
        }

        /// <summary>Place a view or schedule on a sheet. point = sheet coordinates in feet (default sheet center).</summary>
        [BridgeCommand("place_viewport", CommandKind.Write)]
        public static object PlaceViewport(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var sheet = Lookup.Sheet(doc, ctx.StrReq("sheet"));
            var view = Lookup.View(doc, ctx.StrReq("view"));
            var pt = ctx.PointOpt("point");
            if (pt == null)
            {
                var o = sheet.Outline;
                pt = new XYZ((o.Min.U + o.Max.U) / 2, (o.Min.V + o.Max.V) / 2, 0);
            }
            var id = ctx.Tx("Place viewport", () =>
            {
                if (view is ViewSchedule) return ScheduleSheetInstance.Create(doc, sheet.Id, view.Id, pt).Id;
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                    throw new BridgeException("UNSUPPORTED", $"View '{view.Name}' cannot be placed on sheet {sheet.SheetNumber} (already placed, or empty).");
                var vp = Viewport.Create(doc, sheet.Id, view.Id, pt)
                         ?? throw new BridgeException("UNSUPPORTED", $"Revit refused to place '{view.Name}' on {sheet.SheetNumber}. Empty views cannot be placed on sheets.",
                                "Add content to the view (model elements, annotation) first.");
                return vp.Id;
            });
            return new JObject { ["id"] = id.AsLong(), ["sheet"] = sheet.SheetNumber, ["view"] = view.Name };
        }

        /// <summary>
        /// Schedule for a category. fields: [names], filters: [{field, op, value}],
        /// sorting: [{field, descending}], itemize (default true).
        /// </summary>
        [BridgeCommand("create_schedule", CommandKind.Write)]
        public static object CreateSchedule(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var cat = Lookup.Category(doc, ctx.StrReq("category"));
            var id = ctx.Tx("Create schedule", () =>
            {
                var vs = ViewSchedule.CreateSchedule(doc, cat.Id);
                if (ctx.Has("name")) vs.Name = ctx.Str("name");
                var def = vs.Definition;
                var schedulable = def.GetSchedulableFields();
                var added = new Dictionary<string, ScheduleFieldId>(StringComparer.OrdinalIgnoreCase);
                ScheduleFieldId AddField(string n)
                {
                    if (added.TryGetValue(n, out var existing)) return existing;
                    var sf = schedulable.FirstOrDefault(f => string.Equals(f.GetName(doc), n, StringComparison.OrdinalIgnoreCase))
                             ?? throw new BridgeException("NOT_FOUND", $"Field '{n}' is not schedulable for {cat.Name}.",
                                    "Some fields: " + string.Join(", ", schedulable.Take(40).Select(f => f.GetName(doc))));
                    var fid = def.AddField(sf).FieldId;
                    added[n] = fid;
                    return fid;
                }
                foreach (var n in ctx.StrList("fields")) AddField(n);
                foreach (var f in ctx.Arr("filters").OfType<JObject>())
                {
                    var fid = AddField((string)f["field"]);
                    var ft = ParseFilterType((string)f["op"] ?? "eq");
                    var val = f["value"];
                    ScheduleFilter sfl;
                    if (val == null || val.Type == JTokenType.Null) sfl = new ScheduleFilter(fid, ft);
                    else if (val.Type == JTokenType.Integer) sfl = new ScheduleFilter(fid, ft, val.Value<int>());
                    else if (val.Type == JTokenType.Float) sfl = new ScheduleFilter(fid, ft, val.Value<double>());
                    else sfl = new ScheduleFilter(fid, ft, val.ToString());
                    def.AddFilter(sfl);
                }
                foreach (var s in ctx.Arr("sorting").OfType<JObject>())
                    def.AddSortGroupField(new ScheduleSortGroupField(AddField((string)s["field"]),
                        (bool?)s["descending"] == true ? ScheduleSortOrder.Descending : ScheduleSortOrder.Ascending));
                def.IsItemized = ctx.Bool("itemize", true);
                return vs.Id;
            });
            return new JObject { ["id"] = id.AsLong(), ["name"] = doc.GetElement(id).Name };
        }

        private static ScheduleFilterType ParseFilterType(string op)
        {
            switch (op.ToLowerInvariant())
            {
                case "eq": return ScheduleFilterType.Equal;
                case "ne": return ScheduleFilterType.NotEqual;
                case "gt": return ScheduleFilterType.GreaterThan;
                case "ge": return ScheduleFilterType.GreaterThanOrEqual;
                case "lt": return ScheduleFilterType.LessThan;
                case "le": return ScheduleFilterType.LessThanOrEqual;
                case "contains": return ScheduleFilterType.Contains;
                case "not_contains": return ScheduleFilterType.NotContains;
                case "begins_with": return ScheduleFilterType.BeginsWith;
                case "has_value": return ScheduleFilterType.HasValue;
                case "no_value": return ScheduleFilterType.HasNoValue;
                default: throw new BridgeException("BAD_PARAMS", $"Unknown schedule filter op '{op}'.");
            }
        }

        // ── Annotation ───────────────────────────────────────────────────────

        [BridgeCommand("create_text_note", CommandKind.Write)]
        public static object CreateTextNote(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var view = ctx.Has("view") ? Lookup.View(doc, ctx.Str("view"))
                     : ctx.IsActiveDoc ? ctx.UIApp.ActiveUIDocument.ActiveView : throw new BridgeException("BAD_PARAMS", "Pass view.");
            var type = ctx.Has("type") ? Lookup.Type<TextNoteType>(doc, ctx.Str("type"))
                     : doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)) as TextNoteType;
            var id = ctx.Tx("Create text note", () =>
            {
                var tn = ctx.Has("width")
                    ? TextNote.Create(doc, view.Id, ctx.Point("point"), ctx.Length("width"), ctx.StrReq("text"), type.Id)
                    : TextNote.Create(doc, view.Id, ctx.Point("point"), ctx.StrReq("text"), type.Id);
                return tn.Id;
            });
            return new JObject { ["id"] = id.AsLong(), ["view"] = view.Name };
        }

        /// <summary>Tag elements by category in a view. ids, view, leader?, orientation horizontal|vertical, offset [dx,dy].</summary>
        [BridgeCommand("create_tag", CommandKind.Write)]
        public static object CreateTag(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var view = Lookup.View(doc, ctx.StrReq("view"));
            var leader = ctx.Bool("leader", false);
            var orient = string.Equals(ctx.Str("orientation"), "vertical", StringComparison.OrdinalIgnoreCase) ? TagOrientation.Vertical : TagOrientation.Horizontal;
            var offset = ctx.PointOpt("offset") ?? XYZ.Zero;
            var tagType = ctx.Has("tag_type") ? Lookup.Symbol(doc, ctx.Str("tag_type")) : null;
            var els = ctx.Elements("ids");
            var ids = ctx.Tx("Create tags", () =>
            {
                var list = new List<ElementId>();
                foreach (var e in els)
                {
                    var bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
                    var pt = e.Location is LocationPoint lp ? lp.Point
                           : e.Location is LocationCurve lc ? lc.Curve.Evaluate(0.5, true)
                           : bb != null ? (bb.Min + bb.Max) / 2 : XYZ.Zero;
                    var tag = tagType != null
                        ? IndependentTag.Create(doc, tagType.Id, view.Id, new Reference(e), leader, orient, pt + offset)
                        : IndependentTag.Create(doc, view.Id, new Reference(e), leader, TagMode.TM_ADDBY_CATEGORY, orient, pt + offset);
                    list.Add(tag.Id);
                }
                return list;
            });
            return Created(ids);
        }

        /// <summary>
        /// Best-effort linear dimension through references of grids, levels, reference planes,
        /// detail/model lines, or walls (wall centreline side faces are used). line = {start,end} in the view.
        /// </summary>
        [BridgeCommand("create_dimension", CommandKind.Write)]
        public static object CreateDimension(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var view = Lookup.View(doc, ctx.StrReq("view"));
            var line = CommandContext.ToLine(ctx.Tok("line"));
            var ra = new ReferenceArray();
            foreach (var e in ctx.Elements("ids"))
            {
                switch (e)
                {
                    case Grid g: ra.Append(new Reference(g)); break;
                    case Level l: ra.Append(new Reference(l)); break;
                    case ReferencePlane rp: ra.Append(rp.GetReference()); break;
                    case CurveElement ce: ra.Append(ce.GeometryCurve.Reference ?? new Reference(ce)); break;
                    case Wall w:
                        var side = HostObjectUtils.GetSideFaces(w, ShellLayerType.Exterior).FirstOrDefault();
                        if (side == null) throw new BridgeException("UNSUPPORTED", $"Wall {w.Id.AsLong()} has no exterior face reference.");
                        ra.Append(side);
                        break;
                    default:
                        throw new BridgeException("UNSUPPORTED", $"Element {e.Id.AsLong()} ({e.GetType().Name}) is not supported for dimensioning. Use grids, levels, reference planes, lines or walls.");
                }
            }
            if (ra.Size < 2) throw new BridgeException("BAD_PARAMS", "A dimension needs at least 2 references.");
            var id = ctx.Tx("Create dimension", () =>
            {
                var dim = doc.Create.NewDimension(view, line, ra)
                          ?? throw new BridgeException("REVIT_ERROR", "Revit could not create the dimension with these references.");
                return dim.Id;
            });
            var d = (Dimension)doc.GetElement(id);
            return new JObject { ["id"] = id.AsLong(), ["value"] = d.Value, ["display"] = d.ValueString };
        }
    }
}
