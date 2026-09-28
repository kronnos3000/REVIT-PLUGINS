using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitBridge.Bridge;
using RevitBridge.Services;

namespace RevitBridge.Commands
{
    /// <summary>Model edits. All run inside the per-call TransactionGroup (one undo).</summary>
    internal static class ModifyCommands
    {
        public const string FirmSharedParamFile =
            @"G:\Shared drives\4-10A   Design & Planning\AXS_ARCHITECTURAL (RESOURCES)\CC_SharedParameters.txt";

        // ── Parameters ───────────────────────────────────────────────────────

        /// <summary>updates: [{id, name|builtin|guid, value}] — batch, unit-aware, one undo.</summary>
        [BridgeCommand("set_parameters", CommandKind.Write)]
        public static object SetParameters(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var updates = ctx.Arr("updates").OfType<JObject>().ToList();
            if (updates.Count == 0) throw new BridgeException("BAD_PARAMS", "Pass updates: [{id, name|builtin|guid, value}].");
            ctx.GuardSize(updates.Count, "set_parameters");
            var stopOnError = ctx.Bool("stop_on_error", true);
            var results = new JArray();
            int failed = 0;
            ctx.Tx("Set parameters", () =>
            {
                foreach (var u in updates)
                {
                    var r = new JObject { ["id"] = u["id"], ["param"] = u["name"] ?? u["builtin"] ?? u["guid"] };
                    try
                    {
                        var e = ctx.Element(u["id"]);
                        var p = ParamWriter.Find(e, u) ?? throw new BridgeException("NOT_FOUND",
                            $"Element {e.Id.AsLong()} has no parameter '{r["param"]}'. (Type parameter? use set_type_parameters.)");
                        r["value"] = ParamWriter.Set(p, u["value"]);
                        r["ok"] = true;
                    }
                    catch (BridgeException ex)
                    {
                        if (stopOnError) throw new BridgeException(ex.Code, $"Update for id {u["id"]}: {ex.Message}", ex.Hint);
                        r["ok"] = false; r["error"] = ex.Message; failed++;
                    }
                    results.Add(r);
                }
            });
            return new JObject { ["updated"] = updates.Count - failed, ["failed"] = failed, ["results"] = results };
        }

        /// <summary>updates: [{type (id or 'Family: Type'), name|builtin|guid, value}].</summary>
        [BridgeCommand("set_type_parameters", CommandKind.Write)]
        public static object SetTypeParameters(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var updates = ctx.Arr("updates").OfType<JObject>().ToList();
            if (updates.Count == 0) throw new BridgeException("BAD_PARAMS", "Pass updates: [{type, name|builtin|guid, value}].");
            var results = new JArray();
            ctx.Tx("Set type parameters", () =>
            {
                foreach (var u in updates)
                {
                    var t = Lookup.Type<ElementType>(doc, u["type"]?.ToString());
                    var p = ParamWriter.Find(t, u) ?? throw new BridgeException("NOT_FOUND", $"Type '{t.FamilyName}: {t.Name}' has no parameter '{u["name"] ?? u["builtin"] ?? u["guid"]}'.");
                    results.Add(new JObject { ["type_id"] = t.Id.AsLong(), ["type"] = $"{t.FamilyName}: {t.Name}", ["param"] = p.Definition.Name, ["value"] = ParamWriter.Set(p, u["value"]) });
                }
            });
            return new JObject { ["results"] = results };
        }

        [BridgeCommand("change_type", CommandKind.Write)]
        public static object ChangeType(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var els = ctx.Elements("ids");
            var type = Lookup.Type<ElementType>(doc, ctx.StrReq("type"));
            ctx.Tx("Change type", () =>
            {
                if (type is FamilySymbol fs && !fs.IsActive) fs.Activate();
                foreach (var e in els)
                {
                    if (!e.IsValidType(type.Id))
                        throw new BridgeException("BAD_PARAMS", $"'{type.FamilyName}: {type.Name}' is not a valid type for element {e.Id.AsLong()} ({e.Category?.Name}).");
                    e.ChangeTypeId(type.Id);
                }
            });
            return new JObject { ["changed"] = els.Count, ["type"] = $"{type.FamilyName}: {type.Name}", ["type_id"] = type.Id.AsLong() };
        }

