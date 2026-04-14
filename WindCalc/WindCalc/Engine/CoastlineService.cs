using System.IO;

namespace WindCalc.Engine
{
    /// <summary>
    /// Computes the straight-line distance in miles from a lat/lon site to the
    /// nearest segment of the NOAA mean high-water (MHW) shoreline polyline.
    ///
    /// Used to determine whether a site is "within 1 mile of the coast" per
    /// ASCE 7-22 §26.11.1, which lowers the windborne debris threshold from
    /// 140 mph to 130 mph.
    ///
    /// Local data: {LocalDataFolder}\coastline\*.shp  (downloaded by LocalDataDownloader)
    ///
    /// If the local shapefile is absent, returns double.MaxValue — the UI
    /// defaults to the manual CoastalProximity checkbox in that case.
    /// </summary>
    public class CoastlineService
    {
        private readonly string _localDataFolder;

        public CoastlineService(string localDataFolder)
        {
            _localDataFolder = localDataFolder;
        }

        /// <summary>Returns true if the local coastline shapefile is present.</summary>
        public bool IsAvailable() => GetShpPath() != null;

        /// <summary>
        /// Returns the distance in miles to the nearest MHW shoreline segment.
        /// Returns double.MaxValue if the local file is unavailable or an error occurs.
        /// </summary>
        public double GetDistanceToCoastMiles(double lat, double lon)
        {
            string shpPath = GetShpPath();
            if (shpPath == null) return double.MaxValue;

            try
            {
                return ShapefileReader.FindNearestPolylineDistanceMiles(shpPath, lat, lon,
                    searchRadiusMiles: 3.0);
            }
            catch
            {
                return double.MaxValue;
            }
        }

        private string GetShpPath()
        {
            string dir = Path.Combine(_localDataFolder, "coastline");
            if (!Directory.Exists(dir)) return null;
            string[] files = Directory.GetFiles(dir, "*.shp");
            return files.Length > 0 ? files[0] : null;
        }
    }
}
