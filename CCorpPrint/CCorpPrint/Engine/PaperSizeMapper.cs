using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using CCorpPrint.Models;

namespace CCorpPrint.Engine
{
    public class PaperSizeMapper
    {
        private readonly Document _doc;
        private readonly IList<PaperSizeRule> _rules;

        public PaperSizeMapper(Document doc, IList<PaperSizeRule> rules)
        {
            _doc = doc;
            _rules = rules ?? new List<PaperSizeRule>();
        }

        /// <summary>
        /// Returns a printer paper-size name for the given sheet, by looking
        /// at its titleblock symbol's Sheet Width / Sheet Height type params,
        /// or null if no rule matches.
        /// </summary>
        public string Resolve(ViewSheet sheet)
        {
            var titleblock = new FilteredElementCollector(_doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstElement() as FamilyInstance;

            if (titleblock?.Symbol == null) return null;

            var w = titleblock.Symbol.LookupParameter("Sheet Width");
            var h = titleblock.Symbol.LookupParameter("Sheet Height");
            if (w == null || h == null) return null;

            double wIn = w.AsDouble() * 12.0;
            double hIn = h.AsDouble() * 12.0;

            return _rules.FirstOrDefault(r => r.Matches(wIn, hIn))?.PaperSizeName;
        }
    }
}
