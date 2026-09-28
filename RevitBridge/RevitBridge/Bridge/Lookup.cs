using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitBridge.Services;

namespace RevitBridge.Bridge
{
    /// <summary>
    /// Name-or-id resolution for levels, views, categories and types. Every lookup accepts
    /// an element id (as text) or a name; types also accept "Family Name: Type Name".
    /// Misses throw NOT_FOUND with a short list of candidates.
    /// </summary>
    public static class Lookup
    {
        public static bool TryId(string s, out ElementId id)
        {
            id = ElementId.InvalidElementId;
            if (long.TryParse(s?.Trim(), out var v)) { id = new ElementId(v); return true; }
            return false;
        }

        public static Level Level(Document doc, string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId)) throw new BridgeException("BAD_PARAMS", "Level name or id is required.");
            if (TryId(nameOrId, out var id) && doc.GetElement(id) is Level byId) return byId;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            return levels.FirstOrDefault(l => string.Equals(l.Name, nameOrId.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new BridgeException("NOT_FOUND", $"Level '{nameOrId}' not found.",
                       "Levels: " + string.Join(", ", levels.OrderBy(l => l.Elevation).Select(l => l.Name)));
        }

        public static View View(Document doc, string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId)) throw new BridgeException("BAD_PARAMS", "View name or id is required.");
            if (TryId(nameOrId, out var id) && doc.GetElement(id) is View byId) return byId;
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
            var k = nameOrId.Trim();
            var hit = views.FirstOrDefault(v => string.Equals(v.Name, k, StringComparison.OrdinalIgnoreCase))
                   ?? views.OfType<ViewSheet>().FirstOrDefault(s => string.Equals(s.SheetNumber, k, StringComparison.OrdinalIgnoreCase))
                   ?? views.FirstOrDefault(v => string.Equals($"{v.ViewType}: {v.Name}", k, StringComparison.OrdinalIgnoreCase));
            return hit ?? throw new BridgeException("NOT_FOUND", $"View '{nameOrId}' not found.", "Use list_views to see names.");
        }

        public static ViewSheet Sheet(Document doc, string numberNameOrId)
        {
            if (TryId(numberNameOrId, out var id) && doc.GetElement(id) is ViewSheet byId) return byId;
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            var k = numberNameOrId?.Trim();
            return sheets.FirstOrDefault(s => string.Equals(s.SheetNumber, k, StringComparison.OrdinalIgnoreCase))
                ?? sheets.FirstOrDefault(s => string.Equals(s.Name, k, StringComparison.OrdinalIgnoreCase))
                ?? throw new BridgeException("NOT_FOUND", $"Sheet '{numberNameOrId}' not found.", "Use list_sheets.");
        }

        /// <summary>Category by BuiltInCategory name ("OST_Walls"), plain name ("Walls"), or id.</summary>
        public static Category Category(Document doc, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new BridgeException("BAD_PARAMS", "Category is required.");
            var k = key.Trim();
            if (TryId(k, out var id))
            {
                var c = Autodesk.Revit.DB.Category.GetCategory(doc, id);
                if (c != null) return c;
            }
            var bicName = k.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? k : "OST_" + k.Replace(" ", "");
            if (Enum.TryParse(bicName, true, out BuiltInCategory bic))
            {
                try
                {
                    var c = Autodesk.Revit.DB.Category.GetCategory(doc, bic);
                    if (c != null) return c;
                }
                catch { }
            }
            foreach (Category c in doc.Settings.Categories)
            {
                if (string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase)) return c;
                foreach (Category sub in c.SubCategories)
                    if (string.Equals(sub.Name, k, StringComparison.OrdinalIgnoreCase)) return sub;
            }
            throw new BridgeException("NOT_FOUND", $"Category '{key}' not found.",
                "Use a display name like 'Walls', 'Structural Framing', or a BuiltInCategory like 'OST_StructuralColumns'.");
        }

        public static BuiltInCategory Bic(Category c) => (BuiltInCategory)c.Id.AsLong();

        /// <summary>
        /// Resolve an ElementType by id, "Family: Type", or bare type name, optionally
        /// restricted to a category and/or a .NET class.
        /// </summary>
        public static T Type<T>(Document doc, string key, BuiltInCategory? cat = null) where T : ElementType
        {
            if (string.IsNullOrWhiteSpace(key)) throw new BridgeException("BAD_PARAMS", "Type name or id is required.");
            if (TryId(key, out var id) && doc.GetElement(id) is T byId) return byId;
            var col = new FilteredElementCollector(doc).WhereElementIsElementType().OfClass(typeof(T));
            if (cat.HasValue) col = col.OfCategory(cat.Value);
            var all = col.Cast<T>().ToList();
            var k = key.Trim();
            string fam = null, typ = k;
            var colon = k.IndexOf(':');
            if (colon > 0) { fam = k.Substring(0, colon).Trim(); typ = k.Substring(colon + 1).Trim(); }

            IEnumerable<T> hits = all.Where(t => string.Equals(t.Name, typ, StringComparison.OrdinalIgnoreCase));
            if (fam != null) hits = hits.Where(t => string.Equals(t.FamilyName, fam, StringComparison.OrdinalIgnoreCase));
            var list = hits.ToList();
            if (list.Count == 0 && fam == null)
                list = all.Where(t => string.Equals($"{t.FamilyName}: {t.Name}", k, StringComparison.OrdinalIgnoreCase)).ToList();
            if (list.Count == 1) return list[0];
            if (list.Count > 1)
                throw new BridgeException("AMBIGUOUS", $"'{key}' matches {list.Count} types.",
                    "Use 'Family: Type' or an id. Matches: " + string.Join("; ", list.Take(10).Select(t => $"{t.FamilyName}: {t.Name} ({t.Id.AsLong()})")));
            throw new BridgeException("NOT_FOUND", $"Type '{key}' not found" + (cat.HasValue ? $" in {cat.Value}" : "") + ".",
                "Candidates: " + string.Join("; ", all.Take(15).Select(t => $"{t.FamilyName}: {t.Name}")));
        }

        public static FamilySymbol Symbol(Document doc, string key, BuiltInCategory? cat = null) => Type<FamilySymbol>(doc, key, cat);

        public static bool IsPlanLike(View v) =>
            v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan ||
            v.ViewType == ViewType.EngineeringPlan || v.ViewType == ViewType.AreaPlan;
    }
}
