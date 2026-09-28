using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitBridge.Services;

namespace RevitBridge.Bridge
{
    /// <summary>
    /// Everything a command needs for one call: the UIApplication, the resolved document,
    /// typed/unit-aware parameter readers, the transaction helper and the safety flags.
    /// Lives on the Revit main thread for exactly one request.
    /// </summary>
    public sealed class CommandContext
    {
        public const int SizeGuardLimit = 5000;

        public UIApplication UIApp { get; }
        public JObject P { get; }
        public string Method { get; }
        public bool DryRun { get; }
        public bool Confirm { get; }
        public bool DismissWarnings { get; }
        public FailureCollector Failures { get; }
        public List<string> Notes { get; } = new List<string>();

        private Document _doc;

        public CommandContext(UIApplication app, string method, JObject p)
        {
            UIApp = app;
            Method = method;
            P = p ?? new JObject();
            DryRun = Bool("dry_run", false);
            Confirm = Bool("confirm", false);
            DismissWarnings = Bool("dismiss_warnings", false);
            Failures = new FailureCollector(DismissWarnings);
        }

        // ── Documents ────────────────────────────────────────────────────────

        /// <summary>The target document: params.doc (title or path) or the active document.</summary>
        public Document Doc => _doc ?? (_doc = ResolveDoc(Str("doc")));

        public Document DocOrNull
        {
            get { try { return Doc; } catch (BridgeException) { return null; } }
        }

        public UIDocument UIDoc
        {
            get
            {
                var active = UIApp.ActiveUIDocument;
                if (active != null && active.Document.Equals(Doc)) return active;
                return new UIDocument(Doc);
            }
        }

        public bool IsActiveDoc => UIApp.ActiveUIDocument != null && UIApp.ActiveUIDocument.Document.Equals(Doc);

        public IEnumerable<Document> OpenDocuments() =>
            UIApp.Application.Documents.Cast<Document>().Where(d => !d.IsLinked);

        public Document ResolveDoc(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                var active = UIApp.ActiveUIDocument?.Document;
                if (active == null)
                    throw new BridgeException("NO_DOCUMENT", "No document is open in Revit.", "Use open_document first.");
                return active;
            }
            var docs = OpenDocuments().ToList();
            var k = key.Trim();
            var match = docs.FirstOrDefault(d => string.Equals(d.PathName, k, StringComparison.OrdinalIgnoreCase))
                     ?? docs.FirstOrDefault(d => string.Equals(d.Title, k, StringComparison.OrdinalIgnoreCase))
                     ?? docs.FirstOrDefault(d => string.Equals(Path.GetFileNameWithoutExtension(d.Title), Path.GetFileNameWithoutExtension(k), StringComparison.OrdinalIgnoreCase));
            if (match == null)
                throw new BridgeException("NOT_FOUND", $"No open document matches '{key}'.",
                    "Open documents: " + string.Join("; ", docs.Select(d => d.Title)));
            return match;
        }

        public static JObject DocInfo(Document d) => d == null ? null : new JObject
        {
            ["title"] = d.Title,
            ["path"] = string.IsNullOrEmpty(d.PathName) ? null : d.PathName,
            ["is_family"] = d.IsFamilyDocument,
            ["is_workshared"] = d.IsWorkshared,
            ["is_modified"] = d.IsModified,
        };

        // ── Parameter readers ────────────────────────────────────────────────

        public bool Has(string name) => P[name] != null && P[name].Type != JTokenType.Null;

        public string Str(string name, string def = null) => Has(name) ? P[name].ToString() : def;

        public string StrReq(string name) =>
            Has(name) ? P[name].ToString() : throw new BridgeException("BAD_PARAMS", $"Missing required parameter '{name}'.");

        public int Int(string name, int def) => Has(name) ? P[name].Value<int>() : def;
        public double Dbl(string name, double def) => Has(name) ? P[name].Value<double>() : def;
        public bool Bool(string name, bool def) => Has(name) ? P[name].Value<bool>() : def;

