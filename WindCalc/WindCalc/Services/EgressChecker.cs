using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using WindCalc.RevitIO;

namespace WindCalc.Services
{
    /// <summary>
    /// Checks windows and doors for egress compliance per FBC-R R310.
    ///
    /// Emergency escape opening requirements (FBC 2023 Residential):
    /// - Net clear opening >= 5.7 SF (5.0 SF for grade-floor openings)
    /// - Minimum clear height >= 24"
    /// - Minimum clear width >= 20"
    /// - Maximum sill height <= 44" above finished floor
    ///
    /// Only applies to openings flagged as designated emergency escape openings.
    /// Fixed glass, non-opening products, and garage doors are N/A.
    /// </summary>
    public class EgressChecker
    {
        private readonly Document _doc;

        // FBC-R R310 requirements (inches and square feet)
        public const double MinClearAreaSF = 5.7;        // Standard
        public const double MinClearAreaGradeSF = 5.0;   // Grade-floor exception
        public const double MinClearHeightIn = 24.0;
        public const double MinClearWidthIn = 20.0;
        public const double MaxSillHeightIn = 44.0;

        public EgressChecker(Document doc)
        {
            _doc = doc;
        }

        /// <summary>
        /// Result of an egress check for a single element.
        /// </summary>
        public class CheckResult
        {
            public ElementId ElementId { get; set; }
            public string Mark { get; set; }
            public bool IsEgressRequired { get; set; }
            public bool IsGradeFloor { get; set; }

            // Measured values
            public double ClearWidthIn { get; set; }
            public double ClearHeightIn { get; set; }
            public double ClearAreaSF { get; set; }
            public double SillHeightIn { get; set; }

            // Required values
            public double ReqClearAreaSF { get; set; }
            public double ReqClearWidthIn { get; set; } = MinClearWidthIn;
            public double ReqClearHeightIn { get; set; } = MinClearHeightIn;
            public double MaxSillIn { get; set; } = MaxSillHeightIn;

            // Check results
            public bool PassArea { get; set; }
            public bool PassWidth { get; set; }
            public bool PassHeight { get; set; }
            public bool PassSill { get; set; }

            public bool Pass => !IsEgressRequired || (PassArea && PassWidth && PassHeight && PassSill);

            public EgressStatus Status
            {
                get
                {
                    if (!IsEgressRequired) return EgressStatus.NotApplicable;
                    return Pass ? EgressStatus.Pass : EgressStatus.Fail;
                }
            }

            public string StatusText => Status switch
            {
                EgressStatus.Pass => "PASS",
                EgressStatus.Fail => "FAIL",
                _ => "N/A"
            };

            public string StatusSymbol => Status switch
            {
                EgressStatus.Pass => "✓ PASS",   // ✓ PASS
                EgressStatus.Fail => "△ FAIL",   // △ FAIL
                _ => "N/A"
            };

            /// <summary>
            /// Returns a list of failed criteria for reporting.
            /// </summary>
            public List<string> GetFailedCriteria()
            {
                var failed = new List<string>();
                if (IsEgressRequired)
                {
                    if (!PassArea) failed.Add($"Clear area {ClearAreaSF:F1} SF < {ReqClearAreaSF:F1} SF");
                    if (!PassWidth) failed.Add($"Clear width {ClearWidthIn:F1}\" < {ReqClearWidthIn:F0}\"");
                    if (!PassHeight) failed.Add($"Clear height {ClearHeightIn:F1}\" < {ReqClearHeightIn:F0}\"");
                    if (!PassSill) failed.Add($"Sill height {SillHeightIn:F1}\" > {MaxSillIn:F0}\"");
                }
                return failed;
            }
        }

        public enum EgressStatus
        {
            Pass,
            Fail,
            NotApplicable
        }

        /// <summary>
        /// Checks all windows and doors for egress compliance.
        /// </summary>
        public List<CheckResult> CheckAll()
        {
            var results = new List<CheckResult>();

            var collector = new FilteredElementCollector(_doc)
                .WhereElementIsNotElementType()
                .WherePasses(new LogicalOrFilter(
                    new ElementCategoryFilter(BuiltInCategory.OST_Windows),
                    new ElementCategoryFilter(BuiltInCategory.OST_Doors)));

            foreach (var element in collector)
            {
                var result = CheckElement(element);
                if (result != null)
                    results.Add(result);
            }

            return results;
        }

