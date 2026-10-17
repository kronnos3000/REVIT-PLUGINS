using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using WindCalc.RevitIO;

namespace WindCalc.Services
{
    /// <summary>
    /// Calculates wind zone assignment (zone 4 or 5) for windows and doors
    /// based on proximity to building corners.
    ///
    /// Zone 5 = opening is within distance 'a' of any building corner
    /// Zone 4 = opening is further than 'a' from all building corners
    ///
    /// Per ASCE 7-22, 'a' is the lesser of:
    /// - 10% of least horizontal dimension of building, or
    /// - 0.4 × mean roof height h
    /// but not less than 4% of least horizontal dimension or 3 feet.
    /// </summary>
    public class ZoneAssignmentEngine
    {
        private readonly Document _doc;
        private readonly double _edgeDistanceA; // in feet

        public ZoneAssignmentEngine(Document doc, double edgeDistanceA)
        {
            _doc = doc;
            _edgeDistanceA = edgeDistanceA;
        }

        /// <summary>
        /// Calculates the wind zone (4 or 5) for each window and door in the model.
        /// Returns a dictionary of ElementId -> zone number.
        /// </summary>
        public Dictionary<ElementId, int> CalculateAllZones()
        {
            var results = new Dictionary<ElementId, int>();

            // Get building corners from walls
            var corners = GetBuildingCorners();

            // Process all windows and doors
            var collector = new FilteredElementCollector(_doc)
                .WhereElementIsNotElementType()
                .WherePasses(new LogicalOrFilter(
                    new ElementCategoryFilter(BuiltInCategory.OST_Windows),
                    new ElementCategoryFilter(BuiltInCategory.OST_Doors)));

            foreach (var element in collector)
            {
                var zone = CalculateZoneForElement(element, corners);
                results[element.Id] = zone;
            }

            return results;
        }

        /// <summary>
        /// Calculates the wind zone for a single element.
        /// </summary>
        public int CalculateZoneForElement(Element element, List<XYZ> corners = null)
        {
            corners ??= GetBuildingCorners();

            // Get element location
            var location = GetElementLocation(element);
            if (location == null)
                return 4; // Default to zone 4 if we can't determine location

            // Check if within distance 'a' of any corner
            // Only check XY distance (horizontal plane)
            foreach (var corner in corners)
            {
                var horizontalDistance = Math.Sqrt(
                    Math.Pow(location.X - corner.X, 2) +
                    Math.Pow(location.Y - corner.Y, 2));

                if (horizontalDistance <= _edgeDistanceA)
                    return 5; // Zone 5 - near corner
            }

            return 4; // Zone 4 - field
        }

