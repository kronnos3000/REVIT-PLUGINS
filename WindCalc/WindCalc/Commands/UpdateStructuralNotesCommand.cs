using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WindCalc.UI;

namespace WindCalc.Commands
{
    /// <summary>
    /// Updates all code-edition references in the S-001 Structural Notes sheet.
    /// Finds every TextNote in every view placed on the sheet and performs
    /// a comprehensive ordered string-replacement based on the selected edition.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class UpdateStructuralNotesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
            ref string message, ElementSet elements)
        {
            try
            {
                var dialog = new UpdateStructuralNotesWindow(
                    commandData.Application.ActiveUIDocument.Document);
                dialog.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Update Structural Notes – Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
