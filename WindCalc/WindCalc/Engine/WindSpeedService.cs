using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WindCalc.Engine
{
    /// <summary>
    /// Retrieves ASCE 7-22 design wind speeds (Vult and Vasd) for all Risk Categories.
    ///
    /// Lookup order:
    ///   1. Florida county lookup table (FLWindSpeedLookup) — built-in, no API, no download
    ///   2. ASCE Hazard Tool API (api-hazard.asce.org) — requires subscription key (optional)
    ///
    /// Vasd (RC II) is computed as: Vasd = Vult / sqrt(1.6)  (ASCE 7-22 §C26.4)
    /// </summary>
    public class WindSpeedService
    {
        private const string ApiBase =
            "https://api-hazard.asce.org/v1/wind" +
            "?lat={0:F6}&lon={1:F6}&standardsVersion=7-22&token={2}";

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        private readonly string         _apiKey;
        private readonly FLWindSpeedLookup _table;

        public WindSpeedService(string apiKey, string localDataFolder = null)
        {
            _apiKey = apiKey;
            _table  = new FLWindSpeedLookup();
        }

        public async Task<(bool success, double rcI, double rcII, double rcIII, double rcIV,
                           double vasdRcII, string error, string source)>
            GetWindSpeedsAsync(double lat, double lon, string county, bool coastal)
        {
            // ── 1. Florida county table (always available) ────────────────────
            var tableResult = _table.Lookup(county, coastal);
            if (tableResult.success)
            {
                double vasd = Math.Round(tableResult.rcII / Math.Sqrt(1.6), 0);
                return (true,
                    tableResult.rcI, tableResult.rcII,
                    tableResult.rcIII, tableResult.rcIV,
                    vasd, "",
                    $"ASCE 7-22 county table ({county} County, {(coastal ? "coastal" : "inland")})");
            }

            // ── 2. ASCE Hazard Tool API (optional, if key configured) ─────────
            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                try
                {
                    string url     = string.Format(ApiBase, lat, lon, _apiKey);
                    var request    = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    var response = await _http.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        var root    = JObject.Parse(json);
                        var wind    = root["wind"] ?? root["data"]?["wind"] ?? root;

                        double rcII  = GetMph(wind, "rcII",  "vult") ?? GetMph(wind, "riskCategoryII",  "vUlt") ?? 0;
                        double rcI   = GetMph(wind, "rcI",   "vult") ?? GetMph(wind, "riskCategoryI",   "vUlt") ?? 0;
                        double rcIII = GetMph(wind, "rcIII", "vult") ?? GetMph(wind, "riskCategoryIII", "vUlt") ?? 0;
                        double rcIV  = GetMph(wind, "rcIV",  "vult") ?? GetMph(wind, "riskCategoryIV",  "vUlt") ?? 0;
                        double vasd  = GetMph(wind, "rcII",  "vasd") ?? GetMph(wind, "riskCategoryII",  "vAsd") ?? 0;

                        if (rcII > 0)
                        {
                            if (vasd <= 0) vasd = Math.Round(rcII / Math.Sqrt(1.6), 0);
                            return (true, rcI, rcII, rcIII, rcIV, vasd, "", "ASCE Hazard Tool API");
                        }
                    }
                }
                catch { /* fall through */ }
            }

            // ── 3. Both failed — outside FL or county not matched ─────────────
            return (false, 0, 0, 0, 0, 0,
                string.IsNullOrWhiteSpace(county)
                    ? "County not identified — enter wind speeds manually."
                    : $"County '{county}' not in Florida table — enter wind speeds manually.",
                "");
        }

        private static double? GetMph(JToken node, string catKey, string speedKey)
        {
            try { var v = node?[catKey]?[speedKey]?.Value<double>(); return v > 0 ? v : null; }
            catch { return null; }
        }
    }
}
