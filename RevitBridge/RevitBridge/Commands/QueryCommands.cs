using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitBridge.Bridge;
using RevitBridge.Services;

namespace RevitBridge.Commands
{
    /// <summary>Read-only model queries. Lengths in feet.</summary>
    internal static class QueryCommands
    {
        // ── query_elements ──────────────────────────────────────────────────

        /// <summary>
        /// Filtered element search with an analysis breakdown.
        /// params: category (str|list), class, level, view, bbox {min,max}, filters [{param, op, value}],
        /// name_contains, name_equals, type_name, include_types, max_results (200), fields, geometry.
        /// </summary>
        [BridgeCommand("query_elements")]
        public static object QueryElements(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var col = ctx.Has("view")
                ? new FilteredElementCollector(doc, Lookup.View(doc, ctx.Str("view")).Id)
                : new FilteredElementCollector(doc);

            col = ctx.Bool("include_types", false) ? col.WhereElementIsElementType() : col.WhereElementIsNotElementType();

            var cats = ctx.StrList("category");
            if (cats.Count == 1) col = col.OfCategoryId(Lookup.Category(doc, cats[0]).Id);
            else if (cats.Count > 1)
                col = col.WherePasses(new ElementMulticategoryFilter(cats.Select(c => Lookup.Category(doc, c).Id).ToList()));

            if (ctx.Has("class"))
            {
                var t = ResolveClass(ctx.Str("class"));
                col = col.OfClass(t);
            }
            if (ctx.Has("level"))
                col = col.WherePasses(new ElementLevelFilter(ctx.Level(ctx.Str("level")).Id));
            if (ctx.Has("bbox"))
            {
                var bb = (JObject)ctx.Tok("bbox");
                var outline = new Outline(CommandContext.ToXyz(bb["min"]), CommandContext.ToXyz(bb["max"]));
                col = col.WherePasses(ctx.Bool("bbox_inside", false)
                    ? (ElementFilter)new BoundingBoxIsInsideFilter(outline)
                    : new BoundingBoxIntersectsFilter(outline));
            }

            IEnumerable<Element> els = col.ToElements();
            if (!ctx.Has("category") && !ctx.Has("class") && !ctx.Bool("include_types", false))
                els = els.Where(e => e.Category != null && e.Category.CategoryType != CategoryType.Internal);

            var nameContains = ctx.Str("name_contains");
            var nameEquals = ctx.Str("name_equals");
            var typeName = ctx.Str("type_name");
            if (nameContains != null)
                els = els.Where(e => (Json.SafeName(e) ?? "").IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0
                                  || TypeLabel(e).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0);
            if (nameEquals != null)
                els = els.Where(e => string.Equals(Json.SafeName(e), nameEquals, StringComparison.OrdinalIgnoreCase));
            if (typeName != null)
                els = els.Where(e => TypeLabel(e).IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0);

            foreach (var f in ctx.Arr("filters").OfType<JObject>())
                els = ApplyParamFilter(els, f).ToList();

            var list = els.ToList();
            var max = ctx.Int("max_results", 200);
            var fields = ctx.Tok("fields");
            var geometry = ctx.Str("geometry", "none");

            return new JObject
            {
                ["total"] = list.Count,
                ["returned"] = Math.Min(max, list.Count),
                ["truncated"] = list.Count > max,
                ["analysis"] = new JObject
                {
                    ["by_category"] = CountBy(list, e => e.Category?.Name ?? "(none)"),
                    ["by_level"] = CountBy(list, e => LevelName(e) ?? "(none)"),
                    ["by_type"] = CountBy(list, e => TypeLabel(e), 30),
                },
                ["elements"] = new JArray(list.Take(max).Select(e => DescribeElement(e, fields, geometry))),
            };
        }

        private static Type ResolveClass(string name)
        {
            var n = name.Trim();
            var t = typeof(Element).Assembly.GetTypes().FirstOrDefault(x =>
                typeof(Element).IsAssignableFrom(x) && (x.Name.Equals(n, StringComparison.OrdinalIgnoreCase) || x.FullName.Equals(n, StringComparison.OrdinalIgnoreCase)));
            return t ?? throw new BridgeException("NOT_FOUND", $"Element class '{name}' not found.", "e.g. Wall, Floor, FamilyInstance, Level, Grid, ViewSheet.");
        }

