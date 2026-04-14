using System;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using WindCalc.Models;

namespace WindCalc.Engine
{
    /// <summary>
    /// Orchestrates all data services to produce a complete WindCalcResult.
    /// Call FetchSiteDataAsync() when the user clicks "Fetch Data" in Tab 1.
    /// Call ReadRoofGeometry() to pre-fill Tab 2.
    /// </summary>
    public class WindCalcEngine
    {
        private readonly WindCalcConfig         _config;
        private readonly GeocodingService       _geocoder;
        private readonly ElevationService       _elevation;
        private readonly WindSpeedService       _windSpeed;
        private readonly FloodZoneService       _floodZone;
        private readonly ExposureCategoryEngine _exposure;
        private readonly ParcelDataService      _parcel;
        private readonly CoastlineService       _coastline;

        public WindCalcEngine(WindCalcConfig config)
        {
            _config    = config;
            _geocoder  = new GeocodingService();
            _elevation = new ElevationService();
            _windSpeed = new WindSpeedService(config.AsceApiKey, config.LocalDataFolder);
            _floodZone = new FloodZoneService(config.LocalDataFolder);
            _exposure  = new ExposureCategoryEngine(config.LocalDataFolder);
            _parcel    = new ParcelDataService(config.LocalDataFolder);
            _coastline = new CoastlineService(config.LocalDataFolder);
        }

        /// <summary>
        /// Fetches all site data for the given address.
        /// Each service is called independently so partial results are preserved
        /// even when one source fails.
        /// Reports progress via the optional callback (for UI status updates).
        /// </summary>
        public async Task<SiteData> FetchSiteDataAsync(
            string address,
            string riskCategory = "II",
            Action<string> progressCallback = null)
        {
            var site = new SiteData
            {
                InputAddress = address,
                RiskCategory = riskCategory
            };

            // Step 1: Geocode (returns county name from Census geographies)
            progressCallback?.Invoke("Geocoding address...");
            var (geoOk, lat, lon, matched, county, geoErr) = await _geocoder.GeocodeAsync(address);
            site.GeocodingSuccess = geoOk;
            site.GeocodingError   = geoErr;
            if (!geoOk) return site;

            site.Latitude       = lat;
            site.Longitude      = lon;
            site.MatchedAddress = matched;
            site.County         = county;

            // Coastal proximity first (local file — fast) so wind table uses correct zone
            progressCallback?.Invoke("Checking coastal proximity...");
            double distToCoast = await Task.Run(() => _coastline.GetDistanceToCoastMiles(lat, lon));
            if (distToCoast < double.MaxValue)
                site.CoastalProximity = distToCoast <= 1.0;

            // Steps 2-5 run in parallel (wind table uses county + coastal from above)
            progressCallback?.Invoke("Fetching wind speed, elevation, flood zone, and parcel data...");

            var windTask   = _windSpeed.GetWindSpeedsAsync(lat, lon, site.County, site.CoastalProximity);
            var elevTask   = _elevation.GetElevationAsync(lat, lon);
            var floodTask  = _floodZone.GetFloodZoneAsync(lat, lon);
            var parcelTask = _parcel.EnrichWithParcelDataAsync(site);

            await Task.WhenAll(windTask, elevTask, floodTask, parcelTask);

            // Wind speed
            var (windOk, rcI, rcII, rcIII, rcIV, vasd, windErr, windSrc) = windTask.Result;
            site.WindSpeedSource     = windSrc;
            site.WindSpeedSuccess    = windOk;
            site.AsceApiKey_Missing  = windErr == "ASCE_KEY_MISSING";
            site.WindSpeedError      = site.AsceApiKey_Missing
                ? "ASCE API key not configured. Enter your key in Settings."
                : windErr;
            if (windOk)
            {
                site.AsceVultRcI   = rcI;
                site.AsceVultRcII  = rcII;
                site.AsceVultRcIII = rcIII;
                site.AsceVultRcIV  = rcIV;
                site.AsceVasdRcII  = vasd;

                // Apply firm minimum — 150 mph is never reduced below
                double firmMin = _config.FirmMinimumVult;
                site.AppliedVult            = Math.Max(rcII, firmMin);
                site.FirmMinOverrideActive  = site.AppliedVult > rcII;
            }

            // Elevation
            var (elevOk, elevFt, elevErr) = elevTask.Result;
            site.ElevationSuccess = elevOk;
            site.ElevationError   = elevErr;
            if (elevOk) site.ElevationFt = elevFt;

            // Flood zone
            var (floodOk, zone, bfe, floodErr) = floodTask.Result;
            site.FloodZoneSuccess = floodOk;
            site.FloodZoneError   = floodErr;
            if (floodOk)
            {
                site.FloodZone  = zone;
                site.FloodBfeFt = bfe;
            }

            // Parcel enrichment (updates site in place)
            site = parcelTask.Result.updated;

            // Exposure category (needs elevation for heuristic)
            progressCallback?.Invoke("Determining exposure category...");
            var (expCat, expAuto, expNote) = await _exposure.GetExposureCategoryAsync(lat, lon, site.ElevationFt);
            site.ExposureCategory     = expCat;
            site.ExposureAutoDetected = expAuto;

            // Derived flags — recomputed again at Apply time with final user inputs
            RecomputeDerivedFlags(site);

            progressCallback?.Invoke("Done.");
            return site;
        }

        /// <summary>
        /// Reads roof geometry from the Revit document.
        /// Call this before showing the dialog to pre-fill Tab 2.
        /// </summary>
        public BuildingData ReadRoofGeometry(Document doc)
        {
            return new RoofGeometryReader(doc).ReadRoofGeometry();
        }

        /// <summary>
        /// Recomputes WindborneDebrisArea and Envelope160MphRequired from current SiteData values.
        /// Call after fetch and again after the user clicks Apply with final overrides.
        /// CoastalProximity must be set before calling.
        /// </summary>
        public static void RecomputeDerivedFlags(SiteData site)
        {
            double vult = site.AppliedVult;
            // ASCE 7-22 §26.11.1 / FBC 9th Ed:
            //   General windborne debris region: V ≥ 140 mph
            //   Within 1 mi of mean high-water line: V ≥ 130 mph
            site.WindborneDebrisArea = vult >= 140.0
                || (site.CoastalProximity && vult >= 130.0);

            // SB 1218 / HB 911 (FBC 9th Ed): entire envelope impact-resistant for V ≥ 130 mph
            site.Envelope160MphRequired = vult >= 130.0;
        }
    }
}
