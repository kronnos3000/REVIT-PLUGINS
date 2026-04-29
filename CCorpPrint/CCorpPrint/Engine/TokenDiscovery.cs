using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace CCorpPrint.Engine
{
    public enum TokenSource
    {
        BuiltIn,
        Sheet,
        ProjectInfo
    }

    public class TokenInfo
    {
        public string Name { get; set; }
        public TokenSource Source { get; set; }
        public string SampleValue { get; set; }
    }

    /// <summary>
    /// Enumerates all tokens available for a naming template:
    /// built-ins, sheet parameters (built-in + shared/project bound to OST_Sheets),
    /// and ProjectInfo parameters (built-in + shared bound to OST_ProjectInformation).
    /// </summary>
    public class TokenDiscovery
    {
        private static readonly string[] BuiltIns =
        {
            "Today", "Now", "Year", "Username",
            "ProjectFileName", "SheetCount", "Index", "JobId"
        };

        private readonly Document _doc;

        public TokenDiscovery(Document doc)
        {
            _doc = doc;
        }

        public IReadOnlyList<TokenInfo> Discover()
        {
            var result = new List<TokenInfo>();

            foreach (var b in BuiltIns)
                result.Add(new TokenInfo { Name = b, Source = TokenSource.BuiltIn, SampleValue = SampleForBuiltIn(b) });

            // Sheet params — pick a representative sheet
            var sheet = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .FirstOrDefault(v => !v.IsTemplate && !v.IsPlaceholder);

            var sheetNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (sheet != null)
            {
                foreach (Parameter p in sheet.Parameters)
                {
                    if (p?.Definition == null) continue;
                    var name = p.Definition.Name;
                    if (string.IsNullOrEmpty(name) || !sheetNames.Add(name)) continue;
                    result.Add(new TokenInfo
                    {
                        Name = name,
                        Source = TokenSource.Sheet,
                        SampleValue = SafeString(p)
                    });
                }
            }

            // Walk binding map for shared params bound to OST_Sheets that may not appear
            // on the representative sheet (e.g. sheet has no value yet).
            AddBoundButUnseen(result, sheetNames, BuiltInCategory.OST_Sheets, TokenSource.Sheet);

            // ProjectInformation params
            var pInfoNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var pInfo = _doc.ProjectInformation;
            if (pInfo != null)
            {
                foreach (Parameter p in pInfo.Parameters)
                {
                    if (p?.Definition == null) continue;
                    var name = p.Definition.Name;
                    if (string.IsNullOrEmpty(name) || !pInfoNames.Add(name)) continue;
                    if (sheetNames.Contains(name)) continue;
                    result.Add(new TokenInfo
                    {
                        Name = name,
                        Source = TokenSource.ProjectInfo,
                        SampleValue = SafeString(p)
                    });
                }
            }
            AddBoundButUnseen(result, pInfoNames, BuiltInCategory.OST_ProjectInformation, TokenSource.ProjectInfo);

            return result;
        }

        private void AddBoundButUnseen(
            List<TokenInfo> result,
            HashSet<string> alreadySeen,
            BuiltInCategory targetCat,
            TokenSource source)
        {
            var bindings = _doc.ParameterBindings;
            var it = bindings.ForwardIterator();
            it.Reset();
            while (it.MoveNext())
            {
                var def = it.Key as Definition;
                if (def == null) continue;
                if (alreadySeen.Contains(def.Name)) continue;

                var binding = it.Current as ElementBinding;
                if (binding == null) continue;

                bool matches = false;
                foreach (Category c in binding.Categories)
                {
                    try
                    {
                        if (c.Id.AsLong() == (long)targetCat) { matches = true; break; }
                    }
                    catch { /* category may not have an integer id in some cases */ }
                }
                if (!matches) continue;

                alreadySeen.Add(def.Name);
                result.Add(new TokenInfo
                {
                    Name = def.Name,
                    Source = source,
                    SampleValue = ""
                });
            }
        }

        private string SampleForBuiltIn(string name)
        {
            switch (name)
            {
                case "Today":           return System.DateTime.Today.ToString("yyyy-MM-dd");
                case "Now":             return System.DateTime.Now.ToString("yyyy-MM-dd_HHmm");
                case "Year":            return System.DateTime.Now.Year.ToString();
                case "Username":        return System.Environment.UserName;
                case "ProjectFileName": return string.IsNullOrEmpty(_doc.PathName)
                                                ? _doc.Title
                                                : System.IO.Path.GetFileNameWithoutExtension(_doc.PathName);
                case "SheetCount":      return "(int)";
                case "Index":           return "1";
                case "JobId":           return "abcd1234";
                default:                return "";
            }
        }

        private static string SafeString(Parameter p)
        {
            try
            {
                if (p == null || !p.HasValue) return "";
                switch (p.StorageType)
                {
                    case StorageType.String:  return p.AsString() ?? "";
                    case StorageType.Integer: return p.AsInteger().ToString();
                    case StorageType.Double:  return p.AsValueString() ?? p.AsDouble().ToString("F2");
                    case StorageType.ElementId:
                        var id = p.AsElementId();
                        return id == null ? "" : id.AsLong().ToString();
                    default: return p.AsValueString() ?? "";
                }
            }
            catch { return ""; }
        }
    }
}