        /// <summary>
        /// Writes zone assignments to all windows and doors.
        /// </summary>
        public int WriteZonesToModel(Dictionary<ElementId, int> zones)
        {
            int count = 0;

            foreach (var kvp in zones)
            {
                var element = _doc.GetElement(kvp.Key);
                if (element == null) continue;

                var param = element.LookupParameter(A2SharedParams.Names.Wind_Zone);
                if (A2SharedParamBinder.SetParameterValue(param, kvp.Value))
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Writes required DP values based on zone assignment.
        /// Zone 4 uses Z4 ASD pressures, Zone 5 uses Z5 ASD pressures.
        /// </summary>
        public int WriteRequiredDP(Dictionary<ElementId, int> zones, double z4PosASD, double z4NegASD, double z5PosASD, double z5NegASD)
        {
            int count = 0;

            foreach (var kvp in zones)
            {
                var element = _doc.GetElement(kvp.Key);
                if (element == null) continue;

                var zone = kvp.Value;
                var reqPos = zone == 5 ? z5PosASD : z4PosASD;
                var reqNeg = zone == 5 ? z5NegASD : z4NegASD;

                var paramPos = element.LookupParameter(A2SharedParams.Names.DP_Req_Pos);
                var paramNeg = element.LookupParameter(A2SharedParams.Names.DP_Req_Neg);

                if (A2SharedParamBinder.SetParameterValue(paramPos, reqPos) &&
                    A2SharedParamBinder.SetParameterValue(paramNeg, reqNeg))
                    count++;
            }

            return count;
        }

        private XYZ GetElementLocation(Element element)
        {
            // Try to get location from LocationPoint
            if (element.Location is LocationPoint locPoint)
                return locPoint.Point;

            // Try to get location from LocationCurve (center)
            if (element.Location is LocationCurve locCurve)
            {
                var curve = locCurve.Curve;
                return curve.Evaluate(0.5, true); // Midpoint
            }

            // Try to get from bounding box center
            var bbox = element.get_BoundingBox(null);
            if (bbox != null)
            {
                return new XYZ(
                    (bbox.Min.X + bbox.Max.X) / 2,
                    (bbox.Min.Y + bbox.Max.Y) / 2,
                    (bbox.Min.Z + bbox.Max.Z) / 2);
            }

            return null;
        }

        /// <summary>
        /// Gets building corner points from exterior walls.
        /// Corners are where two exterior walls meet.
        /// </summary>
        private List<XYZ> GetBuildingCorners()
        {
            var corners = new List<XYZ>();

            // Get all exterior walls
            var wallCollector = new FilteredElementCollector(_doc)
                .OfClass(typeof(Wall))
                .WhereElementIsNotElementType()
                .Cast<Wall>()
                .Where(w => IsExteriorWall(w))
                .ToList();

            if (wallCollector.Count == 0)
            {
                // Fallback: use all walls if no exterior walls identified
                wallCollector = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Wall))
                    .WhereElementIsNotElementType()
                    .Cast<Wall>()
                    .ToList();
            }

            // Collect wall endpoints
            var endpoints = new List<XYZ>();
            foreach (var wall in wallCollector)
            {
                if (wall.Location is LocationCurve locCurve)
                {
                    var curve = locCurve.Curve;
                    endpoints.Add(curve.GetEndPoint(0));
                    endpoints.Add(curve.GetEndPoint(1));
                }
            }

            // Find corners: points where multiple walls meet
            // Group endpoints by proximity and count
            var tolerance = 0.5; // feet
            var pointCounts = new Dictionary<XYZ, int>(new XYZApproxComparer(tolerance));

            foreach (var pt in endpoints)
            {
                var normalizedPt = new XYZ(pt.X, pt.Y, 0); // Project to XY plane

                bool found = false;
                foreach (var key in pointCounts.Keys.ToList())
                {
                    if (key.DistanceTo(normalizedPt) < tolerance)
                    {
                        pointCounts[key]++;
                        found = true;
                        break;
                    }
                }

                if (!found)
                    pointCounts[normalizedPt] = 1;
            }

            // Corners are where 2+ walls meet
            foreach (var kvp in pointCounts)
            {
                if (kvp.Value >= 2)
                    corners.Add(kvp.Key);
            }

            return corners;
        }

        private bool IsExteriorWall(Wall wall)
        {
            // Check Wall_Role parameter
            var roleParam = wall.WallType.LookupParameter("Wall_Role");
            if (roleParam != null && roleParam.AsString() == "EXTERIOR")
                return true;

            // Check built-in function
            var funcParam = wall.get_Parameter(BuiltInParameter.FUNCTION_PARAM);
            if (funcParam != null)
            {
                var func = (WallFunction)funcParam.AsInteger();
                return func == WallFunction.Exterior;
            }

            return false;
        }

        /// <summary>
        /// Comparer for XYZ points with tolerance.
        /// </summary>
        private class XYZApproxComparer : IEqualityComparer<XYZ>
        {
            private readonly double _tolerance;

            public XYZApproxComparer(double tolerance)
            {
                _tolerance = tolerance;
            }

            public bool Equals(XYZ x, XYZ y)
            {
                if (x == null || y == null) return false;
                return x.DistanceTo(y) < _tolerance;
            }

            public int GetHashCode(XYZ obj)
            {
                // Use a grid-based hash to group nearby points
                int gridX = (int)(obj.X / _tolerance);
                int gridY = (int)(obj.Y / _tolerance);
                return gridX.GetHashCode() ^ (gridY.GetHashCode() << 16);
            }
        }
    }
}
