using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using RevitBridge.Bridge;
using RevitBridge.Services;

namespace RevitBridge.Commands
{
    /// <summary>
    /// Structural / analytical model (Revit 2023+ AnalyticalMember API) for the Revit ↔ Robot round trip.
    /// Load inputs: numbers are US units — force lbf, line load plf (lbf/ft), area load psf, moment lbf·ft;
    /// strings may carry units ("2.5 kip", "10 kN", "0.3 klf", "3 kN/m", "40 psf", "2 kPa", "5 kip-ft").
    /// Outputs report both Revit internal values and lbf-based numbers.
    /// </summary>
    internal static class StructuralCommands
    {
        // ── Analytical members ──────────────────────────────────────────────

        [BridgeCommand("list_analytical_members")]
        public static object ListAnalyticalMembers(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var assoc = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc);
            IEnumerable<AnalyticalMember> members = new FilteredElementCollector(doc).OfClass(typeof(AnalyticalMember)).Cast<AnalyticalMember>();
            if (ctx.Has("ids"))
            {
                var ids = new HashSet<long>(ctx.Ids("ids").Select(i => i.AsLong()));
                members = members.Where(m => ids.Contains(m.Id.AsLong()) || ids.Contains(assoc.GetAssociatedElementId(m.Id).AsLong()));
            }
            var role = ctx.Str("role");
            if (role != null) members = members.Where(m => string.Equals(m.StructuralRole.ToString(), role, StringComparison.OrdinalIgnoreCase));
            var list = members.ToList();
            var max = ctx.Int("max_results", 2000);
            return new JObject
            {
                ["total"] = list.Count,
                ["members"] = new JArray(list.Take(max).Select(m => DescribeMember(doc, m, assoc))),
            };
        }

        private static JObject DescribeMember(Document doc, AnalyticalMember m, AnalyticalToPhysicalAssociationManager assoc)
        {
            var o = new JObject { ["id"] = m.Id.AsLong(), ["role"] = m.StructuralRole.ToString() };
            try { o["curve"] = Describe.Curve(m.GetCurve()); } catch { }
            var section = doc.GetElement(m.SectionTypeId) as FamilySymbol;
            o["section"] = section == null ? null : new JObject
            {
                ["type_id"] = section.Id.AsLong(), ["family"] = section.FamilyName, ["name"] = section.Name,
                ["shape"] = m.StructuralSectionShape.ToString(),
            };
            var mat = doc.GetElement(m.MaterialId) as Material;
            o["material"] = mat == null ? null : new JObject { ["id"] = mat.Id.AsLong(), ["name"] = mat.Name, ["class"] = mat.MaterialClass };
            o["cross_section_rotation_deg"] = Math.Round(m.CrossSectionRotation * 180 / Math.PI, 6);
            o["releases"] = new JObject { ["start"] = Release(m, true), ["end"] = Release(m, false) };
            var phys = assoc.GetAssociatedElementId(m.Id);
            if (phys != null && phys != ElementId.InvalidElementId)
            {
                var pe = doc.GetElement(phys);
                o["physical"] = pe == null ? null : Describe.Basic(pe);
                var robot = pe?.LookupParameter("CC_Robot_Id");
                if (robot != null) o["cc_robot_id"] = robot.AsString();
            }
            return o;
        }

