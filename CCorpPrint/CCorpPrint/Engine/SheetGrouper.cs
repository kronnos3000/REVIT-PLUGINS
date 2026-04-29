using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using CCorpPrint.Models;

namespace CCorpPrint.Engine
{
    /// <summary>
    /// Resolves the destination subfolder for a sheet based on the configured
    /// GroupBy setting and dated-subfolder option.
    /// </summary>
    public class SheetGrouper
    {
        private readonly GroupBy _groupBy;
        private readonly bool _datedSubfolder;
        private readonly string _baseFolder;

        public SheetGrouper(string baseFolder, GroupBy groupBy, bool datedSubfolder)
        {
            _baseFolder = baseFolder ?? "";
            _groupBy = groupBy;
            _datedSubfolder = datedSubfolder;
        }

        public string FolderFor(ViewSheet sheet)
        {
            string folder = _baseFolder;
            if (_datedSubfolder)
                folder = Path.Combine(folder, DateTime.Now.ToString("yyyy-MM-dd"));

            switch (_groupBy)
            {
                case GroupBy.Discipline:
                    folder = Path.Combine(folder, ExtractDiscipline(sheet.SheetNumber));
                    break;
                case GroupBy.Revision:
                    var rev = sheet.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString();
                    if (string.IsNullOrEmpty(rev)) rev = "_NoRev";
                    folder = Path.Combine(folder, rev);
                    break;
            }

            return folder;
        }

        private static string ExtractDiscipline(string sheetNumber)
        {
            if (string.IsNullOrEmpty(sheetNumber)) return "_";
            var sb = new System.Text.StringBuilder();
            foreach (var c in sheetNumber)
            {
                if (char.IsLetter(c)) sb.Append(c);
                else break;
            }
            return sb.Length == 0 ? "_" : sb.ToString();
        }
    }
}
