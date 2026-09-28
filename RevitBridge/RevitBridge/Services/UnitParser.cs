using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RevitBridge.Services
{
    /// <summary>
    /// Converts user-facing lengths/angles to Revit internal units (feet / radians).
    /// Accepts a bare number (already feet) or a string with units:
    ///   12'-6"   12' 6"   12'   6"   6 1/2"   150 in   150mm   3.2 m   45 cm   10 ft
    /// Angles: a bare number is degrees for the angle helpers, or "1.2 rad" / "45 deg".
    /// </summary>
    public static class UnitParser
    {
        private static readonly Regex FeetInches = new Regex(
            @"^\s*(?<neg>-)?\s*(?:(?<ft>\d+(?:\.\d+)?)\s*'\s*-?\s*)?(?:(?<in>\d+(?:\.\d+)?)?(?:\s*(?<num>\d+)\s*/\s*(?<den>\d+))?\s*"")?\s*$",
            RegexOptions.Compiled);

        private static readonly Regex NumberUnit = new Regex(
            @"^\s*(?<v>[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?)\s*(?<u>[a-zA-Z""']*)\s*$",
            RegexOptions.Compiled);

        public static double ToFeet(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                throw new BridgeException("BAD_PARAMS", "Missing length value.");
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                return token.Value<double>();
            return ToFeet(token.Value<string>());
        }

        public static double ToFeet(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new BridgeException("BAD_PARAMS", "Empty length value.");
            var s = text.Trim().Replace("′", "'").Replace("″", "\"").Replace("''", "\"");

            if (s.Contains("'") || s.EndsWith("\""))
            {
                var m = FeetInches.Match(s);
                if (m.Success && (m.Groups["ft"].Success || m.Groups["in"].Success || m.Groups["num"].Success))
                {
                    double ft = m.Groups["ft"].Success ? Parse(m.Groups["ft"].Value) : 0;
                    double inch = m.Groups["in"].Success ? Parse(m.Groups["in"].Value) : 0;
                    if (m.Groups["num"].Success)
                        inch += Parse(m.Groups["num"].Value) / Parse(m.Groups["den"].Value);
                    var v = ft + inch / 12.0;
                    return m.Groups["neg"].Success ? -v : v;
                }
            }

            var nu = NumberUnit.Match(s);
            if (!nu.Success)
                throw new BridgeException("BAD_PARAMS", $"Cannot parse length '{text}'. Use feet (number) or e.g. 12'-6\", 150 in, 3.2 m, 450 mm.");
            double val = Parse(nu.Groups["v"].Value);
            switch (nu.Groups["u"].Value.ToLowerInvariant())
            {
                case "": case "ft": case "feet": case "foot": case "'": return val;
                case "in": case "inch": case "inches": case "\"": return val / 12.0;
                case "mm": return val / 304.8;
                case "cm": return val / 30.48;
                case "m": return val / 0.3048;
                case "yd": return val * 3.0;
                default:
                    throw new BridgeException("BAD_PARAMS", $"Unknown length unit '{nu.Groups["u"].Value}' in '{text}'.");
            }
        }

        /// <summary>Converts to feet given an explicit unit name (used by ScriptContext.ToFeet).</summary>
        public static double ToFeet(double value, string unit) => ToFeet(value.ToString(CultureInfo.InvariantCulture) + " " + (unit ?? "ft"));

        /// <summary>Angle in radians. Bare numbers are degrees.</summary>
        public static double ToRadians(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return 0;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                return token.Value<double>() * Math.PI / 180.0;
            var nu = NumberUnit.Match(token.Value<string>() ?? "");
            if (!nu.Success) throw new BridgeException("BAD_PARAMS", $"Cannot parse angle '{token}'.");
            double v = Parse(nu.Groups["v"].Value);
            var u = nu.Groups["u"].Value.ToLowerInvariant();
            return u == "rad" || u == "radians" ? v : v * Math.PI / 180.0;
        }

        public static string FormatLength(Document doc, double feet)
        {
            try { return UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, feet, false); }
            catch { return feet.ToString("0.####", CultureInfo.InvariantCulture) + " ft"; }
        }

        private static double Parse(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
