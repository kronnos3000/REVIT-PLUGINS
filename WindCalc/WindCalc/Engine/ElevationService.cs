using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WindCalc.Engine
{
    /// <summary>
    /// Returns ground elevation (NAVD88, feet) for a lat/lon coordinate
    /// using the USGS Elevation Point Query Service (free, no API key).
    /// </summary>
    public class ElevationService
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public async Task<(bool success, double elevationFt, string error)>
            GetElevationAsync(double lat, double lon)
        {
            try
            {
                string url = $"https://epqs.nationalmap.gov/v1/json" +
                             $"?x={lon:F6}&y={lat:F6}&units=Feet&includeDate=false";

                string json = await _http.GetStringAsync(url);
                var root = JObject.Parse(json);

                double? elev = root["value"]?.Value<double>();
                if (elev == null)
                    return (false, 0, "USGS EPQS returned no elevation value.");

                return (true, elev.Value, "");
            }
            catch (Exception ex)
            {
                return (false, 0, $"Elevation lookup error: {ex.Message}");
            }
        }
    }
}
