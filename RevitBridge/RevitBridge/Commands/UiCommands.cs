using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitBridge.Bridge;
using RevitBridge.Services;

namespace RevitBridge.Commands
{
    /// <summary>Selection, navigation and export. No model changes (exports that need a transaction roll it back).</summary>
    internal static class UiCommands
    {
        private static UIDocument ActiveUIDoc(CommandContext ctx)
        {
            if (!ctx.IsActiveDoc)
                throw new BridgeException("NOT_ACTIVE", $"'{ctx.Doc.Title}' is not the active document.", "activate_document first.");
            return ctx.UIApp.ActiveUIDocument;
        }

        [BridgeCommand("select")]
        public static object Select(CommandContext ctx)
        {
            var uidoc = ActiveUIDoc(ctx);
            var ids = ctx.Ids("ids", required: false).Where(i => ctx.Doc.GetElement(i) != null).ToList();
            uidoc.Selection.SetElementIds(ids);
            return new JObject { ["selected"] = ids.Count };
        }

        [BridgeCommand("zoom_to")]
        public static object ZoomTo(CommandContext ctx)
        {
            var uidoc = ActiveUIDoc(ctx);
            var ids = ctx.Ids("ids").Where(i => ctx.Doc.GetElement(i) != null).ToList();
            if (ids.Count == 0) throw new BridgeException("NOT_FOUND", "None of the ids exist.");
            if (ctx.Bool("select", true)) uidoc.Selection.SetElementIds(ids);
            uidoc.ShowElements(ids);
            return new JObject { ["zoomed_to"] = ids.Count, ["view"] = uidoc.ActiveView.Name };
        }

        [BridgeCommand("open_view")]
        public static object OpenView(CommandContext ctx)
        {
            var uidoc = ActiveUIDoc(ctx);
            var key = ctx.Str("id") ?? ctx.StrReq("name");
            var v = Lookup.View(ctx.Doc, key);
            uidoc.RequestViewChange(v);
            return new JObject { ["id"] = v.Id.AsLong(), ["name"] = v.Name, ["type"] = v.ViewType.ToString(), ["note"] = "View change is applied when Revit next goes idle." };
        }

        private static string OutFolder(CommandContext ctx, string sub)
        {
            var folder = ctx.Str("folder") ?? Path.Combine(BridgePaths.CapturesDir, sub);
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static List<View> ViewsParam(CommandContext ctx, string name, bool allowActive = true)
        {
            var keys = ctx.StrList(name);
            if (keys.Count == 0)
            {
                if (!allowActive || !ctx.IsActiveDoc) throw new BridgeException("BAD_PARAMS", $"Pass '{name}' (names or ids).");
                return new List<View> { ctx.UIApp.ActiveUIDocument.ActiveView };
            }
            return keys.Select(k => Lookup.View(ctx.Doc, k)).ToList();
        }

        private static List<string> NewFiles(string folder, DateTime since) =>
            Directory.GetFiles(folder).Where(f => File.GetLastWriteTimeUtc(f) >= since.AddSeconds(-1)).OrderBy(f => f).ToList();

        /// <summary>Renders one view to PNG/JPG and returns its path (plus base64 if under max_inline_kb).</summary>
        [BridgeCommand("capture_view")]
        public static object CaptureView(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var view = ctx.Has("view") ? Lookup.View(doc, ctx.Str("view")) : ViewsParam(ctx, "view")[0];
            var fmt = ctx.Str("format", "png").ToLowerInvariant();
            var width = ctx.Int("width", 1600);
            var folder = OutFolder(ctx, "images");
            var baseName = Path.Combine(folder, $"capture_{DateTime.Now:yyyyMMdd_HHmmss_fff}");
            var opts = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = baseName,
                PixelSize = width,
                FitDirection = FitDirectionType.Horizontal,
                ZoomType = ZoomFitType.FitToPage,
                ImageResolution = ImageResolution.DPI_150,
                HLRandWFViewsFileType = fmt == "jpg" || fmt == "jpeg" ? ImageFileType.JPEGMedium : ImageFileType.PNG,
                ShadowViewsFileType = fmt == "jpg" || fmt == "jpeg" ? ImageFileType.JPEGMedium : ImageFileType.PNG,
            };
            opts.SetViewsAndSheets(new List<ElementId> { view.Id });
            var t0 = DateTime.UtcNow;
            doc.ExportImage(opts);
            var file = Directory.GetFiles(folder, Path.GetFileName(baseName) + "*").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                       ?? NewFiles(folder, t0).LastOrDefault()
                       ?? throw new BridgeException("REVIT_ERROR", "Revit did not produce an image.");
            var o = new JObject { ["path"] = file, ["view"] = view.Name, ["bytes"] = new FileInfo(file).Length };
            var maxInline = ctx.Int("max_inline_kb", 400) * 1024;
            if (new FileInfo(file).Length <= maxInline)
            {
                o["base64"] = Convert.ToBase64String(File.ReadAllBytes(file));
                o["mime"] = fmt.StartsWith("jp") ? "image/jpeg" : "image/png";
            }
            return o;
        }

