using System.Collections.Generic;
using Autodesk.Revit.DB;
using CCorpPrint.Models;

namespace CCorpPrint.Engine
{
    /// <summary>
    /// Immutable description of one print job: target sheets,
    /// pre-resolved per-sheet filenames (no extension), output mode,
    /// destination, and combined-file name (if applicable).
    /// </summary>
    public class PrintJob
    {
        public string JobId { get; set; }
        public OutputMode Mode { get; set; }
        public string OutputFolder { get; set; }

        public IReadOnlyList<SheetEntry> Sheets { get; set; }

        // Combined-mode only:
        public string CombinedFileName { get; set; }

        // Physical-printer only:
        public string PrinterName { get; set; }

        public IList<ElementId> SheetIds
        {
            get
            {
                var ids = new List<ElementId>(Sheets.Count);
                foreach (var s in Sheets) ids.Add(s.Sheet.Id);
                return ids;
            }
        }
    }

    public class SheetEntry
    {
        public ViewSheet Sheet { get; set; }
        public string ResolvedFileName { get; set; }   // sanitized, no extension
        public IReadOnlyList<string> MissingTokens { get; set; }
    }
}
