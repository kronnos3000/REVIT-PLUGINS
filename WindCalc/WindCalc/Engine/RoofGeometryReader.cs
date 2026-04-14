using System;
using System.Linq;
using Autodesk.Revit.DB;
using WindCalc.Models;

namespace WindCalc.Engine
{
    /// <summary>
    /// Reads roof elements from the active Revit document to auto-detect
    /// ridge height, eave height, roof pitch, and roof type.
    /// Results are pre-filled into the dialog where the user can override.
    /// </summary>
    public class RoofGeometryReader
    {
        private readonly Document _doc;

        public RoofGeometryReader(Document doc)
        {
            _doc = doc;
        }

        public BuildingData ReadRoofGeometry()
        {
            var result = new BuildingData { AutoDetected = false };

            try
            {
                var roofs = new FilteredElementCollector(_doc)
                    .OfClass(typeof(RoofBase))
                    .WhereElementIsNotElementType()
                    .Cast<RoofBase>()
                    .ToList();

                if (roofs.Count == 0)
                {
                    result.DetectionNote = "No roof elements found in model. Enter values manually.";
                    return result;
                }

                // Gather bounding boxes across all roofs
                double minZ = double.MaxValue;
                double maxZ = double.MinValue;

                foreach (var roof in roofs)
                {
                    var bb = roof.get_BoundingBox(null);
                    if (bb == null) continue;
                    if (bb.Min.Z < minZ) minZ = bb.Min.Z;
                    if (bb.Max.Z > maxZ) maxZ = bb.Max.Z;
                }

                if (minZ == double.MaxValue)
                {
                    result.DetectionNote = "Could not read roof bounding boxes.";
                    return result;
                }

                // Revit internal units are feet
                result.EaveHeightFt  = Math.Round(minZ, 2);
                result.RidgeHeightFt = Math.Round(maxZ, 2);

                // Roof type from first roof's type name
                var firstRoof = roofs[0];
                string typeName = firstRoof.RoofType?.Name ?? "";
                result.RoofType = ClassifyRoofType(typeName);

                // Pitch — try the "Slope" parameter first
                result.RoofPitch = ReadPitch(firstRoof);

                // Stories: estimate from eave height (rough heuristic: ~9 ft/story)
                result.Stories      = Math.Max(1, (int)Math.Round(result.EaveHeightFt / 9.0));
                result.AutoDetected = true;
                result.DetectionNote = $"Auto-detected from {roofs.Count} roof element(s). Verify and adjust as needed.";
            }
            catch (Exception ex)
            {
                result.DetectionNote = $"Auto-detection error: {ex.Message}. Enter values manually.";
            }

            return result;
        }

        private static string ClassifyRoofType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return "Unknown";
            string lower = typeName.ToLowerInvariant();
            if (lower.Contains("hip"))     return "Hip";
            if (lower.Contains("gable"))   return "Gable";
            if (lower.Contains("flat"))    return "Flat";
            if (lower.Contains("mansard")) return "Mansard";
            if (lower.Contains("shed"))    return "Shed";
            if (lower.Contains("gambrel")) return "Gambrel";
            return typeName; // return raw name if unrecognized
        }

        private static string ReadPitch(RoofBase roof)
        {
            try
            {
                // Try built-in slope parameter
                var slopeParam = roof.get_Parameter(BuiltInParameter.ROOF_SLOPE);
                if (slopeParam != null && slopeParam.HasValue)
                {
                    // Revit stores slope as rise/run (dimensionless ratio)
                    double slope = slopeParam.AsDouble();
                    // Convert from feet/foot to x:12 notation
                    double rise = Math.Round(slope * 12, 1);
                    return $"{rise}:12";
                }

                // Try "Slope Angle" parameter
                var angleParam = roof.LookupParameter("Slope Angle");
                if (angleParam != null && angleParam.HasValue)
                {
                    double angleDeg = angleParam.AsDouble() * (180.0 / Math.PI);
                    double rise = Math.Round(Math.Tan(angleDeg * Math.PI / 180.0) * 12, 1);
                    return $"{rise}:12";
                }
            }
            catch { /* fall through */ }

            return "N/A";
        }
    }
}
