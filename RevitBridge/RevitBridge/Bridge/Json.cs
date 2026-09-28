using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitBridge.Services;

namespace RevitBridge.Bridge
{
    /// <summary>
    /// Shared Newtonsoft settings. Converters make Revit types JSON-safe so both typed
    /// commands and execute_csharp can return them directly:
    ///   ElementId -> long, XYZ -> [x,y,z] feet, BoundingBoxXYZ -> {min,max},
    ///   Element -> {id, name, category}.
    /// </summary>
    public static class Json
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Include,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            MaxDepth = 32,
            Converters = new List<JsonConverter>
            {
                new ElementIdConverter(), new XyzConverter(), new BoundingBoxConverter(), new ElementConverter(),
            },
            Error = (s, e) => { e.ErrorContext.Handled = true; },
        };

        public static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

        public static string Serialize(object o) => JsonConvert.SerializeObject(o, Formatting.None, Settings);

        public static JToken ToToken(object o) => o == null ? JValue.CreateNull() : JToken.FromObject(o, Serializer);

        private sealed class ElementIdConverter : JsonConverter<ElementId>
        {
            public override void WriteJson(JsonWriter w, ElementId v, JsonSerializer s) => w.WriteValue(v.AsLong());
            public override ElementId ReadJson(JsonReader r, Type t, ElementId e, bool h, JsonSerializer s) =>
                new ElementId(Convert.ToInt64(r.Value));
        }

        private sealed class XyzConverter : JsonConverter<XYZ>
        {
            public override void WriteJson(JsonWriter w, XYZ v, JsonSerializer s)
            {
                w.WriteStartArray();
                w.WriteValue(Math.Round(v.X, 6)); w.WriteValue(Math.Round(v.Y, 6)); w.WriteValue(Math.Round(v.Z, 6));
                w.WriteEndArray();
            }
            public override XYZ ReadJson(JsonReader r, Type t, XYZ e, bool h, JsonSerializer s)
            {
                var a = JArray.Load(r);
                return new XYZ((double)a[0], (double)a[1], a.Count > 2 ? (double)a[2] : 0);
            }
        }

        private sealed class BoundingBoxConverter : JsonConverter<BoundingBoxXYZ>
        {
            public override bool CanRead => false;
            public override void WriteJson(JsonWriter w, BoundingBoxXYZ v, JsonSerializer s) =>
                JToken.FromObject(new { min = v.Min, max = v.Max }, s).WriteTo(w);
            public override BoundingBoxXYZ ReadJson(JsonReader r, Type t, BoundingBoxXYZ e, bool h, JsonSerializer s) =>
                throw new NotSupportedException();
        }

        private sealed class ElementConverter : JsonConverter<Element>
        {
            public override bool CanRead => false;
            public override void WriteJson(JsonWriter w, Element v, JsonSerializer s)
            {
                w.WriteStartObject();
                w.WritePropertyName("id"); w.WriteValue(v.Id.AsLong());
                w.WritePropertyName("name"); w.WriteValue(SafeName(v));
                w.WritePropertyName("category"); w.WriteValue(v.Category?.Name);
                w.WriteEndObject();
            }
            public override Element ReadJson(JsonReader r, Type t, Element e, bool h, JsonSerializer s) =>
                throw new NotSupportedException();
        }

        internal static string SafeName(Element e)
        {
            try { return e.Name; } catch { return null; }
        }
    }
}
