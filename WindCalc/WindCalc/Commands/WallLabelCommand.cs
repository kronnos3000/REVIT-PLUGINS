using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WindCalc.Engine;

namespace WindCalc.Commands
{
    /// <summary>
    /// Ribbon command — "Label Walls".
    ///
    /// Delegates all logic to <see cref="WallLabelEngine.Run"/>.
    /// The command itself stays transaction-free so that the engine's
    /// interactive PickObject calls work without a suspended transaction.
    /// The engine opens its own transaction after picking is complete.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class WallLabelCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
            ref string message, ElementSet elements)
        {
            try
            {
                UIDocument uidoc = commandData.Application.ActiveUIDocument;
                WallLabelEngine.Run(uidoc);
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Label Walls \u2014 Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