        /// <summary>
        /// Checks a single element for egress compliance.
        /// </summary>
        public CheckResult CheckElement(Element element)
        {
            if (element == null) return null;

            var elementType = _doc.GetElement(element.GetTypeId());
            if (elementType == null) return null;

            var result = new CheckResult
            {
                ElementId = element.Id,
                Mark = GetMark(element)
            };

            // Check if egress is required for this opening
            result.IsEgressRequired = GetBoolParam(element, A2SharedParams.Names.Egress_Required, false);
            result.IsGradeFloor = GetBoolParam(element, A2SharedParams.Names.Egress_GradeFloor, false);

            // Set required clear area based on floor level
            result.ReqClearAreaSF = result.IsGradeFloor ? MinClearAreaGradeSF : MinClearAreaSF;

            // Get clear dimensions from type (in feet, convert to inches for width/height)
            result.ClearWidthIn = GetLengthParamInches(elementType, A2SharedParams.Names.Clear_Width);
            result.ClearHeightIn = GetLengthParamInches(elementType, A2SharedParams.Names.Clear_Height);

            // Get clear area (in SF)
            var clearAreaParam = elementType.LookupParameter(A2SharedParams.Names.Clear_Area);
            if (clearAreaParam != null && clearAreaParam.HasValue)
            {
                // Area params are stored in square feet internally
                result.ClearAreaSF = clearAreaParam.AsDouble();
            }
            else
            {
                // Compute from width x height if not stored
                result.ClearAreaSF = (result.ClearWidthIn * result.ClearHeightIn) / 144.0;
            }

            // Get sill height from instance (convert to inches)
            result.SillHeightIn = GetSillHeight(element);

            // Perform checks
            result.PassArea = result.ClearAreaSF >= result.ReqClearAreaSF;
            result.PassWidth = result.ClearWidthIn >= MinClearWidthIn;
            result.PassHeight = result.ClearHeightIn >= MinClearHeightIn;
            result.PassSill = result.SillHeightIn <= MaxSillHeightIn;

            return result;
        }

        /// <summary>
        /// Writes egress check status to all elements.
        /// </summary>
        public int WriteStatusToModel(List<CheckResult> results)
        {
            int count = 0;

            foreach (var result in results)
            {
                var element = _doc.GetElement(result.ElementId);
                if (element == null) continue;

                var param = element.LookupParameter(A2SharedParams.Names.Egress_Status);
                if (A2SharedParamBinder.SetParameterValue(param, result.StatusSymbol))
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Gets the sill height in inches.
        /// Tries the built-in Sill Height parameter first.
        /// </summary>
        private double GetSillHeight(Element element)
        {
            // Try built-in Sill Height parameter (windows)
            var sillParam = element.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            if (sillParam != null && sillParam.HasValue)
            {
                // Built-in sill height is in feet
                return sillParam.AsDouble() * 12.0;
            }

            // Fallback: try custom parameter or default
            // For doors, sill is typically 0 (at floor level)
            var category = (BuiltInCategory)element.Category.Id.Value;
            if (category == BuiltInCategory.OST_Doors)
                return 0;

            // Default: assume 44" (at limit) if we can't determine
            return MaxSillHeightIn;
        }

        private string GetMark(Element element)
        {
            var markParam = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            return markParam?.AsString() ?? element.Id.ToString();
        }

        private bool GetBoolParam(Element element, string paramName, bool defaultValue = false)
        {
            var param = element.LookupParameter(paramName);
            if (param != null && param.StorageType == StorageType.Integer)
                return param.AsInteger() != 0;
            return defaultValue;
        }

        private double GetLengthParamInches(Element element, string paramName)
        {
            var param = element.LookupParameter(paramName);
            if (param != null && param.StorageType == StorageType.Double)
            {
                // Length params are stored in feet; convert to inches
                return param.AsDouble() * 12.0;
            }
            return 0;
        }
    }
}
