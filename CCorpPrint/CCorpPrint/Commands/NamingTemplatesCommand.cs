using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CCorpPrint.Models;
using CCorpPrint.UI;

namespace CCorpPrint.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class NamingTemplatesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) { message = "No active document."; return Result.Failed; }

            try
            {
                var cfg = PrintConfig.LoadOrDefault();
                var win = new NamingTemplatesWindow(doc, cfg);
                win.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("CCorp Print — Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