        private static IEnumerable<Element> ApplyParamFilter(IEnumerable<Element> els, JObject f)
        {
            var pname = (string)f["param"] ?? (string)f["name"] ?? throw new BridgeException("BAD_PARAMS", "filter needs 'param'.");
            var op = ((string)f["op"] ?? "eq").ToLowerInvariant();
            var val = f["value"];
            foreach (var e in els)
            {
                var p = FindParam(e, pname) ?? FindParam(e.Document.GetElement(e.GetTypeId()), pname);
                if (Match(p, op, val)) yield return e;
            }
        }

        internal static Parameter FindParam(Element e, string name)
        {
            if (e == null) return null;
            if (Enum.TryParse(name, true, out BuiltInParameter bip) && !int.TryParse(name, out _))
            {
                var bp = e.get_Parameter(bip);
                if (bp != null) return bp;
            }
            if (Guid.TryParse(name, out var g)) return e.get_Parameter(g);
            return e.LookupParameter(name);
        }

        private static bool Match(Parameter p, string op, JToken val)
        {
            if (op == "has_value") return p != null && p.HasValue;
            if (op == "empty") return p == null || !p.HasValue || (p.StorageType == StorageType.String && string.IsNullOrEmpty(p.AsString()));
            if (p == null) return op == "ne";
            string sval = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
            double? num = p.StorageType == StorageType.Double ? p.AsDouble()
                        : p.StorageType == StorageType.Integer ? p.AsInteger()
                        : p.StorageType == StorageType.ElementId ? p.AsElementId().AsLong() : (double?)null;
            double? target = null;
            if (val != null && (val.Type == JTokenType.Integer || val.Type == JTokenType.Float)) target = val.Value<double>();
            else if (val != null && p.StorageType == StorageType.Double && val.Type == JTokenType.String)
            {
                try { target = UnitParser.ToFeet(val); } catch { }
            }
            var vtext = val?.ToString() ?? "";
            switch (op)
            {
                case "eq": return target.HasValue && num.HasValue ? Math.Abs(num.Value - target.Value) < 1e-6 : string.Equals(sval, vtext, StringComparison.OrdinalIgnoreCase);
                case "ne": return target.HasValue && num.HasValue ? Math.Abs(num.Value - target.Value) >= 1e-6 : !string.Equals(sval, vtext, StringComparison.OrdinalIgnoreCase);
                case "gt": return num.HasValue && target.HasValue && num > target;
                case "ge": return num.HasValue && target.HasValue && num >= target - 1e-9;
                case "lt": return num.HasValue && target.HasValue && num < target;
                case "le": return num.HasValue && target.HasValue && num <= target + 1e-9;
                case "contains": return (sval ?? "").IndexOf(vtext, StringComparison.OrdinalIgnoreCase) >= 0;
                case "startswith": return (sval ?? "").StartsWith(vtext, StringComparison.OrdinalIgnoreCase);
                default: throw new BridgeException("BAD_PARAMS", $"Unknown filter op '{op}'. Use eq, ne, gt, ge, lt, le, contains, startswith, has_value, empty.");
            }
        }

        private static JObject CountBy(List<Element> els, Func<Element, string> key, int top = 100)
        {
            var o = new JObject();
            foreach (var g in els.GroupBy(key).OrderByDescending(g => g.Count()).Take(top)) o[g.Key ?? "(none)"] = g.Count();
            return o;
        }

        private static string TypeLabel(Element e)
        {
            var t = e.Document.GetElement(e.GetTypeId()) as ElementType;
            return t == null ? "" : $"{t.FamilyName}: {t.Name}";
        }