        /// <summary>Duplicate a type under a new name and optionally set its parameters: params {name: value}.</summary>
        [BridgeCommand("duplicate_type", CommandKind.Write)]
        public static object DuplicateType(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var src = Lookup.Type<ElementType>(doc, ctx.StrReq("type"));
            var newName = ctx.StrReq("new_name");
            var existing = new FilteredElementCollector(doc).OfClass(src.GetType()).Cast<ElementType>()
                .FirstOrDefault(t => t.FamilyName == src.FamilyName && t.Name == newName);
            if (existing != null && !ctx.Bool("reuse_existing", false))
                throw new BridgeException("EXISTS", $"'{src.FamilyName}: {newName}' already exists (id {existing.Id.AsLong()}).", "Pass reuse_existing=true to just update its params.");
            var values = new JObject();
            var id = ctx.Tx("Duplicate type", () =>
            {
                var t = existing ?? src.Duplicate(newName);
                if (ctx.Tok("params") is JObject ps)
                    foreach (var kv in ps)
                    {
                        var p = ParamWriter.Find(t, new JObject { ["name"] = kv.Key })
                                ?? throw new BridgeException("NOT_FOUND", $"Type has no parameter '{kv.Key}'.");
                        values[kv.Key] = ParamWriter.Set(p, kv.Value);
                    }
                return t.Id;
            });
            var nt = (ElementType)doc.GetElement(id);
            return new JObject { ["id"] = id.AsLong(), ["type"] = $"{nt.FamilyName}: {nt.Name}", ["reused"] = existing != null, ["params"] = values };
        }

        // ── Transforms ───────────────────────────────────────────────────────

        [BridgeCommand("move", CommandKind.Write)]
        public static object Move(CommandContext ctx)
        {
            var ids = ctx.Elements("ids").Select(e => e.Id).ToList();
            var v = CommandContext.ToXyz(ctx.Tok("vector"));
            ctx.Tx("Move", () => ElementTransformUtils.MoveElements(ctx.Doc, ids, v));
            return new JObject { ["moved"] = ids.Count, ["vector"] = Describe.Xyz(v) };
        }

        /// <summary>Rotate about a vertical axis through "point" (default: the elements' bbox centre). angle: degrees.</summary>
        [BridgeCommand("rotate", CommandKind.Write)]
        public static object Rotate(CommandContext ctx)
        {
            var els = ctx.Elements("ids");
            var center = ctx.PointOpt("point") ?? Center(els);
            var angle = ctx.Angle("angle");
            ctx.Tx("Rotate", () => ElementTransformUtils.RotateElements(ctx.Doc, els.Select(e => e.Id).ToList(),
                Line.CreateBound(center, center + XYZ.BasisZ), angle));
            return new JObject { ["rotated"] = els.Count, ["about"] = Describe.Xyz(center), ["angle_deg"] = angle * 180 / Math.PI };
        }

        private static XYZ Center(List<Element> els)
        {
            var boxes = els.Select(e => e.get_BoundingBox(null)).Where(b => b != null).ToList();
            if (boxes.Count == 0) throw new BridgeException("BAD_PARAMS", "Elements have no bounding box; pass point.");
            var min = new XYZ(boxes.Min(b => b.Min.X), boxes.Min(b => b.Min.Y), boxes.Min(b => b.Min.Z));
            var max = new XYZ(boxes.Max(b => b.Max.X), boxes.Max(b => b.Max.Y), boxes.Max(b => b.Max.Z));
            return (min + max) / 2;
        }

        [BridgeCommand("copy", CommandKind.Write)]
        public static object Copy(CommandContext ctx)
        {
            var ids = ctx.Elements("ids").Select(e => e.Id).ToList();
            var v = CommandContext.ToXyz(ctx.Tok("vector"));
            var created = ctx.Tx("Copy", () => ElementTransformUtils.CopyElements(ctx.Doc, ids, v));
            return new JObject { ["ids"] = Describe.Ids(created), ["count"] = created.Count };
        }

