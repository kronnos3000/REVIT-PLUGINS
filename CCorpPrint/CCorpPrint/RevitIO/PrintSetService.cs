using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace CCorpPrint.RevitIO
{
    /// <summary>
    /// Helpers for ViewSheetSet manipulation. The temporary set we create for
    /// physical printing is rolled back via the surrounding TransactionGroup so
    /// it never persists in the model.
    /// </summary>
    public class PrintSetService
    {
        private readonly Document _doc;

        public PrintSetService(Document doc) { _doc = doc; }

        public IList<ViewSheetSet> AllSets()
        {
            return new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewSheetSet))
                .Cast<ViewSheetSet>()
                .OrderBy(s => s.Name)
                .ToList();
        }

        public ViewSheetSet ByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return AllSets().FirstOrDefault(s =>
                string.Equals(s.Name, name, System.StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Configures the document's ViewSheetSetting with the given sheets and
        /// saves it under a temp name. Caller must be inside a Transaction.
        /// </summary>
        public IViewSheetSet ConfigureCurrentSheetSet(IList<ElementId> sheetIds, string tempName)
        {
            var pm = _doc.PrintManager;
            pm.PrintRange = PrintRange.Select;

            var setting = pm.ViewSheetSetting;
            var sheets = new ViewSet();
            foreach (var id in sheetIds)
            {
                if (_doc.GetElement(id) is ViewSheet vs) sheets.Insert(vs);
            }
            setting.CurrentViewSheetSet.Views = sheets;
            setting.SaveAs(tempName);
            return setting.CurrentViewSheetSet;
        }
    }
}
