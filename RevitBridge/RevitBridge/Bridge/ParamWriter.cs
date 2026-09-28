using System;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitBridge.Services;

namespace RevitBridge.Bridge
{
    /// <summary>
    /// Unit-aware parameter writes. Resolves a parameter by name / BuiltInParameter / GUID and
    /// converts the JSON value to the parameter's storage type:
    ///   Double    → number = internal units (feet for lengths); string = parsed with units for
    ///               lengths, otherwise via SetValueString (display units, e.g. "45°", "12 psf").
    ///   Integer   → bool / int, or a display string via SetValueString.
    ///   ElementId → id, or a name of a type/level/material/phase.
    ///   String    → text.
    /// </summary>
    public static class ParamWriter
    {
        public static Parameter Find(Element e, JObject spec)
        {
            var name = (string)spec["name"];
            var builtin = (string)spec["builtin"];
            var guid = (string)spec["guid"];
            Parameter p = null;
            if (builtin != null)
            {
                if (!Enum.TryParse(builtin, true, out BuiltInParameter bip))
                    throw new BridgeException("BAD_PARAMS", $"'{builtin}' is not a BuiltInParameter.");
                p = e.get_Parameter(bip);
            }
            else if (guid != null)
            {
                if (!Guid.TryParse(guid, out var g)) throw new BridgeException("BAD_PARAMS", $"'{guid}' is not a GUID.");
                p = e.get_Parameter(g);
            }
            else if (name != null)
            {
                p = e.LookupParameter(name);
                if (p == null && Enum.TryParse(name, true, out BuiltInParameter bip2) && !int.TryParse(name, out _)) p = e.get_Parameter(bip2);
            }
            else throw new BridgeException("BAD_PARAMS", "Each parameter write needs name, builtin or guid.");
            return p;
        }

        /// <summary>Returns the new display value. Throws BridgeException on failure.</summary>
        public static JToken Set(Parameter p, JToken value)
        {
            var label = p.Definition?.Name;
            if (p.IsReadOnly) throw new BridgeException("READ_ONLY", $"Parameter '{label}' is read-only.");
            bool ok;
            switch (p.StorageType)
            {
                case StorageType.String:
                    ok = p.Set(value == null || value.Type == JTokenType.Null ? "" : value.ToString());
                    break;
                case StorageType.Integer:
                    if (value.Type == JTokenType.Boolean) ok = p.Set(value.Value<bool>() ? 1 : 0);
                    else if (value.Type == JTokenType.Integer) ok = p.Set(value.Value<int>());
                    else
                    {
                        var s = value.ToString();
                        if (bool.TryParse(s, out var b)) ok = p.Set(b ? 1 : 0);
                        else if (s.Equals("yes", StringComparison.OrdinalIgnoreCase)) ok = p.Set(1);
                        else if (s.Equals("no", StringComparison.OrdinalIgnoreCase)) ok = p.Set(0);
                        else if (int.TryParse(s, out var i)) ok = p.Set(i);
                        else ok = p.SetValueString(s);
                    }
                    break;
                case StorageType.Double:
                    if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float) ok = p.Set(value.Value<double>());
                    else
                    {
                        var s = value.ToString();
                        var spec = p.Definition.GetDataType();
                        if (spec == SpecTypeId.Length) ok = p.Set(UnitParser.ToFeet(s));
                        else ok = p.SetValueString(s);
                    }
                    break;
                case StorageType.ElementId:
                    ok = p.Set(ResolveIdValue(p.Element.Document, value));
                    break;
                default:
                    throw new BridgeException("UNSUPPORTED", $"Parameter '{label}' has no settable storage type.");
            }
            if (!ok) throw new BridgeException("REVIT_ERROR", $"Revit rejected value '{value}' for '{label}'.");
            return Describe.ParamValue(p);
        }

        private static ElementId ResolveIdValue(Document doc, JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return ElementId.InvalidElementId;
            if (value.Type == JTokenType.Integer) return new ElementId(value.Value<long>());
            var s = value.ToString();
            if (Lookup.TryId(s, out var id)) return id;
            var hit = new FilteredElementCollector(doc).WhereElementIsElementType().Cast<Element>()
                          .FirstOrDefault(e => string.Equals(e.Name, s, StringComparison.OrdinalIgnoreCase)
                                            || (e is ElementType et && string.Equals($"{et.FamilyName}: {et.Name}", s, StringComparison.OrdinalIgnoreCase)))
                      ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstOrDefault(e => string.Equals(e.Name, s, StringComparison.OrdinalIgnoreCase))
                      ?? new FilteredElementCollector(doc).OfClass(typeof(Material)).FirstOrDefault(e => string.Equals(e.Name, s, StringComparison.OrdinalIgnoreCase))
                      ?? doc.Phases.Cast<Phase>().FirstOrDefault(ph => string.Equals(ph.Name, s, StringComparison.OrdinalIgnoreCase));
            return hit?.Id ?? throw new BridgeException("NOT_FOUND", $"No type/level/material/phase named '{s}'.");
        }
    }
}
