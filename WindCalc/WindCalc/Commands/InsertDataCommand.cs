using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace WindCalc.Commands
{
    /// <summary>
    /// Inserts the "DESIGN DATA – Wind Analysis" drafting view onto the active sheet.
    /// User clicks a point on the sheet to place it.
    ///
    /// Requirements:
    ///   - Active view must be a sheet (ViewSheet).
    ///   - The DESIGN DATA drafting view must exist (run Wind Calculator first).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class InsertDataCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
            ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document   doc   = uiDoc.Document;

            // ── Must be on a sheet ────────────────────────────────────────────
            if (!(uiDoc.ActiveView is ViewSheet sheet))
            {
                TaskDialog.Show("Insert Data",
                    "Please open a sheet view before using Insert Data.\n\n" +
                    "Double-click a sheet in the Project Browser to open it.");
                return Result.Cancelled;
            }

            // ── Find the DESIGN DATA drafting view ────────────────────────────
            var dataView = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewDrafting))
                .Cast<ViewDrafting>()
                .FirstOrDefault(v => v.Name.StartsWith("DESIGN DATA"));

            if (dataView == null)
            {
                TaskDialog.Show("Insert Data",
                    "No \"DESIGN DATA\" drafting view found in this project.\n\n" +
                    "Run Wind Calculator first to generate the view, then come back to insert it.");
                return Result.Cancelled;
            }

            // ── Check it isn't already on this sheet ──────────────────────────
            bool alreadyPlaced = new FilteredElementCollector(doc)
                .OfClass(typeof(Viewport))
                .Cast<Viewport>()
                .Any(vp => vp.SheetId == sheet.Id && vp.ViewId == dataView.Id);

            if (alreadyPlaced)
            {
                var td = new TaskDialog("Insert Data")
                {
                    MainContent        = "The DESIGN DATA view is already placed on this sheet.\n\nDo you want to place a second copy?",
                    CommonButtons      = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton      = TaskDialogResult.No
                };
                if (td.Show() != TaskDialogResult.Yes)
                    return Result.Cancelled;
            }

            // ── Ask user to click a placement point on the sheet ──────────────
            XYZ placementPoint;
            try
            {
                placementPoint = uiDoc.Selection.PickPoint(
                    ObjectSnapTypes.None,
                    "Click to place the DESIGN DATA view  [Esc to cancel]");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            // ── Create the viewport ───────────────────────────────────────────
            try
            {
                using (var tx = new Transaction(doc, "Insert Design Data View"))
                {
                    tx.Start();
                    Viewport.Create(doc, sheet.Id, dataView.Id, placementPoint);
                    tx.Commit();
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
