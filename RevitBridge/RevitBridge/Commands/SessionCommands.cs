using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitBridge.Bridge;
using RevitBridge.Services;

namespace RevitBridge.Commands
{
    /// <summary>Session-level commands: status, documents, save/sync/close, warnings, project info.</summary>
    internal static class SessionCommands
    {
        [BridgeCommand("bridge_status")]
        public static object BridgeStatus(CommandContext ctx)
        {
            var app = ctx.UIApp.Application;
            var active = ctx.UIApp.ActiveUIDocument;
            return new JObject
            {
                ["pid"] = BridgeHost.Pid,
                ["pipe"] = BridgeHost.PipeName,
                ["revit_year"] = BridgeHost.RevitYear,
                ["version_build"] = app.VersionBuild,
                ["version_name"] = app.VersionName,
                ["bridge_version"] = UpdateChecker.CurrentVersion.ToString(),
                ["username"] = app.Username,
                ["active_doc"] = active == null ? null : CommandContext.DocInfo(active.Document),
                ["active_view"] = active?.ActiveView == null ? null : new JObject
                {
                    ["id"] = active.ActiveView.Id.AsLong(),
                    ["name"] = active.ActiveView.Name,
                    ["type"] = active.ActiveView.ViewType.ToString(),
                },
                ["open_documents"] = ctx.OpenDocuments().Count(),
                ["started_utc"] = BridgeHost.StartedUtc.ToString("o"),
                ["commands"] = CommandRouter.All.Count(),
            };
        }

        [BridgeCommand("list_commands")]
        public static object ListCommands(CommandContext ctx) =>
            new JArray(CommandRouter.All.Select(d => new JObject { ["name"] = d.Name, ["kind"] = d.Kind.ToString().ToLowerInvariant() }));

        [BridgeCommand("list_documents")]
        public static object ListDocuments(CommandContext ctx)
        {
            var active = ctx.UIApp.ActiveUIDocument?.Document;
            return new JArray(ctx.OpenDocuments().Select(d =>
            {
                var o = CommandContext.DocInfo(d);
                o["is_active"] = active != null && d.Equals(active);
                o["is_read_only"] = d.IsReadOnly;
                if (d.IsWorkshared)
                {
                    try { o["central_path"] = ModelPathUtils.ConvertModelPathToUserVisiblePath(d.GetWorksharingCentralModelPath()); }
                    catch { }
                }
                return o;
            }));
        }

        [BridgeCommand("activate_document", CommandKind.Session)]
        public static object ActivateDocument(CommandContext ctx)
        {
            var doc = ctx.ResolveDoc(ctx.StrReq("doc"));
            if (string.IsNullOrEmpty(doc.PathName))
                throw new BridgeException("UNSUPPORTED", "Only saved documents can be activated through the API.", "save_as it first.");
            ctx.UIApp.OpenAndActivateDocument(doc.PathName);
            return CommandContext.DocInfo(ctx.UIApp.ActiveUIDocument.Document);
        }

        [BridgeCommand("open_document", CommandKind.Session)]
        public static object OpenDocument(CommandContext ctx)
        {
            var path = ctx.StrReq("path");
            if (!File.Exists(path)) throw new BridgeException("NOT_FOUND", $"File not found: {path}");
            var detach = ctx.Bool("detach", false);
            var audit = ctx.Bool("audit", false);
            var activate = ctx.Bool("activate", true);

            var opts = new OpenOptions { Audit = audit };
            if (detach) opts.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
            var mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);

            Document doc;
            if (activate) doc = ctx.UIApp.OpenAndActivateDocument(mp, opts, false).Document;
            else doc = ctx.UIApp.Application.OpenDocumentFile(mp, opts);
            return CommandContext.DocInfo(doc);
        }

