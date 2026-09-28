using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitBridge.Services;

namespace RevitBridge.Bridge
{
    /// <summary>Element → JSON helpers shared by all commands. Lengths in feet.</summary>
    public static class Describe
    {
        public static JObject Basic(Element e)
        {
            var doc = e.Document;
            var o = new JObject
            {
                ["id"] = e.Id.AsLong(),
                ["name"] = Json.SafeName(e),
                ["category"] = e.Category?.Name,
                ["class"] = e.GetType().Name,
            };
            var typeId = e.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                var t = doc.GetElement(typeId) as ElementType;
                o["type_id"] = typeId.AsLong();
                o["family"] = t?.FamilyName;
                o["type"] = t?.Name;
            }
            var lvlId = e.LevelId;
            if (lvlId != null && lvlId != ElementId.InvalidElementId)
                o["level"] = doc.GetElement(lvlId)?.Name;
            else if (e is FamilyInstance fi && fi.Host is Level hl)
                o["level"] = hl.Name;
            return o;
        }

        public static JObject Params(Element e, ICollection<string> only = null)
        {
            var o = new JObject();
            foreach (Parameter p in e.Parameters)
            {
                var name = p.Definition?.Name;
                if (name == null) continue;
                if (only != null && !only.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (o.ContainsKey(name)) continue;
                o[name] = ParamValue(p);
            }
            if (only != null)
            {
                // Built-in parameter names ("ALL_MODEL_MARK") are accepted too.
                foreach (var n in only.Where(n => !o.ContainsKey(n)))
                {
                    if (Enum.TryParse(n, true, out BuiltInParameter bip))
                    {
                        var p = e.get_Parameter(bip);
                        if (p != null) o[n] = ParamValue(p);
                    }
                }
            }
            return o;
        }

        public static JToken ParamValue(Parameter p)
        {
            if (!p.HasValue) return JValue.CreateNull();
            switch (p.StorageType)
            {
                case StorageType.String: return p.AsString();
                case StorageType.Integer:
                    if (p.Definition.GetDataType() == SpecTypeId.Boolean.YesNo) return p.AsInteger() != 0;
                    var vs = p.AsValueString();
                    return vs != null && vs != p.AsInteger().ToString()
                        ? new JObject { ["value"] = p.AsInteger(), ["display"] = vs }
                        : (JToken)p.AsInteger();
                case StorageType.Double:
                    return new JObject { ["value"] = Math.Round(p.AsDouble(), 9), ["display"] = p.AsValueString() };
                case StorageType.ElementId:
                    var id = p.AsElementId();
                    var el = id != ElementId.InvalidElementId ? p.Element.Document.GetElement(id) : null;
                    return new JObject { ["id"] = id.AsLong(), ["name"] = el != null ? Json.SafeName(el) : null };
                default: return JValue.CreateNull();
            }
        }

        public static JToken Location(Element e)
        {
            switch (e.Location)
            {
                case LocationPoint lp:
                    var o = new JObject { ["point"] = Xyz(lp.Point) };
                    try { o["rotation_deg"] = Math.Round(lp.Rotation * 180 / Math.PI, 6); } catch { }
                    return o;
                case LocationCurve lc:
                    return Curve(lc.Curve);
                default:
                    return JValue.CreateNull();
            }
        }

        public static JObject Curve(Curve c)
        {
            var o = new JObject { ["type"] = c.GetType().Name };
            if (c.IsBound)
            {
                o["start"] = Xyz(c.GetEndPoint(0));
                o["end"] = Xyz(c.GetEndPoint(1));
                o["length"] = Math.Round(c.Length, 6);
            }
            if (c is Arc arc)
            {
                o["center"] = Xyz(arc.Center);
                o["radius"] = Math.Round(arc.Radius, 6);
            }
            return o;
        }

        public static JObject BBox(Element e, View v = null)
        {
            var bb = e.get_BoundingBox(v);
            return bb == null ? null : new JObject { ["min"] = Xyz(bb.Min), ["max"] = Xyz(bb.Max) };
        }

        public static JObject SolidSummary(Element e)
        {
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            double vol = 0, area = 0;
            int solids = 0, faces = 0;
            void Walk(GeometryElement ge)
            {
                if (ge == null) return;
                foreach (var g in ge)
                {
                    if (g is Solid s && s.Volume > 0) { solids++; vol += s.Volume; area += s.SurfaceArea; faces += s.Faces.Size; }
                    else if (g is GeometryInstance gi) Walk(gi.GetInstanceGeometry());
                }
            }
            Walk(e.get_Geometry(opt));
            return new JObject
            {
                ["solids"] = solids, ["faces"] = faces,
                ["volume_ft3"] = Math.Round(vol, 6), ["surface_area_ft2"] = Math.Round(area, 6),
            };
        }

        public static JArray Xyz(XYZ p) => new JArray(Math.Round(p.X, 6), Math.Round(p.Y, 6), Math.Round(p.Z, 6));

        public static JArray Ids(IEnumerable<ElementId> ids) => new JArray(ids.Select(i => i.AsLong()));
    }
}
