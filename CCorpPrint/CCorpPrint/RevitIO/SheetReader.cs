using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace CCorpPrint.RevitIO
{
    public class SheetRow
    {
        public ViewSheet Sheet { get; set; }
        public string Number { get; set; }
        public string Name { get; set; }
        public string CurrentRevision { get; set; }
        public string IssueDate { get; set; }
        public string TitleblockSize { get; set; }
        public string Discipline { get; set; }
    }

    public class SheetReader
    {
        private readonly Document _doc;

        public SheetReader(Document doc) { _doc = doc; }

        public IList<SheetRow> Read()
        {
            var rows = new List<SheetRow>();
            var sheets = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(v => !v.IsTemplate && !v.IsPlaceholder)
                .OrderBy(v => v.SheetNumber, new NaturalSheetNumberComparer());

            foreach (var s in sheets)
            {
                var titleblock = new FilteredElementCollector(_doc, s.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .FirstElement() as FamilyInstance;

                rows.Add(new SheetRow
                {
                    Sheet            = s,
                    Number           = s.SheetNumber,
                    Name             = s.Name,
                    CurrentRevision  = SafeParam(s, BuiltInParameter.SHEET_CURRENT_REVISION),
                    IssueDate        = SafeParam(s, BuiltInParameter.SHEET_ISSUE_DATE),
                    TitleblockSize   = TitleblockSize(titleblock),
                    Discipline       = ExtractDiscipline(s.SheetNumber),
                });
            }

            return rows;
        }

        private static string SafeParam(Element e, BuiltInParameter bip)
        {
            try
            {
                var p = e.get_Parameter(bip);
                return p == null ? "" : (p.AsString() ?? p.AsValueString() ?? "");
            }
            catch { return ""; }
        }

        private static string TitleblockSize(FamilyInstance tb)
        {
            if (tb == null) return "";
            try
            {
                var symbol = tb.Symbol;
                var w = symbol.LookupParameter("Sheet Width");
                var h = symbol.LookupParameter("Sheet Height");
                if (w == null || h == null) return symbol.Name ?? "";
                double wIn = w.AsDouble() * 12.0;
                double hIn = h.AsDouble() * 12.0;
                return $"{wIn:F0}x{hIn:F0}";
            }
            catch { return ""; }
        }

        private static string ExtractDiscipline(string sheetNumber)
        {
            if (string.IsNullOrEmpty(sheetNumber)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var c in sheetNumber)
            {
                if (char.IsLetter(c)) sb.Append(c);
                else break;
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Sorts sheet numbers naturally so "A10" follows "A2", not "A100".
    /// </summary>
    internal class NaturalSheetNumberComparer : IComparer<string>
    {
        public int Compare(string x, string y)
        {
            x = x ?? ""; y = y ?? "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int xs = i, ys = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    long xn = long.Parse(x.Substring(xs, i - xs));
                    long yn = long.Parse(y.Substring(ys, j - ys));
                    if (xn != yn) return xn.CompareTo(yn);
                }
                else
                {
                    int cmp = x[i].CompareTo(y[j]);
                    if (cmp != 0) return cmp;
                    i++; j++;
                }
            }
            return x.Length - y.Length;
        }
    }
}