        /// <summary>Mirror about a vertical plane through the line axis {start,end}. copy=true keeps originals.</summary>
        [BridgeCommand("mirror", CommandKind.Write)]
        public static object Mirror(CommandContext ctx)
        {
            var ids = ctx.Elements("ids").Select(e => e.Id).ToList();
            var axis = CommandContext.ToLine(ctx.Tok("axis"));
            var dir = (axis.GetEndPoint(1) - axis.GetEndPoint(0)).Normalize();
            var normal = new XYZ(-dir.Y, dir.X, 0).Normalize();
            var plane = Plane.CreateByNormalAndOrigin(normal, axis.GetEndPoint(0));
            var copy = ctx.Bool("copy", true);
            ctx.Tx("Mirror", () => ElementTransformUtils.MirrorElements(ctx.Doc, ids, plane, copy));
            return new JObject { ["mirrored"] = ids.Count, ["copy"] = copy };
        }

        /// <summary>Linear array as independent copies: count (total incl. original) along spacing vector.</summary>
        [BridgeCommand("array", CommandKind.Write)]
        public static object Array(CommandContext ctx)
        {
            var ids = ctx.Elements("ids").Select(e => e.Id).ToList();
            var count = ctx.Int("count", 2);
            if (count < 2) throw new BridgeException("BAD_PARAMS", "count must be >= 2 (it includes the original).");
            var step = CommandContext.ToXyz(ctx.Tok("spacing"));
            ctx.GuardSize(ids.Count * (count - 1), "array");
            var created = ctx.Tx("Array", () =>
            {
                var all = new List<ElementId>();
                for (int i = 1; i < count; i++) all.AddRange(ElementTransformUtils.CopyElements(ctx.Doc, ids, step * i));
                return all;
            });
            return new JObject { ["ids"] = Describe.Ids(created), ["count"] = created.Count };
        }

        // ── Delete ───────────────────────────────────────────────────────────

        [BridgeCommand("delete_elements", CommandKind.Write)]
        public static object DeleteElements(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var els = ctx.Elements("ids");
            var ids = els.Select(e => e.Id).ToList();
            var dependents = new HashSet<long>();
            foreach (var e in els)
                foreach (var d in e.GetDependentElements(null))
                    if (!ids.Contains(d)) dependents.Add(d.AsLong());
            var preview = new JObject
            {
                ["count"] = els.Count,
                ["by_category"] = new JObject(els.GroupBy(e => e.Category?.Name ?? "(none)").Select(g => new JProperty(g.Key, g.Count()))),
                ["sample"] = new JArray(els.Take(20).Select(Describe.Basic)),
                ["dependent_count"] = dependents.Count,
                ["dependent_sample"] = new JArray(dependents.Take(20).Select(d => doc.GetElement(new ElementId(d))).Where(e => e != null).Select(Describe.Basic)),
            };
            ctx.RequireConfirm(preview, $"Deleting {els.Count} element(s) (+{dependents.Count} dependents)");
            var deleted = ctx.Tx("Delete elements", () => doc.Delete(ids));
            return new JObject { ["requested"] = ids.Count, ["deleted_total"] = deleted.Count, ["deleted"] = Describe.Ids(deleted.Take(1000)) };
        }

        // ── Geometry, worksets, phases ───────────────────────────────────────

        /// <summary>pairs: [[a,b],...] or ids (all pairwise with the first). mode: join | unjoin | switch.</summary>
        [BridgeCommand("join_geometry", CommandKind.Write)]
        public static object JoinGeometry(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var mode = ctx.Str("mode", "join").ToLowerInvariant();
            var pairs = new List<(Element, Element)>();
            if (ctx.Has("pairs"))
                foreach (var p in ctx.Arr("pairs").OfType<JArray>()) pairs.Add((ctx.Element(p[0]), ctx.Element(p[1])));
            else
            {
                var els = ctx.Elements("ids");
                for (int i = 1; i < els.Count; i++) pairs.Add((els[0], els[i]));
            }
            var skipped = new JArray();
            int done = 0;
            ctx.Tx("Join geometry", () =>
            {
                foreach (var (a, b) in pairs)
                {
                    bool joined = JoinGeometryUtils.AreElementsJoined(doc, a, b);
                    if (mode == "join" && !joined) { JoinGeometryUtils.JoinGeometry(doc, a, b); done++; }
                    else if (mode == "unjoin" && joined) { JoinGeometryUtils.UnjoinGeometry(doc, a, b); done++; }
                    else if (mode == "switch" && joined) { JoinGeometryUtils.SwitchJoinOrder(doc, a, b); done++; }
                    else skipped.Add(new JArray(a.Id.AsLong(), b.Id.AsLong()));
                }
            });
            return new JObject { ["mode"] = mode, ["done"] = done, ["skipped"] = skipped };
        }