        /// <summary>Not in the original spec list, but needed for scratch-doc workflows and the smoke test.</summary>
        [BridgeCommand("new_document", CommandKind.Session)]
        public static object NewDocument(CommandContext ctx)
        {
            var app = ctx.UIApp.Application;
            var template = ctx.Str("template") ?? app.DefaultProjectTemplate;
            if (string.IsNullOrWhiteSpace(template) || !File.Exists(template))
                throw new BridgeException("NOT_FOUND", $"Template not found: '{template}'.", "Pass template=<path to .rte>.");
            var savePath = ctx.Str("save_path") ??
                Path.Combine(Path.GetTempPath(), "RevitBridge", $"scratch_{DateTime.Now:yyyyMMdd_HHmmss}.rvt");
            if (File.Exists(savePath))
                throw new BridgeException("EXISTS", $"{savePath} already exists.", "Choose a new save_path.");
            Directory.CreateDirectory(Path.GetDirectoryName(savePath));

            var doc = app.NewProjectDocument(template);
            doc.SaveAs(savePath, new SaveAsOptions { OverwriteExistingFile = false });
            if (ctx.Bool("activate", true))
            {
                ctx.UIApp.OpenAndActivateDocument(savePath);
                doc = ctx.UIApp.ActiveUIDocument.Document;
            }
            return new JObject { ["doc"] = CommandContext.DocInfo(doc), ["template"] = template };
        }

        [BridgeCommand("save_document", CommandKind.Session)]
        public static object SaveDocument(CommandContext ctx)
        {
            var doc = ctx.Doc;
            if (string.IsNullOrEmpty(doc.PathName))
                throw new BridgeException("UNSUPPORTED", "Document has never been saved.", "Use save_as with a path.");
            doc.Save();
            return CommandContext.DocInfo(doc);
        }

