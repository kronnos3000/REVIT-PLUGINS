using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using WindCalc.RevitIO;

namespace WindCalc.Services
{
    /// <summary>
    /// Checks windows and doors for wind design pressure compliance.
    ///
    /// PASS when: Rated+ >= Req'd+ AND |Rated-| >= |Req'd-|
    /// FAIL when: Either rated value is less than required
    /// </summary>
    public class WindChecker
    {
        private readonly Document _doc;

        public WindChecker(Document doc)
        {
            _doc = doc;
        }

        /// <summary>
        /// Result of a wind check for a single element.
        /// </summary>
        public class CheckResult
        {
            public ElementId ElementId { get; set; }
            public string Mark { get; set; }
            public double RatedPos { get; set; }
            public double RatedNeg { get; set; }
            public double ReqPos { get; set; }
            public double ReqNeg { get; set; }
            public int Zone { get; set; }
            public bool PassPos { get; set; }
            public bool PassNeg { get; set; }
            public bool Pass => PassPos && PassNeg;
            public string StatusText => Pass ? "PASS" : "FAIL";
            public string StatusSymbol => Pass ? "✓ PASS" : "△ FAIL"; // ✓ PASS or △ FAIL
        }

        /// <summary>
        /// Checks all windows and doors for wind compliance.
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
        /// Checks a single element for wind compliance.
        /// </summary>
        public CheckResult CheckElement(Element element)
        {
            if (element == null) return null;

            // Get type element for rated values
            var elementType = _doc.GetElement(element.GetTypeId());
            if (elementType == null) return null;

            var result = new CheckResult
            {
                ElementId = element.Id,
                Mark = GetMark(element)
            };

            // Get rated values from type
            result.RatedPos = GetDoubleParam(elementType, A2SharedParams.Names.DP_Rated_Pos);
            result.RatedNeg = GetDoubleParam(elementType, A2SharedParams.Names.DP_Rated_Neg);

            // Get required values from instance
            result.ReqPos = GetDoubleParam(element, A2SharedParams.Names.DP_Req_Pos);
            result.ReqNeg = GetDoubleParam(element, A2SharedParams.Names.DP_Req_Neg);

            // Get zone from instance
            result.Zone = GetIntParam(element, A2SharedParams.Names.Wind_Zone, 4);

            // Check: rated must be >= required (comparing magnitudes for negative)
            result.PassPos = result.RatedPos >= result.ReqPos;
            result.PassNeg = result.RatedNeg >= result.ReqNeg; // Both stored as positive magnitudes

            return result;
        }

        /// <summary>
        /// Writes wind check status to all elements.
        /// </summary>
        public int WriteStatusToModel(List<CheckResult> results)
        {
            int count = 0;

            foreach (var result in results)
            {
                var element = _doc.GetElement(result.ElementId);
                if (element == null) continue;

                var param = element.LookupParameter(A2SharedParams.Names.Wind_Status);
                if (A2SharedParamBinder.SetParameterValue(param, result.StatusSymbol))
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Checks a non-DP product (roofing, connectors) against its criterion.
        /// For example: shingle class vs Vult, connector capacity vs uplift demand.
        /// </summary>
        public bool CheckNonDPProduct(string ratingText, string requiredText)
        {
            // Parse rating and required criterion
            // Examples:
            // "CLASS H · 150 MPH" vs "150 MPH" -> PASS if rated >= required
            // "UPLIFT 1,240 LB" vs "980 LB" -> PASS if rated >= required

            // Simple numeric extraction for comparison
            var ratedValue = ExtractNumericValue(ratingText);
            var requiredValue = ExtractNumericValue(requiredText);

            if (ratedValue.HasValue && requiredValue.HasValue)
                return ratedValue.Value >= requiredValue.Value;

            // String comparison fallback
            return ratingText.Contains(requiredText) ||
                   ratingText.Equals(requiredText, StringComparison.OrdinalIgnoreCase);
        }

        private double? ExtractNumericValue(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            // Remove common separators and units, extract first number
            var cleanText = text.Replace(",", "").Replace("LB", "").Replace("MPH", "")
                               .Replace("PSF", "").Replace("SF", "").Trim();

            var parts = cleanText.Split(new[] { ' ', '·', '-' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (double.TryParse(part, out double value))
                    return value;
            }

            return null;
        }

        private string GetMark(Element element)
        {
            var markParam = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            return markParam?.AsString() ?? element.Id.ToString();
        }

        private double GetDoubleParam(Element element, string paramName, double defaultValue = 0)
        {
            var param = element.LookupParameter(paramName);
            if (param != null && param.StorageType == StorageType.Double)
                return param.AsDouble();
            return defaultValue;
        }

        private int GetIntParam(Element element, string paramName, int defaultValue = 0)
        {
            var param = element.LookupParameter(paramName);
            if (param != null && param.StorageType == StorageType.Integer)
                return param.AsInteger();
            return defaultValue;
        }
    }
}