        /// <summary>
        /// Create analytical members for physical framing/columns/braces that don't have one
        /// (Revit 2023+ doesn't make them automatically): curve from the physical location
        /// (column: base→top), section type and structural material copied, role set, and the
        /// analytical↔physical association added. Elements already associated are skipped.
        /// </summary>
        [BridgeCommand("create_analytical_members", CommandKind.Write)]
        public static object CreateAnalyticalMembers(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var assoc = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc);
            var els = ctx.Elements("ids");
            var created = new JArray();
            var skipped = new JArray();
            ctx.Tx("Create analytical members", () =>
            {
                foreach (var e in els)
                {
                    if (!(e is FamilyInstance fi)) { skipped.Add(new JObject { ["id"] = e.Id.AsLong(), ["reason"] = "not a family instance" }); continue; }
                    if (assoc.HasAssociation(e.Id)) { skipped.Add(new JObject { ["id"] = e.Id.AsLong(), ["reason"] = "already associated", ["analytical_id"] = assoc.GetAssociatedElementId(e.Id).AsLong() }); continue; }
                    Curve curve;
                    var role = AnalyticalStructuralRole.StructuralRoleBeam;
                    if (fi.Location is LocationCurve lc) curve = lc.Curve;
                    else if (fi.StructuralType == StructuralType.Column && fi.Location is LocationPoint lp)
                    {
                        var bb = fi.get_BoundingBox(null);
                        curve = Line.CreateBound(new XYZ(lp.Point.X, lp.Point.Y, bb.Min.Z), new XYZ(lp.Point.X, lp.Point.Y, bb.Max.Z));
                        role = AnalyticalStructuralRole.StructuralRoleColumn;
                    }
                    else { skipped.Add(new JObject { ["id"] = e.Id.AsLong(), ["reason"] = "no usable location" }); continue; }
                    if (fi.StructuralType == StructuralType.Column) role = AnalyticalStructuralRole.StructuralRoleColumn;
                    else if (fi.StructuralType == StructuralType.Brace) role = AnalyticalStructuralRole.StructuralRoleMember;

                    var am = AnalyticalMember.Create(doc, curve);
                    if (am.IsValidStructuralRole(role)) am.StructuralRole = role;
                    if (am.IsValidSectionTypeId(fi.Symbol.Id)) am.SectionTypeId = fi.Symbol.Id;
                    var matId = fi.StructuralMaterialId;
                    if (matId != null && matId != ElementId.InvalidElementId) am.MaterialId = matId;
                    assoc.AddAssociation(am.Id, e.Id);
                    created.Add(new JObject { ["physical_id"] = e.Id.AsLong(), ["analytical_id"] = am.Id.AsLong(), ["role"] = am.StructuralRole.ToString() });
                }
            });
            return new JObject { ["created"] = created, ["skipped"] = skipped };
        }

        private static JObject Release(AnalyticalMember m, bool start)
        {
            var o = new JObject { ["type"] = m.GetReleaseType(start).ToString() };
            try
            {
                var rc = m.GetReleaseConditions().FirstOrDefault(c => c.Start == start);
                if (rc != null)
                    o["released"] = new JObject { ["fx"] = rc.Fx, ["fy"] = rc.Fy, ["fz"] = rc.Fz, ["mx"] = rc.Mx, ["my"] = rc.My, ["mz"] = rc.Mz };
            }
            catch { }
            return o;
        }

        /// <summary>
        /// ids: analytical member ids (or their physical element ids). start/end: "fixed" | "pinned" |
        /// "bending_moment" | {fx,fy,fz,mx,my,mz: bool released}.
        /// </summary>
        [BridgeCommand("set_analytical_releases", CommandKind.Write)]
        public static object SetAnalyticalReleases(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var members = ResolveMembers(ctx);
            ctx.Tx("Set analytical releases", () =>
            {
                foreach (var m in members)
                {
                    if (ctx.Has("start")) ApplyRelease(m, true, ctx.Tok("start"));
                    if (ctx.Has("end")) ApplyRelease(m, false, ctx.Tok("end"));
                }
            });
            var assoc = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc);
            return new JObject { ["members"] = new JArray(members.Select(m => DescribeMember(doc, m, assoc))) };
        }

        private static void ApplyRelease(AnalyticalMember m, bool start, JToken spec)
        {
            if (spec is JObject o)
            {
                bool B(string k) => o[k] != null && o[k].Value<bool>();
                m.SetReleaseType(start, ReleaseType.UserDefined);
                m.SetReleaseConditions(new ReleaseConditions(start, B("fx"), B("fy"), B("fz"), B("mx"), B("my"), B("mz")));
                return;
            }
            switch (spec.ToString().ToLowerInvariant().Replace("_", ""))
            {
                case "fixed": m.SetReleaseType(start, ReleaseType.Fixed); break;
                case "pinned": m.SetReleaseType(start, ReleaseType.Pinned); break;
                case "bendingmoment": m.SetReleaseType(start, ReleaseType.BendingMoment); break;
                default: throw new BridgeException("BAD_PARAMS", $"Release '{spec}' invalid. Use fixed, pinned, bending_moment or {{fx..mz: bool}}.");
            }
        }

        private static List<AnalyticalMember> ResolveMembers(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var assoc = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc);
            var list = new List<AnalyticalMember>();
            foreach (var id in ctx.Ids("ids"))
            {
                var e = doc.GetElement(id) ?? throw new BridgeException("NOT_FOUND", $"Element {id.AsLong()} not found.");
                if (e is AnalyticalMember am) { list.Add(am); continue; }
                var aid = assoc.GetAssociatedElementId(id);
                if (doc.GetElement(aid) is AnalyticalMember am2) list.Add(am2);
                else throw new BridgeException("NOT_FOUND", $"Element {id.AsLong()} has no associated analytical member.");
            }
            return list;
        }

        // ── Loads ────────────────────────────────────────────────────────────

        private static readonly Regex NumUnit = new Regex(@"^\s*(?<v>[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?)\s*(?<u>.*?)\s*$", RegexOptions.Compiled);

        private enum LoadKind { Force, Linear, Area, Moment }

        /// <summary>Converts one component (number = lbf / plf / psf / lbf·ft) to Revit internal units.</summary>
        private static double ToInternal(JToken t, LoadKind kind)
        {
            if (t == null || t.Type == JTokenType.Null) return 0;
            double v; string u = "";
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) v = t.Value<double>();
            else
            {
                var m = NumUnit.Match(t.ToString());
                if (!m.Success) throw new BridgeException("BAD_PARAMS", $"Cannot parse load value '{t}'.");
                v = double.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture);
                u = m.Groups["u"].Value.ToLowerInvariant().Replace(" ", "").Replace("·", "-").Replace("*", "-");
            }
            ForgeTypeId unit;
            switch (kind)
            {
                case LoadKind.Force:
                    unit = u == "" || u == "lb" || u == "lbf" || u == "lbs" ? UnitTypeId.PoundsForce
                         : u == "kip" || u == "kips" || u == "k" ? UnitTypeId.Kips
                         : u == "kn" ? UnitTypeId.Kilonewtons : u == "n" ? UnitTypeId.Newtons : null;
                    break;
                case LoadKind.Linear:
                    unit = u == "" || u == "plf" || u == "lb/ft" || u == "lbf/ft" ? UnitTypeId.PoundsForcePerFoot
                         : u == "klf" || u == "kip/ft" || u == "k/ft" ? UnitTypeId.KipsPerFoot
                         : u == "kn/m" ? UnitTypeId.KilonewtonsPerMeter : u == "n/m" ? UnitTypeId.NewtonsPerMeter : null;
                    break;
                case LoadKind.Area:
                    unit = u == "" || u == "psf" || u == "lb/ft2" || u == "lbf/ft2" ? UnitTypeId.PoundsForcePerSquareFoot
                         : u == "ksf" || u == "kip/ft2" ? UnitTypeId.KipsPerSquareFoot
                         : u == "kpa" || u == "kn/m2" ? UnitTypeId.KilonewtonsPerSquareMeter
                         : u == "pa" || u == "n/m2" ? UnitTypeId.NewtonsPerSquareMeter : null;
                    break;
                default:
                    unit = u == "" || u == "lb-ft" || u == "lbf-ft" || u == "ft-lb" ? UnitTypeId.PoundForceFeet
                         : u == "kip-ft" || u == "k-ft" || u == "ft-kip" ? UnitTypeId.KipFeet
                         : u == "kn-m" || u == "knm" ? UnitTypeId.KilonewtonMeters : u == "n-m" || u == "nm" ? UnitTypeId.NewtonMeters : null;
                    break;
            }
            if (unit == null) throw new BridgeException("BAD_PARAMS", $"Unknown {kind} unit '{u}' in '{t}'.");
            return UnitUtils.ConvertToInternalUnits(v, unit);
        }

        private static XYZ Vec(JToken t, LoadKind kind)
        {
            if (t == null || t.Type == JTokenType.Null) return XYZ.Zero;
            if (t is JArray a) return new XYZ(ToInternal(a[0], kind), ToInternal(a.Count > 1 ? a[1] : null, kind), ToInternal(a.Count > 2 ? a[2] : null, kind));
            if (t is JObject o) return new XYZ(ToInternal(o["x"], kind), ToInternal(o["y"], kind), ToInternal(o["z"], kind));
            throw new BridgeException("BAD_PARAMS", $"'{t}' is not a vector. Use [fx, fy, fz] (negative z = gravity).");
        }

        private static ElementId CaseId(CommandContext ctx)
        {
            if (!ctx.Has("load_case")) return null;
            var key = ctx.Str("load_case");
            var cases = new FilteredElementCollector(ctx.Doc).OfClass(typeof(LoadCase)).Cast<LoadCase>().ToList();
            var hit = cases.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase) || c.Id.AsLong().ToString() == key || c.Number.ToString() == key);
            return hit?.Id ?? throw new BridgeException("NOT_FOUND", $"Load case '{key}' not found.", "Cases: " + string.Join(", ", cases.Select(c => c.Name)));
        }

        private static void AssignCase(Element load, ElementId caseId)
        {
            if (caseId == null) return;
            var p = load.get_Parameter(BuiltInParameter.LOAD_CASE_ID);
            if (p == null || p.IsReadOnly || !p.Set(caseId)) throw new BridgeException("REVIT_ERROR", "Could not assign the load case.");
        }

        private static ElementId HostId(CommandContext ctx)
        {
            if (!ctx.Has("host")) return ElementId.InvalidElementId;
            var id = CommandContext.ToId(ctx.Tok("host"));
            var doc = ctx.Doc;
            if (doc.GetElement(id) is AnalyticalElement) return id;
            var aid = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc).GetAssociatedElementId(id);
            if (aid != null && aid != ElementId.InvalidElementId) return aid;
            throw new BridgeException("NOT_FOUND", $"Host {id.AsLong()} is not (and has no) analytical element.");
        }

        /// <summary>
        /// host (analytical member or its physical element — required: the Revit 2024+ API only creates hosted
        /// point loads), at start|end or point [x,y,z] ft on the host, force [fx,fy,fz] (lbf), moment (lbf·ft), load_case?.
        /// </summary>
        [BridgeCommand("create_point_load", CommandKind.Write)]
        public static object CreatePointLoad(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var host = HostId(ctx);
            if (host == ElementId.InvalidElementId)
                throw new BridgeException("BAD_PARAMS", "create_point_load needs host (an analytical member or its physical beam/column id).",
                    "Revit 2024+ only creates hosted point loads through the API.");
            var caseId = CaseId(ctx);
            var force = Vec(ctx.Tok("force"), LoadKind.Force);
            var moment = Vec(ctx.Tok("moment"), LoadKind.Moment);
            var id = ctx.Tx("Create point load", () =>
            {
                PointLoad pl;
                var sel = ctx.Str("at");
                if (host != ElementId.InvalidElementId && sel != null)
                    pl = PointLoad.Create(doc, host, string.Equals(sel, "start", StringComparison.OrdinalIgnoreCase) ? AnalyticalElementSelector.StartOrBase : AnalyticalElementSelector.EndOrTop, force, moment, null);
                else
                    pl = PointLoad.Create(doc, host, ctx.Point("point"), force, moment, null);
                AssignCase(pl, caseId);
                return pl.Id;
            });
            return new JObject { ["id"] = id.AsLong() };
        }

        /// <summary>start/end points (or host analytical member for its full length), force [fx,fy,fz] plf, load_case?.</summary>
        [BridgeCommand("create_line_load", CommandKind.Write)]
        public static object CreateLineLoad(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var host = HostId(ctx);
            var caseId = CaseId(ctx);
            var force = Vec(ctx.Tok("force"), LoadKind.Linear);
            var moment = Vec(ctx.Tok("moment"), LoadKind.Moment);
            var id = ctx.Tx("Create line load", () =>
            {
                LineLoad ll;
                if (ctx.Has("line"))
                    ll = LineLoad.Create(doc, host, CommandContext.ToLine(ctx.Tok("line")), force, moment, null);
                else if (host != ElementId.InvalidElementId)
                    ll = LineLoad.Create(doc, host, force, moment, null);
                else throw new BridgeException("BAD_PARAMS", "Pass line {start,end} and/or host.");
                AssignCase(ll, caseId);
                return ll.Id;
            });
            return new JObject { ["id"] = id.AsLong() };
        }

        /// <summary>
        /// host (analytical panel, or a floor/wall with one — required: Revit 2024+ only creates hosted area loads),
        /// optional boundary (closed points on the panel) for a partial load, force [fx,fy,fz] psf, load_case?.
        /// </summary>
        [BridgeCommand("create_area_load", CommandKind.Write)]
        public static object CreateAreaLoad(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var host = HostId(ctx);
            if (host == ElementId.InvalidElementId)
                throw new BridgeException("BAD_PARAMS", "create_area_load needs host (an analytical panel, or a floor/wall that has one).",
                    "Revit 2024+ only creates hosted area loads through the API.");
            var caseId = CaseId(ctx);
            var force = Vec(ctx.Tok("force"), LoadKind.Area);
            var id = ctx.Tx("Create area load", () =>
            {
                AreaLoad al;
                if (ctx.Has("boundary"))
                    al = AreaLoad.Create(doc, host, new List<CurveLoop> { CommandContext.ToLoop(ctx.Tok("boundary")) }, force, null);
                else if (host != ElementId.InvalidElementId)
                    al = AreaLoad.Create(doc, host, force, null);
                else throw new BridgeException("BAD_PARAMS", "Pass boundary (closed points) and/or host.");
                AssignCase(al, caseId);
                return al.Id;
            });
            return new JObject { ["id"] = id.AsLong() };
        }

        [BridgeCommand("list_load_cases")]
        public static object ListLoadCases(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var loads = new FilteredElementCollector(doc).OfClass(typeof(LoadBase)).Cast<LoadBase>()
                .GroupBy(l => l.LoadCaseId).ToDictionary(g => g.Key, g => g.Count());
            return new JArray(new FilteredElementCollector(doc).OfClass(typeof(LoadCase)).Cast<LoadCase>().OrderBy(c => c.Number).Select(c => new JObject
            {
                ["id"] = c.Id.AsLong(),
                ["name"] = c.Name,
                ["number"] = c.Number,
                ["nature"] = doc.GetElement(c.NatureId)?.Name,
                ["category"] = Category.GetCategory(doc, c.SubcategoryId)?.Name,
                ["loads"] = loads.TryGetValue(c.Id, out var n) ? n : 0,
            }));
        }

        /// <summary>name, category dead|live|roof_live|snow|wind|seismic|temperature|accidental, nature? (existing or new name).</summary>
        [BridgeCommand("create_load_case", CommandKind.Write)]
        public static object CreateLoadCase(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var name = ctx.StrReq("name");
            var catName = ctx.Str("category", "dead").Replace("_", "");
            if (!Enum.TryParse(catName, true, out LoadCaseCategory cat))
                throw new BridgeException("BAD_PARAMS", $"category '{ctx.Str("category")}' invalid. Use dead, live, roof_live, snow, wind, seismic, temperature, accidental.");
            var natureName = ctx.Str("nature", catName);
            var id = ctx.Tx("Create load case", () =>
            {
                var nature = new FilteredElementCollector(doc).OfClass(typeof(LoadNature)).Cast<LoadNature>()
                                 .FirstOrDefault(n => string.Equals(n.Name, natureName, StringComparison.OrdinalIgnoreCase))
                             ?? LoadNature.Create(doc, natureName);
                return LoadCase.Create(doc, name, nature.Id, cat).Id;
            });
            var lc = (LoadCase)doc.GetElement(id);
            return new JObject { ["id"] = id.AsLong(), ["name"] = lc.Name, ["number"] = lc.Number };
        }

        // ── Structural usage ─────────────────────────────────────────────────

        [BridgeCommand("get_structural_usage")]
        public static object GetStructuralUsage(CommandContext ctx) =>
            new JArray(ctx.Elements("ids").Select(e =>
            {
                var o = Describe.Basic(e);
                if (e is FamilyInstance fi)
                {
                    o["structural_type"] = fi.StructuralType.ToString();
                    try { o["structural_usage"] = fi.StructuralUsage.ToString(); } catch { o["structural_usage"] = null; }
                }
                return o;
            }));

        /// <summary>usage: Girder | Joist | Purlin | HorizontalBracing | KickerBracing | Other | Automatic ...</summary>
        [BridgeCommand("set_structural_usage", CommandKind.Write)]
        public static object SetStructuralUsage(CommandContext ctx)
        {
            var key = ctx.StrReq("usage").Replace("_", "").Replace(" ", "");
            if (!Enum.TryParse(key, true, out StructuralInstanceUsage usage))
                throw new BridgeException("BAD_PARAMS", $"usage '{ctx.Str("usage")}' invalid.", "Values: " + string.Join(", ", Enum.GetNames(typeof(StructuralInstanceUsage))));
            var els = ctx.Elements("ids");
            ctx.Tx("Set structural usage", () =>
            {
                foreach (var e in els)
                {
                    if (!(e is FamilyInstance fi) || fi.StructuralType != StructuralType.Beam)
                        throw new BridgeException("BAD_PARAMS", $"Element {e.Id.AsLong()} is not a structural framing member.");
                    fi.StructuralUsage = usage;
                }
            });
            return new JObject { ["changed"] = els.Count, ["usage"] = usage.ToString() };
        }
    }
}