        [BridgeCommand("save_as", CommandKind.Session)]
        public static object SaveAs(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var path = ctx.Str("path") ?? NextVersionedPath(doc.PathName);
            if (path == null)
                throw new BridgeException("BAD_PARAMS", "Document has no path; pass path=.");
            if (!Path.HasExtension(path)) path += doc.IsFamilyDocument ? ".rfa" : ".rvt";
            var exists = File.Exists(path);
            if (exists)
                ctx.RequireConfirm(new JObject { ["path"] = path, ["exists"] = true, ["size_bytes"] = new FileInfo(path).Length,
                    ["modified_utc"] = File.GetLastWriteTimeUtc(path).ToString("o") }, "Overwriting an existing file");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var opts = new SaveAsOptions { OverwriteExistingFile = exists };
            if (doc.IsWorkshared)
                opts.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = ctx.Bool("as_central", false) });
            doc.SaveAs(path, opts);
            return new JObject { ["path"] = path, ["overwritten"] = exists, ["doc"] = CommandContext.DocInfo(doc) };
        }

        private static string NextVersionedPath(string current)
        {
            if (string.IsNullOrEmpty(current)) return null;
            var dir = Path.GetDirectoryName(current);
            var ext = Path.GetExtension(current);
            var stem = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(current), @"_v\d+$", "");
            for (int v = 2; v < 1000; v++)
            {
                var p = Path.Combine(dir, $"{stem}_v{v}{ext}");
                if (!File.Exists(p)) return p;
            }
            throw new BridgeException("EXISTS", "No free _vN name below _v1000.");
        }

        [BridgeCommand("close_document", CommandKind.Session)]
        public static object CloseDocument(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var save = ctx.Bool("save", false);
            if (doc.IsModified && !save)
                ctx.RequireConfirm(new JObject { ["doc"] = CommandContext.DocInfo(doc), ["unsaved_changes"] = true },
                    "Closing a modified document without saving");
            if (ctx.IsActiveDoc)
            {
                var other = ctx.OpenDocuments().FirstOrDefault(d => !d.Equals(doc) && !string.IsNullOrEmpty(d.PathName));
                if (other == null)
                    throw new BridgeException("UNSUPPORTED", "Revit cannot close the active document through the API when no other saved document is open.",
                        "Open or activate another document first (open_document / activate_document).");
                ctx.UIApp.OpenAndActivateDocument(other.PathName);
            }
            var title = doc.Title;
            var ok = doc.Close(save);
            return new JObject { ["closed"] = ok, ["title"] = title, ["saved"] = save };
        }

        [BridgeCommand("synchronize_with_central", CommandKind.Session)]
        public static object SynchronizeWithCentral(CommandContext ctx)
        {
            var doc = ctx.Doc;
            if (!doc.IsWorkshared) throw new BridgeException("UNSUPPORTED", $"'{doc.Title}' is not workshared.");
            var comment = ctx.Str("comment", "Synchronized by Claude via RevitBridge");
            var relinquish = ctx.Bool("relinquish", true);
            ctx.RequireConfirm(new JObject { ["doc"] = CommandContext.DocInfo(doc), ["comment"] = comment, ["relinquish_all"] = relinquish },
                "Synchronize with central");
            // Belt and braces: only this method, only with confirm, ever syncs.
            if (!ctx.Confirm || ctx.Method != "synchronize_with_central")
                throw new BridgeException("CONFIRM_REQUIRED", "Refusing to synchronize.");
            var sync = new SynchronizeWithCentralOptions { Comment = comment, SaveLocalBefore = true, SaveLocalAfter = true };
            var ro = new RelinquishOptions(relinquish);
            sync.SetRelinquishOptions(ro);
            doc.SynchronizeWithCentral(new TransactWithCentralOptions(), sync);
            return new JObject { ["synchronized"] = true, ["comment"] = comment };
        }

        [BridgeCommand("relinquish", CommandKind.Session)]
        public static object Relinquish(CommandContext ctx)
        {
            var doc = ctx.Doc;
            if (!doc.IsWorkshared) throw new BridgeException("UNSUPPORTED", $"'{doc.Title}' is not workshared.");
            ctx.RequireConfirm(new JObject { ["doc"] = CommandContext.DocInfo(doc) }, "Relinquishing all borrowed elements and worksets");
            var ro = new RelinquishOptions(true);
            var res = WorksharingUtils.RelinquishOwnership(doc, ro, new TransactWithCentralOptions());
            return new JObject
            {
                ["relinquished_elements"] = res.GetRelinquishedElements().Count,
                ["relinquished_worksets"] = res.GetRelinquishedWorksets().Count,
            };
        }

        [BridgeCommand("get_warnings")]
        public static object GetWarnings(CommandContext ctx)
        {
            var max = ctx.Int("max_results", 500);
            var all = ctx.Doc.GetWarnings();
            var groups = all.GroupBy(w => w.GetDescriptionText())
                            .Select(g => new JObject { ["message"] = g.Key, ["count"] = g.Count() })
                            .OrderByDescending(o => (int)o["count"]);
            return new JObject
            {
                ["total"] = all.Count,
                ["by_message"] = new JArray(groups),
                ["warnings"] = new JArray(all.Take(max).Select(w => new JObject
                {
                    ["message"] = w.GetDescriptionText(),
                    ["severity"] = w.GetSeverity().ToString(),
                    ["element_ids"] = Describe.Ids(w.GetFailingElements().Concat(w.GetAdditionalElements())),
                })),
                ["truncated"] = all.Count > max,
            };
        }

        [BridgeCommand("get_project_info")]
        public static object GetProjectInfo(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var pi = doc.ProjectInformation;
            var o = new JObject { ["doc"] = CommandContext.DocInfo(doc) };
            if (pi != null) o["parameters"] = Describe.Params(pi);
            try
            {
                var site = doc.SiteLocation;
                o["site"] = new JObject
                {
                    ["place"] = site.PlaceName,
                    ["latitude_deg"] = site.Latitude * 180 / Math.PI,
                    ["longitude_deg"] = site.Longitude * 180 / Math.PI,
                    ["elevation_ft"] = site.Elevation,
                };
            }
            catch { }
            var units = doc.GetUnits();
            o["length_unit"] = units.GetFormatOptions(SpecTypeId.Length).GetUnitTypeId().TypeId;
            o["levels"] = new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount();
            o["phases"] = new JArray(doc.Phases.Cast<Phase>().Select(p => p.Name));
            return o;
        }
    }
}