        [BridgeCommand("export_image")]
        public static object ExportImage(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var views = ViewsParam(ctx, "views");
            var folder = OutFolder(ctx, "images");
            var fmt = ctx.Str("format", "png").ToLowerInvariant();
            var fileType = fmt == "jpg" || fmt == "jpeg" ? ImageFileType.JPEGLossless : fmt == "tif" || fmt == "tiff" ? ImageFileType.TIFF : ImageFileType.PNG;
            var dpi = ctx.Int("dpi", 150);
            var opts = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(folder, ctx.Str("prefix", "export")),
                PixelSize = ctx.Int("width", 2400),
                FitDirection = FitDirectionType.Horizontal,
                ZoomType = ZoomFitType.FitToPage,
                ImageResolution = dpi >= 600 ? ImageResolution.DPI_600 : dpi >= 300 ? ImageResolution.DPI_300 : dpi >= 150 ? ImageResolution.DPI_150 : ImageResolution.DPI_72,
                HLRandWFViewsFileType = fileType,
                ShadowViewsFileType = fileType,
            };
            opts.SetViewsAndSheets(views.Select(v => v.Id).ToList());
            var t0 = DateTime.UtcNow;
            doc.ExportImage(opts);
            return new JObject { ["folder"] = folder, ["files"] = new JArray(NewFiles(folder, t0)) };
        }

        /// <summary>PDF export of sheets and/or views. naming: optional file name (combine) or a prefix.</summary>
        [BridgeCommand("export_pdf")]
        public static object ExportPdf(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var ids = new List<ElementId>();
            foreach (var s in ctx.StrList("sheets")) ids.Add(Lookup.Sheet(doc, s).Id);
            foreach (var v in ctx.StrList("views")) ids.Add(Lookup.View(doc, v).Id);
            if (ids.Count == 0) throw new BridgeException("BAD_PARAMS", "Pass 'sheets' (numbers/names/ids) and/or 'views'.");
            var folder = OutFolder(ctx, "pdf");
            var combine = ctx.Bool("combine", true);
            var opts = new PDFExportOptions { Combine = combine };
            var naming = ctx.Str("naming");
            if (combine) opts.FileName = naming ?? $"{Path.GetFileNameWithoutExtension(doc.Title)}_{DateTime.Now:yyyyMMdd_HHmmss}";
            var t0 = DateTime.UtcNow;
            var ok = doc.Export(folder, ids, opts);
            return new JObject { ["ok"] = ok, ["folder"] = folder, ["files"] = new JArray(NewFiles(folder, t0).Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))) };
        }

        [BridgeCommand("export_dwg")]
        public static object ExportDwg(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var views = ViewsParam(ctx, "views");
            var folder = OutFolder(ctx, "dwg");
            var setup = ctx.Str("setup");
            DWGExportOptions opts = null;
            if (!string.IsNullOrEmpty(setup))
            {
                opts = DWGExportOptions.GetPredefinedOptions(doc, setup)
                       ?? throw new BridgeException("NOT_FOUND", $"DWG export setup '{setup}' not found.",
                            "Setups: " + string.Join(", ", BaseExportOptions.GetPredefinedSetupNames(doc)));
            }
            opts = opts ?? new DWGExportOptions();
            opts.MergedViews = ctx.Bool("merged_views", true);
            var t0 = DateTime.UtcNow;
            var ok = doc.Export(folder, ctx.Str("naming", Path.GetFileNameWithoutExtension(doc.Title)), views.Select(v => v.Id).ToList(), opts);
            return new JObject { ["ok"] = ok, ["folder"] = folder, ["files"] = new JArray(NewFiles(folder, t0)) };
        }

        [BridgeCommand("export_ifc")]
        public static object ExportIfc(CommandContext ctx)
        {
            var doc = ctx.Doc;
            var folder = OutFolder(ctx, "ifc");
            var name = ctx.Str("naming", Path.GetFileNameWithoutExtension(doc.Title)) + ".ifc";
            var opts = new IFCExportOptions();
            var version = ctx.Str("version");
            if (version != null && Enum.TryParse(version, true, out IFCVersion iv)) opts.FileVersion = iv;
            if (ctx.Has("view")) opts.FilterViewId = Lookup.View(doc, ctx.Str("view")).Id;
            if (ctx.Tok("options") is JObject extra)
                foreach (var kv in extra) opts.AddOption(kv.Key, kv.Value.ToString());
            // IFC export must run inside a transaction; roll it back so the model is untouched.
            bool ok;
            using (var t = new Transaction(doc, "Claude: export_ifc"))
            {
                t.Start();
                try { ok = doc.Export(folder, name, opts); }
                finally { t.RollBack(); }
            }
            return new JObject { ["ok"] = ok, ["path"] = Path.Combine(folder, name) };
        }
    }
}