        [BridgeCommand("set_workset", CommandKind.Write)]
        public static object SetWorkset(CommandContext ctx)
        {
            var doc = ctx.Doc;
            if (!doc.IsWorkshared) throw new BridgeException("UNSUPPORTED", "Document is not workshared.");
            var key = ctx.StrReq("workset");
            var ws = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)
                .FirstOrDefault(w => string.Equals(w.Name, key, StringComparison.OrdinalIgnoreCase) || w.Id.IntegerValue.ToString() == key)
                ?? throw new BridgeException("NOT_FOUND", $"Workset '{key}' not found.", "Use list_worksets.");
            var els = ctx.Elements("ids");
            ctx.Tx("Set workset", () =>
            {
                foreach (var e in els)
                {
                    var p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (p == null || p.IsReadOnly) throw new BridgeException("READ_ONLY", $"Workset of element {e.Id.AsLong()} cannot be changed.");
                    p.Set(ws.Id.IntegerValue);
                }
            });
            return new JObject { ["changed"] = els.Count, ["workset"] = ws.Name };
        }

        /// <summary>created and/or demolished: phase name or id ("none" clears demolished).</summary>
        [BridgeCommand("set_phase", CommandKind.Write)]
        public static object SetPhase(CommandContext ctx)
        {
            var doc = ctx.Doc;
            Phase Find(string k) => doc.Phases.Cast<Phase>().FirstOrDefault(p => string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase) || p.Id.AsLong().ToString() == k)
                                    ?? throw new BridgeException("NOT_FOUND", $"Phase '{k}' not found.", "Use list_phases.");
            var created = ctx.Has("created") ? Find(ctx.Str("created")) : null;
            var demolished = ctx.Str("demolished");
            var els = ctx.Elements("ids");
            ctx.Tx("Set phase", () =>
            {
                foreach (var e in els)
                {
                    if (created != null) e.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(created.Id);
                    if (demolished != null)
                        e.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.Set(
                            demolished.Equals("none", StringComparison.OrdinalIgnoreCase) ? ElementId.InvalidElementId : Find(demolished).Id);
                }
            });
            return new JObject { ["changed"] = els.Count };
        }

        // ── Families & shared parameters ─────────────────────────────────────

        private sealed class LoadOpts : IFamilyLoadOptions
        {
            private readonly bool _overwrite;
            public LoadOpts(bool overwrite) { _overwrite = overwrite; }
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = _overwrite; return _overwrite; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            { source = FamilySource.Family; overwriteParameterValues = _overwrite; return _overwrite; }
        }

        [BridgeCommand("load_family", CommandKind.Write)]
        public static object LoadFamily(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var path = ctx.StrReq("path");
            if (!File.Exists(path)) throw new BridgeException("NOT_FOUND", $"Family file not found: {path}");
            var overwrite = ctx.Bool("overwrite", false);
            Family fam = null;
            var loaded = ctx.Tx("Load family", () => doc.LoadFamily(path, new LoadOpts(overwrite), out fam));
            if (fam == null)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                fam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == name);
            }
            return new JObject
            {
                ["loaded"] = loaded,
                ["note"] = loaded ? null : "Family already loaded (pass overwrite=true to reload).",
                ["family_id"] = fam?.Id.AsLong(),
                ["family"] = fam?.Name,
                ["types"] = fam == null ? new JArray() : new JArray(fam.GetFamilySymbolIds().Select(i => doc.GetElement(i)?.Name)),
            };
        }

        /// <summary>
        /// Bind a shared parameter from the firm file (or file=) to categories.
        /// name, group (group in the shared-parameter file), categories [..], binding instance|type,
        /// spec text|number|integer|length|area|volume|angle|yesno|url (used only when creating),
        /// create_if_missing (default false — adds the definition to the shared file), properties_group
        /// (Properties palette group, e.g. Data, IdentityData, StructuralAnalysis; default Data).
        /// </summary>
        [BridgeCommand("bind_shared_parameter", CommandKind.Write)]
        public static object BindSharedParameter(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var app = ctx.UIApp.Application;
            var name = ctx.StrReq("name");
            var groupName = ctx.Str("group", "Analysis");
            var file = ctx.Str("file", FirmSharedParamFile);
            if (!File.Exists(file)) throw new BridgeException("NOT_FOUND", $"Shared parameter file not found: {file}");
            var isType = string.Equals(ctx.Str("binding", "instance"), "type", StringComparison.OrdinalIgnoreCase);
            var cats = ctx.StrList("categories");
            if (cats.Count == 0) throw new BridgeException("BAD_PARAMS", "Pass categories: ['Structural Framing', ...].");
            var propsGroup = ResolveGroupType(ctx.Str("properties_group", "Data"));

            var previous = app.SharedParametersFilename;
            ExternalDefinition def;
            bool createdDef = false;
            try
            {
                app.SharedParametersFilename = file;
                var df = app.OpenSharedParameterFile() ?? throw new BridgeException("REVIT_ERROR", $"Revit could not open {file}.");
                def = df.Groups.SelectMany(g => g.Definitions.Cast<Definition>()).OfType<ExternalDefinition>()
                        .FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
                if (def == null)
                {
                    if (!ctx.Bool("create_if_missing", false))
                        throw new BridgeException("NOT_FOUND", $"'{name}' is not in {Path.GetFileName(file)}.",
                            "Pass create_if_missing=true (and spec) to add it to the shared parameter file.");
                    if (ctx.DryRun)
                        throw new BridgeException("UNSUPPORTED", "dry_run cannot preview a new shared-file definition (the file write is not transactional).");
                    var grp = df.Groups.get_Item(groupName) ?? df.Groups.Create(groupName);
                    var opts = new ExternalDefinitionCreationOptions(name, ResolveSpec(ctx.Str("spec", "text")))
                    {
                        Description = ctx.Str("description", ""),
                        UserModifiable = true,
                        Visible = true,
                    };
                    def = (ExternalDefinition)grp.Definitions.Create(opts);
                    createdDef = true;
                }
            }
            finally
            {
                try { app.SharedParametersFilename = previous ?? ""; } catch { }
            }

            var catSet = app.Create.NewCategorySet();
            foreach (var c in cats) catSet.Insert(Lookup.Category(doc, c));
            var binding = isType ? (ElementBinding)app.Create.NewTypeBinding(catSet) : app.Create.NewInstanceBinding(catSet);
            var result = ctx.Tx("Bind shared parameter", () =>
            {
                var map = doc.ParameterBindings;
                var ok = map.Insert(def, binding, propsGroup);
                if (!ok) ok = map.ReInsert(def, binding, propsGroup);
                return ok;
            });
            return new JObject
            {
                ["bound"] = result,
                ["name"] = def.Name,
                ["guid"] = def.GUID.ToString(),
                ["spec"] = def.GetDataType().TypeId,
                ["binding"] = isType ? "type" : "instance",
                ["categories"] = new JArray(cats),
                ["created_in_shared_file"] = createdDef,
            };
        }

        /// <summary>Correct ForgeTypeIds (WindCalc's SharedParamWriter has Number wrong — don't copy it).</summary>
        internal static ForgeTypeId ResolveSpec(string s)
        {
            switch ((s ?? "text").ToLowerInvariant())
            {
                case "text": case "string": return SpecTypeId.String.Text;
                case "number": return SpecTypeId.Number;
                case "integer": case "int": return SpecTypeId.Int.Integer;
                case "length": return SpecTypeId.Length;
                case "area": return SpecTypeId.Area;
                case "volume": return SpecTypeId.Volume;
                case "angle": return SpecTypeId.Angle;
                case "yesno": case "bool": case "boolean": return SpecTypeId.Boolean.YesNo;
                case "url": return SpecTypeId.String.Url;
                case "force": return SpecTypeId.Force;
                case "moment": return SpecTypeId.Moment;
                default: throw new BridgeException("BAD_PARAMS", $"Unknown spec '{s}'. Use text, number, integer, length, area, volume, angle, yesno, url, force, moment.");
            }
        }

        private static ForgeTypeId ResolveGroupType(string name)
        {
            var prop = typeof(GroupTypeId).GetProperties(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(p => string.Equals(p.Name, name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
            return prop?.GetValue(null) as ForgeTypeId
                   ?? throw new BridgeException("BAD_PARAMS", $"Unknown properties_group '{name}'.",
                        "e.g. " + string.Join(", ", typeof(GroupTypeId).GetProperties(BindingFlags.Public | BindingFlags.Static).Take(25).Select(p => p.Name)));
        }
    }
}