        /// <summary>Unit-aware length in feet (number = feet, or "12'-6\"", "150 mm", ...).</summary>
        public double Length(string name) =>
            Has(name) ? UnitParser.ToFeet(P[name]) : throw new BridgeException("BAD_PARAMS", $"Missing required length '{name}'.");

        public double? LengthOpt(string name) => Has(name) ? UnitParser.ToFeet(P[name]) : (double?)null;

        /// <summary>Angle in radians; bare numbers are degrees.</summary>
        public double Angle(string name, double defDeg = 0) =>
            Has(name) ? UnitParser.ToRadians(P[name]) : defDeg * Math.PI / 180.0;

        public List<string> StrList(string name) =>
            !Has(name) ? new List<string>() :
            P[name] is JArray a ? a.Select(t => t.ToString()).ToList() : new List<string> { P[name].ToString() };

        public List<ElementId> Ids(string name, bool required = true)
        {
            if (!Has(name))
            {
                if (required) throw new BridgeException("BAD_PARAMS", $"Missing required id list '{name}'.");
                return new List<ElementId>();
            }
            var tok = P[name];
            var list = tok is JArray arr ? arr.Select(ToId).ToList() : new List<ElementId> { ToId(tok) };
            return list;
        }

        public static ElementId ToId(JToken t)
        {
            if (t is JObject o && o["id"] != null) t = o["id"];
            try { return new ElementId(t.Value<long>()); }
            catch { throw new BridgeException("BAD_PARAMS", $"'{t}' is not an element id."); }
        }

        public JArray Arr(string name) => P[name] as JArray ?? new JArray();

        public JToken Tok(string name) => P[name];

        /// <summary>A point as [x,y,z] or {x,y,z}; each coordinate unit-aware (feet by default).</summary>
        public static XYZ ToXyz(JToken t, double defaultZ = 0)
        {
            if (t is JArray a && a.Count >= 2)
                return new XYZ(UnitParser.ToFeet(a[0]), UnitParser.ToFeet(a[1]), a.Count > 2 ? UnitParser.ToFeet(a[2]) : defaultZ);
            if (t is JObject o && o["x"] != null)
                return new XYZ(UnitParser.ToFeet(o["x"]), UnitParser.ToFeet(o["y"]), o["z"] != null ? UnitParser.ToFeet(o["z"]) : defaultZ);
            throw new BridgeException("BAD_PARAMS", $"'{t}' is not a point. Use [x,y,z] or {{x,y,z}} in feet (strings with units allowed).");
        }

        public XYZ Point(string name, double defaultZ = 0) =>
            Has(name) ? ToXyz(P[name], defaultZ) : throw new BridgeException("BAD_PARAMS", $"Missing required point '{name}'.");

        public XYZ PointOpt(string name) => Has(name) ? ToXyz(P[name]) : null;

        /// <summary>A line as {start,end} or [[x,y,z],[x,y,z]].</summary>
        public static Line ToLine(JToken t, double defaultZ = 0)
        {
            XYZ a, b;
            if (t is JObject o) { a = ToXyz(o["start"] ?? o["p0"], defaultZ); b = ToXyz(o["end"] ?? o["p1"], defaultZ); }
            else if (t is JArray arr && arr.Count == 2) { a = ToXyz(arr[0], defaultZ); b = ToXyz(arr[1], defaultZ); }
            else throw new BridgeException("BAD_PARAMS", $"'{t}' is not a line. Use {{start:[x,y,z], end:[x,y,z]}}.");
            if (a.DistanceTo(b) < 1e-6) throw new BridgeException("BAD_PARAMS", "Line start and end are the same point.");
            return Line.CreateBound(a, b);
        }

