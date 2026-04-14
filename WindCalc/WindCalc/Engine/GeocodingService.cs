using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WindCalc.Engine
{
    /// <summary>
    /// Converts a street address to latitude/longitude and county name
    /// using the US Census Bureau Geocoding API (free, no key required).
    ///
    /// Uses the geographies benchmark which returns the county name alongside
    /// the coordinate, at no extra cost.
    /// </summary>
    public class GeocodingService
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public async Task<(bool success, double lat, double lon,
                           string matched, string county, string error)>
            GeocodeAsync(string address)
        {
            try
            {
                string encoded = Uri.EscapeDataString(address);

                // Use geographies benchmark — returns county in addition to lat/lon
                string url =
                    $"https://geocoding.geo.census.gov/geocoder/geographies/onelineaddress" +
                    $"?address={encoded}" +
                    $"&benchmark=Public_AR_Current" +
                    $"&vintage=Current_Current" +
                    $"&format=json";

                string json = await _http.GetStringAsync(url);
                var root = JObject.Parse(json);

                var matches = root["result"]?["addressMatches"] as JArray;
                if (matches == null || matches.Count == 0)
                    return (false, 0, 0, "", "", "No match found for the entered address.");

                var first   = matches[0];
                double lon  = first["coordinates"]?["x"]?.Value<double>() ?? 0;
                double lat  = first["coordinates"]?["y"]?.Value<double>() ?? 0;
                string matched = first["matchedAddress"]?.Value<string>() ?? address;

                // Extract county name from geographies block
                string county = "";
                var counties = first["geographies"]?["Counties"] as JArray;
                if (counties != null && counties.Count > 0)
                    county = counties[0]["NAME"]?.Value<string>() ?? "";

                return (true, lat, lon, matched, county, "");
            }
            catch (Exception ex)
            {
                return (false, 0, 0, "", "", $"Geocoding error: {ex.Message}");
            }
        }
    }
}
