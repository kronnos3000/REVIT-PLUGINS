using Autodesk.Revit.DB;
using WindCalc.Models;

namespace WindCalc.RevitIO
{
    /// <summary>
    /// Writes key wind analysis values to Revit's built-in Project Information fields.
    /// These appear on title blocks and in project properties automatically.
    /// </summary>
    public class ProjectInfoWriter
    {
        private readonly Document _doc;

        public ProjectInfoWriter(Document doc)
        {
            _doc = doc;
        }

        public void WriteResult(WindCalcResult result)
        {
            using var tx = new Transaction(_doc, "Update Project Information — Wind Analysis");
            tx.Start();

            var pi = _doc.ProjectInformation;

            // Built-in Project Address
            SetBuiltInParam(pi, BuiltInParameter.PROJECT_ADDRESS,
                result.Site.MatchedAddress);

            // Built-in Project Status — wind summary
            SetBuiltInParam(pi, BuiltInParameter.PROJECT_STATUS,
                $"Vult={result.AppliedVult:F0} mph | Exp={result.ExposureCategory} | " +
                $"RC={result.RiskCategory} | Elev={result.ElevationFt:F1} ft | " +
                $"Flood={result.FloodZone}");

            // Custom "Client Name" param (may exist in firm's template or from PCPAO Sync tool).
            // Writes owner name when parcel data was successfully retrieved.
            if (!string.IsNullOrWhiteSpace(result.Site.ParcelOwner))
                SetNamedParam(pi, "Client Name", result.Site.ParcelOwner);

            tx.Commit();
        }

        private static void SetBuiltInParam(ProjectInfo pi, BuiltInParameter bip, string value)
        {
            try
            {
                var param = pi.get_Parameter(bip);
                if (param != null && !param.IsReadOnly && param.StorageType == StorageType.String)
                    param.Set(value ?? "");
            }
            catch { /* non-critical */ }
        }

        private static void SetNamedParam(ProjectInfo pi, string paramName, string value)
        {
            try
            {
                var param = pi.LookupParameter(paramName);
                if (param != null && !param.IsReadOnly && param.StorageType == StorageType.String)
                    param.Set(value ?? "");
            }
            catch { /* non-critical */ }
        }
    }
}