        private static string LevelName(Element e)
        {
            var id = e.LevelId;
            if (id != null && id != ElementId.InvalidElementId) return e.Document.GetElement(id)?.Name;
            var p = e.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM) ?? e.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)
                    ?? e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
            return p != null && p.StorageType == StorageType.ElementId ? e.Document.GetElement(p.AsElementId())?.Name : null;
        }

        /// <summary>fields: "basic" | "params" | "all" | [names]; geometry: none|bbox|location|solid_summary|all.</summary>
        internal static JObject DescribeElement(Element e, JToken fields, string geometry)
        {
            var o = Describe.Basic(e);
            if (o["level"] == null) { var ln = LevelName(e); if (ln != null) o["level"] = ln; }
            var mode = fields?.Type == JTokenType.String ? fields.ToString().ToLowerInvariant() : fields is JArray ? "specific" : "basic";
            if (mode == "params" || mode == "all") o["parameters"] = Describe.Params(e);
            if (mode == "all")
            {
                var t = e.Document.GetElement(e.GetTypeId());
                if (t != null) o["type_parameters"] = Describe.Params(t);
            }
            if (mode == "specific")
            {
                var names = ((JArray)fields).Select(x => x.ToString()).ToList();
                var ps = Describe.Params(e, names);
                var t = e.Document.GetElement(e.GetTypeId());
                if (t != null)
                    foreach (var kv in Describe.Params(t, names.Where(n => !ps.ContainsKey(n)).ToList())) ps[kv.Key] = kv.Value;
                o["parameters"] = ps;
            }
            geometry = (geometry ?? "none").ToLowerInvariant();
            if (geometry == "location" || geometry == "all") o["location"] = Describe.Location(e);
            if (geometry == "bbox" || geometry == "all") o["bbox"] = Describe.BBox(e);
            if (geometry == "solid_summary" || geometry == "all") o["solid"] = Describe.SolidSummary(e);
            return o;
        }

        [BridgeCommand("get_elements")]
        public static object GetElements(CommandContext ctx)
        {
            var fields = ctx.Tok("fields") ?? "params";
            var geometry = ctx.Str("geometry", "location");
            var arr = new JArray();
            var missing = new JArray();
            foreach (var id in ctx.Ids("ids"))
            {
                var e = ctx.Doc.GetElement(id);
                if (e == null) { missing.Add(id.AsLong()); continue; }
                arr.Add(DescribeElement(e, fields, geometry));
            }
            return new JObject { ["elements"] = arr, ["missing"] = missing };
        }

        // ── Lists ───────────────────────────────────────────────────────────

        [BridgeCommand("list_types")]
        public static object ListTypes(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var col = new FilteredElementCollector(doc).WhereElementIsElementType();
            if (ctx.Has("category")) col = col.OfCategoryId(Lookup.Category(doc, ctx.Str("category")).Id);
            if (ctx.Has("class")) col = col.OfClass(ResolveClass(ctx.Str("class")));
            var nameContains = ctx.Str("name_contains");
            var types = col.Cast<ElementType>()
                .Where(t => nameContains == null || $"{t.FamilyName}: {t.Name}".IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(t => t.FamilyName).ThenBy(t => t.Name).ToList();
            var max = ctx.Int("max_results", 500);
            return new JObject
            {
                ["total"] = types.Count,
                ["types"] = new JArray(types.Take(max).Select(t => new JObject
                {
                    ["id"] = t.Id.AsLong(),
                    ["family"] = t.FamilyName,
                    ["name"] = t.Name,
                    ["category"] = t.Category?.Name,
                    ["class"] = t.GetType().Name,
                    ["is_active"] = t is FamilySymbol fs ? fs.IsActive : (JToken)null,
                })),
            };
        }

        [BridgeCommand("list_families")]
        public static object ListFamilies(CommandContext ctx)
        {
            var doc = ctx.Doc;
            IEnumerable<Family> fams = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>();
            if (ctx.Has("category"))
            {
                var cid = Lookup.Category(doc, ctx.Str("category")).Id;
                fams = fams.Where(f => f.FamilyCategory != null && f.FamilyCategory.Id == cid);
            }
            return new JArray(fams.OrderBy(f => f.FamilyCategory?.Name).ThenBy(f => f.Name).Select(f => new JObject
            {
                ["id"] = f.Id.AsLong(),
                ["name"] = f.Name,
                ["category"] = f.FamilyCategory?.Name,
                ["is_in_place"] = f.IsInPlace,
                ["types"] = new JArray(f.GetFamilySymbolIds().Select(i => doc.GetElement(i)).Where(s => s != null)
                    .Select(s => new JObject { ["id"] = s.Id.AsLong(), ["name"] = s.Name })),
            }));
        }

        [BridgeCommand("list_levels")]
        public static object ListLevels(CommandContext ctx)
        {
            var doc = ctx.Doc;
            return new JArray(new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation)
                .Select(l => new JObject
                {
                    ["id"] = l.Id.AsLong(),
                    ["name"] = l.Name,
                    ["elevation"] = Math.Round(l.Elevation, 6),
                    ["elevation_display"] = UnitParser.FormatLength(doc, l.Elevation),
                    ["is_building_story"] = l.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY)?.AsInteger() == 1,
                }));
        }

        [BridgeCommand("list_grids")]
        public static object ListGrids(CommandContext ctx) =>
            new JArray(new FilteredElementCollector(ctx.Doc).OfClass(typeof(Grid)).Cast<Grid>().OrderBy(g => g.Name)
                .Select(g => new JObject { ["id"] = g.Id.AsLong(), ["name"] = g.Name, ["curve"] = Describe.Curve(g.Curve) }));

        [BridgeCommand("list_views")]
        public static object ListViews(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var type = ctx.Str("type");
            var includeTemplates = ctx.Bool("include_templates", false);
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => includeTemplates || !v.IsTemplate)
                .Where(v => v.ViewType != ViewType.Internal && v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser && v.ViewType != ViewType.Undefined)
                .Where(v => type == null || string.Equals(v.ViewType.ToString(), type, StringComparison.OrdinalIgnoreCase)
                            || (type.Equals("plan", StringComparison.OrdinalIgnoreCase) && Lookup.IsPlanLike(v))
                            || (type.Equals("3d", StringComparison.OrdinalIgnoreCase) && v.ViewType == ViewType.ThreeD))
                .OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name).ToList();
            var placed = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()
                .GroupBy(vp => vp.ViewId).ToDictionary(g => g.Key, g => g.First().SheetId);
            return new JArray(views.Select(v =>
            {
                var o = new JObject
                {
                    ["id"] = v.Id.AsLong(),
                    ["name"] = v.Name,
                    ["type"] = v.ViewType.ToString(),
                    ["is_template"] = v.IsTemplate,
                    ["scale"] = v.ViewType == ViewType.Schedule || v.ViewType == ViewType.DrawingSheet ? (JToken)null : v.Scale,
                };
                if (v.GenLevel != null) o["level"] = v.GenLevel.Name;
                if (placed.TryGetValue(v.Id, out var sheetId)) o["sheet"] = (doc.GetElement(sheetId) as ViewSheet)?.SheetNumber;
                return o;
            }));
        }

        [BridgeCommand("list_sheets")]
        public static object ListSheets(CommandContext ctx)
        {
            var doc = ctx.Doc;
            return new JArray(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsTemplate).OrderBy(s => s.SheetNumber)
                .Select(s => new JObject
                {
                    ["id"] = s.Id.AsLong(),
                    ["number"] = s.SheetNumber,
                    ["name"] = s.Name,
                    ["is_placeholder"] = s.IsPlaceholder,
                    ["views"] = new JArray(s.GetAllPlacedViews().Select(i => doc.GetElement(i)).Where(v => v != null)
                        .Select(v => new JObject { ["id"] = v.Id.AsLong(), ["name"] = v.Name })),
                    ["titleblock"] = new FilteredElementCollector(doc, s.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .WhereElementIsNotElementType().FirstElement() is Element tb ? $"{(doc.GetElement(tb.GetTypeId()) as ElementType)?.FamilyName}: {Json.SafeName(tb)}" : null,
                }));
        }

        [BridgeCommand("list_schedules")]
        public static object ListSchedules(CommandContext ctx) =>
            new JArray(new FilteredElementCollector(ctx.Doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule)
                .OrderBy(s => s.Name)
                .Select(s => new JObject
                {
                    ["id"] = s.Id.AsLong(),
                    ["name"] = s.Name,
                    ["category"] = Category.GetCategory(ctx.Doc, s.Definition.CategoryId)?.Name,
                    ["fields"] = s.Definition.GetFieldCount(),
                }));

        [BridgeCommand("get_schedule_data")]
        public static object GetScheduleData(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var key = ctx.Str("id") ?? ctx.StrReq("name");
            var sched = (Lookup.TryId(key, out var id) ? doc.GetElement(id) as ViewSchedule : null)
                        ?? new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                            .FirstOrDefault(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase))
                        ?? throw new BridgeException("NOT_FOUND", $"Schedule '{key}' not found.", "Use list_schedules.");
            var max = ctx.Int("max_rows", 2000);
            var table = sched.GetTableData();
            JArray Section(SectionType st)
            {
                var sec = table.GetSectionData(st);
                var rows = new JArray();
                if (sec == null) return rows;
                for (int r = sec.FirstRowNumber; r <= sec.LastRowNumber && rows.Count < max; r++)
                {
                    var row = new JArray();
                    for (int c = sec.FirstColumnNumber; c <= sec.LastColumnNumber; c++) row.Add(sched.GetCellText(st, r, c));
                    rows.Add(row);
                }
                return rows;
            }
            var body = Section(SectionType.Body);
            return new JObject
            {
                ["id"] = sched.Id.AsLong(),
                ["name"] = sched.Name,
                ["header"] = Section(SectionType.Header),
                ["columns"] = new JArray(Enumerable.Range(0, sched.Definition.GetFieldCount())
                    .Select(i => sched.Definition.GetField(i)).Where(f => !f.IsHidden).Select(f => f.GetName())),
                ["rows"] = body,
                ["row_count"] = body.Count,
            };
        }

        [BridgeCommand("get_selection")]
        public static object GetSelection(CommandContext ctx)
        {
            var uidoc = ctx.UIApp.ActiveUIDocument ?? throw new BridgeException("NO_DOCUMENT", "No active document.");
            var ids = uidoc.Selection.GetElementIds();
            return new JObject
            {
                ["count"] = ids.Count,
                ["elements"] = new JArray(ids.Take(ctx.Int("max_results", 500)).Select(i => uidoc.Document.GetElement(i)).Where(e => e != null)
                    .Select(e => DescribeElement(e, ctx.Tok("fields"), ctx.Str("geometry", "none")))),
            };
        }

        [BridgeCommand("list_worksets")]
        public static object ListWorksets(CommandContext ctx)
        {
            var doc = ctx.Doc;
            if (!doc.IsWorkshared) return new JObject { ["workshared"] = false, ["worksets"] = new JArray() };
            var active = doc.GetWorksetTable().GetActiveWorksetId();
            return new JObject
            {
                ["workshared"] = true,
                ["worksets"] = new JArray(new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Select(w => new JObject
                {
                    ["id"] = w.Id.IntegerValue,
                    ["name"] = w.Name,
                    ["is_open"] = w.IsOpen,
                    ["is_editable"] = w.IsEditable,
                    ["owner"] = w.Owner,
                    ["is_active"] = w.Id == active,
                })),
            };
        }

        [BridgeCommand("list_phases")]
        public static object ListPhases(CommandContext ctx) =>
            new JArray(ctx.Doc.Phases.Cast<Phase>().Select((p, i) => new JObject { ["id"] = p.Id.AsLong(), ["name"] = p.Name, ["sequence"] = i + 1 }));

        [BridgeCommand("list_design_options")]
        public static object ListDesignOptions(CommandContext ctx) =>
            new JArray(new FilteredElementCollector(ctx.Doc).OfClass(typeof(DesignOption)).Cast<DesignOption>().Select(o => new JObject
            {
                ["id"] = o.Id.AsLong(),
                ["name"] = o.Name,
                ["is_primary"] = o.IsPrimary,
                ["set"] = ctx.Doc.GetElement(o.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId() ?? ElementId.InvalidElementId)?.Name,
            }));

        [BridgeCommand("list_links")]
        public static object ListLinks(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var rvt = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Select(li =>
            {
                var lt = doc.GetElement(li.GetTypeId()) as RevitLinkType;
                var ld = li.GetLinkDocument();
                string path = null;
                try { var ext = lt?.GetExternalFileReference(); if (ext != null) path = ModelPathUtils.ConvertModelPathToUserVisiblePath(ext.GetAbsolutePath()); } catch { }
                return new JObject
                {
                    ["id"] = li.Id.AsLong(),
                    ["kind"] = "revit",
                    ["name"] = li.Name,
                    ["type_id"] = lt?.Id.AsLong(),
                    ["loaded"] = ld != null,
                    ["path"] = path,
                    ["linked_doc_title"] = ld?.Title,
                };
            });
            var cad = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Select(ii => new JObject
            {
                ["id"] = ii.Id.AsLong(),
                ["kind"] = ii.IsLinked ? "cad_link" : "cad_import",
                ["name"] = ii.Category?.Name,
                ["view_specific"] = ii.ViewSpecific,
            });
            return new JArray(rvt.Concat(cad));
        }
    }
}