        /// <summary>A closed loop from a list of points (auto-closed) or a list of lines.</summary>
        public static CurveLoop ToLoop(JToken t, double z = 0)
        {
            var arr = t as JArray ?? throw new BridgeException("BAD_PARAMS", "Boundary must be a list of points or lines.");
            var loop = new CurveLoop();
            if (arr.Count > 0 && (arr[0] is JObject lo && lo["start"] != null))
            {
                foreach (var l in arr) loop.Append(ToLine(l, z));
                return loop;
            }
            var pts = arr.Select(p => ToXyz(p, z)).ToList();
            if (pts.Count >= 2 && pts[0].IsAlmostEqualTo(pts[pts.Count - 1])) pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3) throw new BridgeException("BAD_PARAMS", "A boundary needs at least 3 points.");
            for (int i = 0; i < pts.Count; i++)
                loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
            return loop;
        }

        // ── Lookups ──────────────────────────────────────────────────────────

        public Element Element(ElementId id)
        {
            var e = Doc.GetElement(id);
            if (e == null) throw new BridgeException("NOT_FOUND", $"Element {id.AsLong()} not found in '{Doc.Title}'.");
            return e;
        }

        public Element Element(JToken idTok) => Element(ToId(idTok));

        public List<Element> Elements(string name)
        {
            var ids = Ids(name);
            var missing = ids.Where(i => Doc.GetElement(i) == null).Select(i => i.AsLong()).ToList();
            if (missing.Count > 0)
                throw new BridgeException("NOT_FOUND", $"{missing.Count} element id(s) not found: {string.Join(", ", missing.Take(20))}");
            return ids.Select(i => Doc.GetElement(i)).ToList();
        }

        public Level Level(string nameOrId) => Lookup.Level(Doc, nameOrId);
        public Level LevelParam(string name) => Level(StrReq(name));
        public Level LevelOpt(string name) => Has(name) ? Level(Str(name)) : null;

        // ── Safety ───────────────────────────────────────────────────────────

        /// <summary>
        /// For destructive calls: without confirm=true, stop and return the preview instead.
        /// Nothing has been changed when this throws.
        /// </summary>
        public void RequireConfirm(JObject preview, string what)
        {
            if (Confirm) return;
            preview["requires_confirm"] = true;
            throw new BridgeException("CONFIRM_REQUIRED",
                $"{what} requires confirm=true. Nothing was changed; the preview is in result.",
                "Re-issue the same call with confirm=true once the user has agreed.", preview);
        }

        public void GuardSize(int count, string what)
        {
            if (count > SizeGuardLimit && !Confirm)
                throw new BridgeException("SIZE_GUARD",
                    $"{what} would touch {count} elements (limit {SizeGuardLimit} without confirm=true). Nothing was changed.",
                    "Narrow the selection, or pass confirm=true.");
        }

        public void Warn(string note) => Notes.Add(note);

        // ── Transactions ─────────────────────────────────────────────────────

        /// <summary>
        /// Runs <paramref name="body"/> in a Transaction with the bridge's failure handling.
        /// Called inside the per-call TransactionGroup that the handler owns, so all
        /// transactions of one call collapse into one undo step.
        /// </summary>
        public T Tx<T>(string name, Func<T> body, Document doc = null)
        {
            doc = doc ?? Doc;
            using (var t = new Transaction(doc, name))
            {
                var opts = t.GetFailureHandlingOptions();
                opts.SetFailuresPreprocessor(Failures);
                opts.SetClearAfterRollback(true);
                opts.SetForcedModalHandling(false);
                t.SetFailureHandlingOptions(opts);
                t.Start();
                T result;
                try { result = body(); }
                catch
                {
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    throw;
                }
                var status = t.Commit();
                if (status != TransactionStatus.Committed)
                {
                    var msgs = Failures.Errors.Select(e => (string)e["message"]).ToList();
                    throw new BridgeException("REVIT_ERROR",
                        $"Revit rolled back '{name}'" + (msgs.Count > 0 ? ": " + string.Join(" | ", msgs) : $" (status {status})."));
                }
                return result;
            }
        }

        public void Tx(string name, Action body, Document doc = null) => Tx<object>(name, () => { body(); return null; }, doc);
    }
}
