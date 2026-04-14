using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WindCalc.Engine;
using WindCalc.Models;
using WindCalc.RevitIO;
using WindCalc.UI;

namespace WindCalc.Commands
{
    /// <summary>
    /// Main Wind Calculator command. Invoked when user clicks "Wind Calculator" in the ribbon.
    ///
    /// Workflow:
    ///   1. Load config (API key, firm minimum, local data paths)
    ///   2. Pre-read roof geometry from the active document
    ///   3. Show WindCalcWindow (3-tab dialog)
    ///   4. User fetches site data, reviews/adjusts values, clicks Apply
    ///   5. Write shared params, project info, and Wind Report sheet
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class WindCalcCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
            ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document   doc   = uiDoc.Document;

            try
            {
                // Load persisted config
                var config = WindCalcConfig.LoadOrDefault();

                // Pre-read roof geometry from model
                var engine       = new WindCalcEngine(config);
                var buildingData = engine.ReadRoofGeometry(doc);

                // Show dialog
                var dialog = new WindCalcWindow(config, buildingData);
                bool? result = dialog.ShowDialog();

                if (result != true || dialog.Result == null)
                    return Result.Cancelled;

                WindCalcResult calcResult = dialog.Result;

                // Write to Revit inside a single transaction group
                using var txGroup = new TransactionGroup(doc, "Wind Calculator — Apply Results");
                txGroup.Start();

                // 1. Shared parameters
                var spWriter = new SharedParamWriter(doc, config.SharedParamFilePath);
                spWriter.WriteResult(calcResult);

                // 2. Project Information built-in fields
                var piWriter = new ProjectInfoWriter(doc);
                piWriter.WriteResult(calcResult);

                // 3. Design Data drafting view
                View designDataView = null;
                using (var tx = new Transaction(doc, "Create Design Data View"))
                {
                    tx.Start();
                    var sheetBuilder = new WindReportSheetBuilder(doc);
                    designDataView = sheetBuilder.BuildOrUpdateSheet(calcResult);
                    tx.Commit();
                }

                // Navigate to the new view
                if (designDataView != null)
                    uiDoc.ActiveView = designDataView;

                txGroup.Assimilate();

                // Confirm to user
                string firmNote = calcResult.Site.FirmMinOverrideActive
                    ? $"\n\nFirm minimum applied: ASCE computed {calcResult.Site.AsceVultRcII:F0} mph → " +
                      $"{calcResult.AppliedVult:F0} mph per company standard."
                    : "";

                string sheetNote = designDataView != null
                    ? "\nDESIGN DATA drafting view created/updated."
                    : "";

                string windborneNote = calcResult.Site.WindborneDebrisArea
                    ? "\nWindborne Debris Region: YES"
                    : "";

                string env160Note = calcResult.Site.Envelope160MphRequired
                    ? $"\n160 MPH Envelope Required (FBC {calcResult.CodeEdition}th Ed SB 1218): YES"
                    : "";

                string editionNote = $"\nCode Edition: {(calcResult.CodeEdition == "9th" ? "FBC 9th 2026" : "FBC 8th 2023")}";

                TaskDialog.Show("Wind Calculator \u2014 Complete",
                    $"Wind analysis parameters written to project.\n\n" +
                    $"Applied Vult: {calcResult.AppliedVult:F0} mph (RC {calcResult.Site.RiskCategory})\n" +
                    $"Exposure Category: {calcResult.Site.ExposureCategory}\n" +
                    $"Ground Elevation: {calcResult.Site.ElevationFt:F1} ft NAVD88\n" +
                    $"Flood Zone: {calcResult.Site.FloodZone}\n" +
                    $"Roof Type: {calcResult.Building.RoofType} | Pitch: {calcResult.Building.RoofPitch}" +
                    windborneNote + env160Note + editionNote + firmNote + sheetNote);

                return Result.Succeeded;
            }
            catch (OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Wind Calculator — Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
