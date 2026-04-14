using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WindCalc.Models;
using WindCalc.UI;

namespace WindCalc.Commands
{
    /// <summary>
    /// Opens the Setup Data window which automates downloading of:
    ///   - FEMA NFHL flood zone shapefiles (5 counties)
    ///   - Pinellas County parcel shapefile
    /// Run once after installation, then keep updated periodically.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class SetupLocalDataCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
            ref string message, ElementSet elements)
        {
            try
            {
                var config = WindCalcConfig.LoadOrDefault();
                var dialog = new SetupDataWindow(config);
                dialog.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Wind Calculator – Setup Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
