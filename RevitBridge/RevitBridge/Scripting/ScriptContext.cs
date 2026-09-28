using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitBridge.Bridge;
using RevitBridge.Services;

namespace RevitBridge.Scripting
{
    /// <summary>
    /// The object execute_csharp scripts receive as <c>ctx</c>. Scripts are compiled as
    /// <c>public static object Run(ScriptContext ctx) { ... }</c> and whatever they return is
    /// serialized to JSON (ElementId → long, XYZ → [x,y,z], Element → {id,name,category}).
    /// </summary>
    public sealed class ScriptContext
    {
        private readonly List<string> _log = new List<string>();

        public Document Doc { get; }
        public UIDocument UIDoc { get; }
        public UIApplication UIApp { get; }
        public Application App => UIApp.Application;
        /// <summary>"read" or "write". In write mode the script runs inside an open Transaction.</summary>
        public string Mode { get; }
        public ScriptResult Result { get; } = new ScriptResult();

        internal IReadOnlyList<string> LogLines => _log;

        internal ScriptContext(UIApplication app, Document doc, UIDocument uidoc, string mode)
        {
            UIApp = app;
            Doc = doc;
            UIDoc = uidoc;
            Mode = mode;
        }

        /// <summary>Append a line to the "log" array in the response.</summary>
        public void Log(string message)
        {
            if (_log.Count < 1000) _log.Add(message ?? "");
        }

        /// <summary>Convert a value in the given unit (ft, in, mm, cm, m) to feet.</summary>
        public double ToFeet(double value, string unit) => UnitParser.ToFeet(value, unit);

        /// <summary>Parse a length string like 12'-6", 150 mm, 3.2 m into feet.</summary>
        public double ToFeet(string text) => UnitParser.ToFeet(text);

        /// <summary>Format feet with the document's length units.</summary>
        public string FormatLength(double feet) => UnitParser.FormatLength(Doc, feet);

        public Element Get(long id) => Doc.GetElement(new ElementId(id));

        public FilteredElementCollector Collect(BuiltInCategory cat) =>
            new FilteredElementCollector(Doc).OfCategory(cat).WhereElementIsNotElementType();
    }

    /// <summary>Helpers for shaping the returned data (ctx.Result.*).</summary>
    public sealed class ScriptResult
    {
        internal JObject Extra { get; } = new JObject();

        /// <summary>Attach an extra named output (returned under "outputs").</summary>
        public void Set(string key, object value) => Extra[key] = Json.ToToken(value);

        /// <summary>Basic description (id, name, category, type, level) of elements.</summary>
        public JArray Elements(IEnumerable<Element> elements, int max = 500) =>
            new JArray(elements.Take(max).Select(Describe.Basic));

        /// <summary>Elements with all instance parameters.</summary>
        public JArray ElementsWithParams(IEnumerable<Element> elements, int max = 200) =>
            new JArray(elements.Take(max).Select(e => { var o = Describe.Basic(e); o["parameters"] = Describe.Params(e); return o; }));

        /// <summary>Rows → a table {columns, rows}.</summary>
        public JObject Table(IEnumerable<string> columns, IEnumerable<IEnumerable<object>> rows) => new JObject
        {
            ["columns"] = new JArray(columns),
            ["rows"] = new JArray(rows.Select(r => new JArray(r.Select(Json.ToToken)))),
        };
    }
}
