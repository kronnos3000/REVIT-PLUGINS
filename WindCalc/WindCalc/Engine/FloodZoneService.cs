using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WindCalc.Engine
{
    /// <summary>
    /// Determines FEMA flood zone and Base Flood Elevation for a lat/lon.
    ///
    /// Query order:
    ///   1. FEMA NFHL ArcGIS REST service (live, no API key, nationwide)
    ///   2. Local NFHL shapefiles (if downloaded via Setup Data)
    /// </summary>
    public class FloodZoneService
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private readonly string _localDataFolder;

        // FEMA NFHL REST — Layer 28 = Flood Hazard Zones (S_Fld_Haz_Ar)
        private const string FemaRestUrl =
            "https://hazards.fema.gov/arcgis/rest/services/public/NFHL/MapServer/28/query" +
            "?geometry={0},{1}" +
            "&geometryType=esriGeometryPoint" +
            "&inSR=4326" +
            "&spatialRel=esriSpatialRelIntersects" +
            "&outFields=FLD_ZONE,ZONE_SUBTY,STATIC_BFE,DEPTH" +
            "&returnGeometry=false" +
            "&f=json";

        public FloodZoneService(string localDataFolder)
        {
            _localDataFolder = localDataFolder;
        }

        public async Task<(bool success, string floodZone, double bfeFt, string error)>
            GetFloodZoneAsync(double lat, double lon)
        {
            // 1. Try FEMA REST API
            try
            {
                return await QueryFemaRestAsync(lat, lon);
            }
            catch { /* fall through to local */ }

            // 2. Fall back to local shapefiles
            return await Task.Run(() => QueryLocal(lat, lon));
        }

        // ── FEMA ArcGIS REST ──────────────────────────────────────────────────

        private async Task<(bool, string, double, string)> QueryFemaRestAsync(double lat, double lon)
        {
            string url = string.Format(FemaRestUrl, lon, lat);
            string json = await _http.GetStringAsync(url);
            var root = JObject.Parse(json);

            // REST errors come back as JSON with an "error" key
            if (root["error"] != null)
                throw new Exception(root["error"]["message"]?.Value<string>());

            var features = root["features"] as JArray;
            if (features == null || features.Count == 0)
                return (true, "X", double.NaN, ""); // outside any mapped zone

            var attrs = features[0]["attributes"];
            string zone = attrs?["FLD_ZONE"]?.Value<string>()?.Trim() ?? "X";

            double bfe = double.NaN;
            double staticBfe = attrs?["STATIC_BFE"]?.Value<double>() ?? -9999;
            double depth     = attrs?["DEPTH"]?.Value<double>()      ?? -9999;

            if (staticBfe > -9000) bfe = staticBfe;
            else if (depth > -9000) bfe = depth;

            return (true, zone, bfe, "");
        }

        // ── Local shapefile fallback ──────────────────────────────────────────

        private (bool success, string floodZone, double bfeFt, string error) QueryLocal(double lat, double lon)
        {
            try
            {
                var candidates = new List<string>();

                string merged = Path.Combine(_localDataFolder, "flood_zones", "flood_zones_merged.shp");
                if (File.Exists(merged))
                {
                    candidates.Add(merged);
                }
                else
                {
                    string indexPath = Path.Combine(_localDataFolder, "flood_zones", "county_index.txt");
                    if (File.Exists(indexPath))
                        candidates.AddRange(File.ReadAllLines(indexPath));
                    else
                    {
                        string floodDir = Path.Combine(_localDataFolder, "flood_zones");
                        if (Directory.Exists(floodDir))
                            candidates.AddRange(
                                Directory.GetFiles(floodDir, "*.shp", SearchOption.AllDirectories));
                    }
                }

                if (candidates.Count == 0)
                    return (false, "", double.NaN,
                        "FEMA REST API unavailable and no local shapefiles found. " +
                        "Check your internet connection or run Setup Data.");

                foreach (string shpPath in candidates)
                {
                    if (!File.Exists(shpPath)) continue;
                    try
                    {
                        var record = ShapefileReader.FindContainingRecord(
                            shpPath, lat, lon, "FLD_ZONE", "BFE_VAL");

                        if (record != null)
                        {
                            string zone = record.TryGetValue("FLD_ZONE", out string z) ? z : "";
                            double bfe  = double.NaN;
                            if (record.TryGetValue("BFE_VAL", out string bfeStr) &&
                                double.TryParse(bfeStr, out double bfeVal) && bfeVal > -9000)
                                bfe = bfeVal;
                            return (true, zone, bfe, "");
                        }
                    }
                    catch { }
                }

                return (true, "X (outside mapped SFHA)", double.NaN, "");
            }
            catch (Exception ex)
            {
                return (false, "", double.NaN, $"Flood zone query error: {ex.Message}");
            }
        }
    }
}
