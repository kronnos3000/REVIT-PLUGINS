using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WindCalc.Models;

namespace WindCalc.Engine
{
    /// <summary>
    /// Retrieves parcel data (parcel ID, year built, construction class, owner,
    /// legal description, mailing address) for a lat/lon + input address.
    ///
    /// Lookup order for Pinellas County:
    ///   1. Local PCPAO CSV bulk export  (all fields incl. owner / legal / mailing)
    ///   2. Online Pinellas EGIS         (live fallback — no owner/legal/mailing)
    ///
    /// Lookup order for other FL counties:
    ///   3. County PA REST services      (Hillsborough, Sarasota, Pasco — CONSTCLASS)
    ///   4. Florida FGIO statewide API   (all 67 counties, standardized fields)
    ///   5. SWFWMD legacy               (4-county coverage, last resort)
    ///
    /// Charlotte County:
    ///   Local shapefile if present, then FGIO/SWFWMD online.
    ///
    /// PCPAO CSV session cache: indexes are built once per Revit session and
    /// reused for every subsequent lookup (fast after first call).
    /// </summary>
    public class ParcelDataService
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // ── County bounding boxes ─────────────────────────────────────────────

        private const double PinellasMinLat = 27.63, PinellasMaxLat = 28.18;
        private const double PinellasMinLon = -82.87, PinellasMaxLon = -82.45;

        private const double CharlotteMinLat = 26.73, CharlotteMaxLat = 27.04;
        private const double CharlotteMinLon = -82.35, CharlotteMaxLon = -81.66;

        // 5-county expanded bbox for FGIO / SWFWMD fallback
        private const double RegionMinLat = 26.73, RegionMaxLat = 28.46;
        private const double RegionMinLon = -82.87, RegionMaxLon = -81.66;

        // ── Florida statewide parcel APIs ────────────────────────────────────
        private const string FgioUrl =
            "https://services1.arcgis.com/O1JpcwDW8sjYuddV/arcgis/rest/services/" +
            "Florida_Parcels/FeatureServer/0/query" +
            "?geometry={0:F6},{1:F6}&geometryType=esriGeometryPoint&inSR=4326" +
            "&spatialRel=esriSpatialRelIntersects" +
            "&outFields=PARCELID,YRBLT,CONSTCLASS,DORUC,SITUSADDR,SITUSCITY" +
            "&returnGeometry=false&f=json";

        private const string FgioFallbackUrl =
            "https://geodata.floridagio.gov/arcgis/rest/services/FL_Parcels/Parcels/MapServer/0/query" +
            "?geometry={0:F6},{1:F6}&geometryType=esriGeometryPoint&inSR=4326" +
            "&spatialRel=esriSpatialRelIntersects" +
            "&outFields=PARCELID,YRBLT,CONSTCLASS,DORUC,SITEADDR,SITECITY" +
            "&returnGeometry=false&f=json";

        // ── County PA REST services ───────────────────────────────────────────
        private static readonly (string Name, string Url,
            string ParcelField, string AddrField, string CityField, string YrField, string ConstField,
            double MinLat, double MaxLat, double MinLon, double MaxLon)[] CountyPaServices =
        {
            (
                "Hillsborough",
                "https://gis2.hcpafl.org/arcgis/rest/services/Public/Parcels/MapServer/0/query" +
                "?geometry={lon:F6},{lat:F6}&geometryType=esriGeometryPoint&inSR=4326" +
                "&spatialRel=esriSpatialRelIntersects" +
                "&outFields=FOLIO_NUM,SITESTREET,SITECITY,YEAR_BUILT,CONST_CLASS" +
                "&returnGeometry=false&f=json",
                "FOLIO_NUM", "SITESTREET", "SITECITY", "YEAR_BUILT", "CONST_CLASS",
                27.64, 28.17, -82.72, -82.10
            ),
            (
                "Sarasota",
                "https://gis.sc-pa.com/arcgis/rest/services/Parcels/ParcelData/MapServer/0/query" +
                "?geometry={lon:F6},{lat:F6}&geometryType=esriGeometryPoint&inSR=4326" +
                "&spatialRel=esriSpatialRelIntersects" +
                "&outFields=PARCEL_ID,SITE_ADDR,SITE_CITY,YR_BLT,CONST_CLASS" +
                "&returnGeometry=false&f=json",
                "PARCEL_ID", "SITE_ADDR", "SITE_CITY", "YR_BLT", "CONST_CLASS",
                26.86, 27.53, -82.66, -82.04
            ),
            (
                "Pasco",
                "https://www.pascopa.com/arcgis/rest/services/Parcels/MapServer/0/query" +
                "?geometry={lon:F6},{lat:F6}&geometryType=esriGeometryPoint&inSR=4326" +
                "&spatialRel=esriSpatialRelIntersects" +
                "&outFields=PARCELID,SITEADDRESS,SITECITY,YEARBUILT,CONSTCLASS" +
                "&returnGeometry=false&f=json",
                "PARCELID", "SITEADDRESS", "SITECITY", "YEARBUILT", "CONSTCLASS",
                27.87, 28.46, -82.78, -82.01
            ),
        };

        // ── SWFWMD legacy ─────────────────────────────────────────────────────
        private const string SwfwmdBase =
            "https://www25.swfwmd.state.fl.us/arcgis12/rest/services/BaseVector/parcel_search/MapServer/{0}/query" +
            "?geometry={1:F6},{2:F6}&geometryType=esriGeometryPoint&inSR=4326" +
            "&spatialRel=esriSpatialRelIntersects" +
            "&outFields=PARNO,SITEADD,SCITY,YRBLT_ACT,YRBLT_EFF,PARUSEDESC,DOR4CODE" +
            "&returnGeometry=false&f=json";

        private static readonly (string Name, int LayerId, double MinLat, double MaxLat, double MinLon, double MaxLon)[]
            SwfwmdCounties =
        {
            ( "Hillsborough", 7,  27.64, 28.17, -82.72, -82.10 ),
            ( "Pasco",        12, 27.87, 28.46, -82.78, -82.01 ),
            ( "Sarasota",     15, 26.86, 27.53, -82.66, -82.04 ),
        };

        private readonly string _localDataFolder;

        public ParcelDataService(string localDataFolder = null)
        {
            _localDataFolder = localDataFolder ?? "";
        }

        // ── Main entry ────────────────────────────────────────────────────────

        public async Task<(bool success, SiteData updated)> EnrichWithParcelDataAsync(SiteData site)
        {
            double lat = site.Latitude;
            double lon = site.Longitude;

            // 1. Pinellas: local PCPAO CSV bulk export (superset — includes owner/legal/mailing)
            if (IsInBbox(lat, lon, PinellasMinLat, PinellasMaxLat, PinellasMinLon, PinellasMaxLon))
            {
                bool done = TryLocalPcpaoCsv(site);
                if (done) return (true, site);
            }

            // 2. Charlotte: local shapefile (no CSV equivalent available)
            if (IsInBbox(lat, lon, CharlotteMinLat, CharlotteMaxLat, CharlotteMinLon, CharlotteMaxLon))
            {
                bool done = TryLocalParcel(site, "Charlotte",
                    "PARCELID", "SITEADDR", "SITECITY", "YRBLT", "CONSTCLASS", null);
                if (done) return (true, site);
            }

            // 3. Online Pinellas EGIS fallback (when CSV not present)
            if (IsInBbox(lat, lon, PinellasMinLat, PinellasMaxLat, PinellasMinLon, PinellasMaxLon))
            {
                var (ok, updated) = await QueryPinellasAsync(site);
                if (ok) return (true, updated);
            }

            // 4. County PA REST services (Hillsborough, Sarasota, Pasco — have CONSTCLASS)
            foreach (var pa in CountyPaServices)
            {
                if (IsInBbox(lat, lon, pa.MinLat, pa.MaxLat, pa.MinLon, pa.MaxLon))
                {
                    var (ok, updated) = await QueryCountyPaAsync(site, pa);
                    if (ok) return (true, updated);
                    break;
                }
            }

            // 5. FGIO statewide (covers all FL counties, standardized fields)
            if (IsInBbox(lat, lon, RegionMinLat, RegionMaxLat, RegionMinLon, RegionMaxLon))
            {
                var (ok, updated) = await QueryFgioAsync(site);
                if (ok) return (true, updated);
            }

            // 6. SWFWMD legacy (last resort — no CONSTCLASS)
            foreach (var county in SwfwmdCounties)
            {
                if (IsInBbox(lat, lon, county.MinLat, county.MaxLat, county.MinLon, county.MaxLon))
                {
                    var (ok, updated) = await QuerySwfwmdAsync(site, county.LayerId);
                    if (ok) return (true, updated);
                    break;
                }
            }

            site.ParcelSuccess = false;
            return (false, site);
        }

        // ══════════════════════════════════════════════════════════════════════
        // PCPAO CSV LOCAL LOOKUP
        // ══════════════════════════════════════════════════════════════════════

        // Session-level cache — built once per Revit session, shared across all lookups.
        // Call InvalidateCsvCache() after a fresh download so the next lookup rebuilds.
        private static volatile PcpaoCsvIndex _csvIndex;
        private static readonly object _csvLock = new object();

        /// <summary>
        /// Clears the in-memory CSV index so the next parcel lookup rebuilds it
        /// from the freshly downloaded files.  Called by LocalDataDownloader after
        /// a successful PCPAO CSV download.
        /// </summary>
        public static void InvalidateCsvCache()
        {
            lock (_csvLock) { _csvIndex = null; }
        }

        private bool TryLocalPcpaoCsv(SiteData site)
        {
            if (string.IsNullOrWhiteSpace(_localDataFolder)) return false;

            string csvDir = Path.Combine(_localDataFolder, "parcels", "Pinellas", "csv");
            if (!Directory.Exists(csvDir)) return false;

            // Required files must all exist
            string pathAddr = Path.Combine(csvDir, "RP_ALL_SITE_ADDRESSES.csv");
            string pathOwn  = Path.Combine(csvDir, "RP_ALL_OWNERS.csv");
            string pathBld  = Path.Combine(csvDir, "RP_BUILDING.csv");
            string pathProp = Path.Combine(csvDir, "RP_PROPERTY_INFO.csv");
            string pathLeg  = Path.Combine(csvDir, "RP_LEGAL.csv");

            if (!File.Exists(pathAddr) || !File.Exists(pathOwn) || !File.Exists(pathBld))
                return false;

            // Build or reuse session cache
            var index = GetOrBuildIndex(csvDir, pathAddr, pathOwn, pathBld, pathProp, pathLeg);
            if (index == null) return false;

            // Normalize the input address and look up
            string normInput = NormAddress(site.InputAddress);
            string houseNum  = GetHouseNumber(normInput);

            string strap = MatchStrap(index, normInput, houseNum);
            if (strap == null) return false;

            // Populate SiteData from index
            site.ParcelId   = index.StrapToParcel.TryGetValue(strap, out string pid)  ? pid  : "";
            site.YearBuilt  = index.StrapToYear.TryGetValue(strap,   out int  yr)     ? yr   : 0;
            site.ConstructionClass = ""; // PCPAO CSVs don't carry CONSTCLASS — will remain blank

            site.ParcelOwner         = index.StrapToOwner.TryGetValue(strap,   out string own)  ? own  : "";
            site.OwnerMailingAddress = index.StrapToMail.TryGetValue(strap,    out string mail) ? mail : "";
            site.LegalDescription    = index.StrapToLegal.TryGetValue(strap,   out string leg)  ? leg  : "";

            if (!string.IsNullOrWhiteSpace(site.ParcelId))
            {
                site.ParcelSuccess = true;
                site.ParcelSource  = "Local PCPAO CSV bulk export";
                return true;
            }
            return false;
        }

        private PcpaoCsvIndex GetOrBuildIndex(
            string csvDir,
            string pathAddr, string pathOwn, string pathBld,
            string pathProp, string pathLeg)
        {
            // Double-checked locking — only build once per session.
            if (_csvIndex != null) return _csvIndex;
            lock (_csvLock)
            {
                if (_csvIndex != null) return _csvIndex;
                try
                {
                    _csvIndex = BuildPcpaoCsvIndex(csvDir, pathAddr, pathOwn, pathBld, pathProp, pathLeg);
                }
                catch
                {
                    // Leave _csvIndex null so next call retries (rare — only on corrupted files)
                    return null;
                }
            }
            return _csvIndex;
        }

        private static PcpaoCsvIndex BuildPcpaoCsvIndex(
            string csvDir,
            string pathAddr, string pathOwn, string pathBld,
            string pathProp, string pathLeg)
        {
            var idx = new PcpaoCsvIndex();

            // ── Site addresses: addr_norm → [STRAP...], house# → [addr_norms...] ──
            foreach (var row in ReadCsv(pathAddr))
            {
                string addr  = NormAddress(GetCol(row, "SITE_ADDR"));
                string strap = GetCol(row, "STRAP");
                if (string.IsNullOrEmpty(addr) || string.IsNullOrEmpty(strap)) continue;

                if (!idx.AddrToStraps.TryGetValue(addr, out var list))
                    idx.AddrToStraps[addr] = list = new List<string>();
                if (!list.Contains(strap)) list.Add(strap);

                string hn = GetHouseNumber(addr);
                if (!string.IsNullOrEmpty(hn))
                {
                    if (!idx.HouseToAddrs.TryGetValue(hn, out var bucket))
                        idx.HouseToAddrs[hn] = bucket = new List<string>();
                    if (!bucket.Contains(addr)) bucket.Add(addr);
                }
            }

            // ── Owners: STRAP → owner name + parcel number ─────────────────────
            foreach (var row in ReadCsv(pathOwn))
            {
                string strap  = GetCol(row, "STRAP");
                string owner  = GetCol(row, "OWNER_NAME");
                string parcel = GetCol(row, "PARCEL_NUMBER");
                if (string.IsNullOrEmpty(strap)) continue;
                if (!string.IsNullOrEmpty(owner)  && !idx.StrapToOwner.ContainsKey(strap))
                    idx.StrapToOwner[strap] = owner;
                if (!string.IsNullOrEmpty(parcel) && !idx.StrapToParcel.ContainsKey(strap))
                    idx.StrapToParcel[strap] = parcel;
            }

            // ── Building: STRAP → earliest year built ──────────────────────────
            foreach (var row in ReadCsv(pathBld))
            {
                string strap = GetCol(row, "STRAP");
                string yearS = GetCol(row, "YEAR_BUILT");
                if (string.IsNullOrEmpty(strap) || string.IsNullOrEmpty(yearS)) continue;
                if (!int.TryParse(yearS, out int y) || y <= 0) continue;
                if (!idx.StrapToYear.TryGetValue(strap, out int prev) || y < prev)
                    idx.StrapToYear[strap] = y;
            }

            // ── Property info: STRAP → mailing address + fallback year ──────────
            if (File.Exists(pathProp))
            {
                foreach (var row in ReadCsv(pathProp))
                {
                    string strap = GetCol(row, "STRAP");
                    if (string.IsNullOrEmpty(strap)) continue;

                    string m1 = GetCol(row, "MAILING_ADDRESS_1");
                    string m2 = GetCol(row, "MAILING_ADDRESS_2");
                    if (!string.IsNullOrWhiteSpace(m1) && !idx.StrapToMail.ContainsKey(strap))
                    {
                        string combined = (m1 + "\n" + m2).Trim();
                        if (string.IsNullOrWhiteSpace(m2)) combined = m1;
                        idx.StrapToMail[strap] = combined;
                    }

                    if (!idx.StrapToYear.ContainsKey(strap))
                    {
                        string yb = GetCol(row, "YEAR_BUILT");
                        if (int.TryParse(yb, out int y) && y > 0)
                            idx.StrapToYear[strap] = y;
                    }
                }
            }

            // ── Legal: STRAP/PARCEL → legal description ─────────────────────────
            if (File.Exists(pathLeg))
            {
                foreach (var row in ReadCsv(pathLeg))
                {
                    string strap  = GetCol(row, "STRAP");
                    string parcel = GetCol(row, "PARCEL_NUMBER");
                    string v1     = GetCol(row, "LONG_LEGAL_1");
                    string v2     = GetCol(row, "LONG_LEGAL_2");
                    string legal  = (v1 + " " + v2).Trim();
                    if (string.IsNullOrWhiteSpace(legal)) continue;

                    if (!string.IsNullOrEmpty(strap) && !idx.StrapToLegal.ContainsKey(strap))
                        idx.StrapToLegal[strap] = legal;
                    else if (!string.IsNullOrEmpty(parcel) && !idx.StrapToLegal.ContainsKey(parcel))
                        idx.StrapToLegal[parcel] = legal;
                }
            }

            return idx;
        }

        private static string MatchStrap(PcpaoCsvIndex idx, string normInput, string houseNum)
        {
            // 1. Exact match
            if (idx.AddrToStraps.TryGetValue(normInput, out var straps) && straps.Count > 0)
                return straps[0]; // take first; multi-unit ambiguity resolved by caller if needed

            // 2. House-number-bucket partial match (handles unit/apt number differences)
            if (!string.IsNullOrEmpty(houseNum) && idx.HouseToAddrs.TryGetValue(houseNum, out var bucket))
            {
                foreach (string candidate in bucket)
                {
                    if (normInput.Contains(candidate) || candidate.Contains(normInput))
                    {
                        if (idx.AddrToStraps.TryGetValue(candidate, out var sl) && sl.Count > 0)
                            return sl[0];
                    }
                }
            }
            return null;
        }

        // ── PCPAO CSV index container ─────────────────────────────────────────

        private class PcpaoCsvIndex
        {
            public Dictionary<string, List<string>> AddrToStraps   = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            public Dictionary<string, List<string>> HouseToAddrs   = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            public Dictionary<string, string>       StrapToOwner   = new Dictionary<string, string>(StringComparer.Ordinal);
            public Dictionary<string, string>       StrapToParcel  = new Dictionary<string, string>(StringComparer.Ordinal);
            public Dictionary<string, int>          StrapToYear    = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<string, string>       StrapToMail    = new Dictionary<string, string>(StringComparer.Ordinal);
            public Dictionary<string, string>       StrapToLegal   = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        // ── Address normalization ─────────────────────────────────────────────

        private static readonly Dictionary<string, string> _suffixMap = new Dictionary<string, string>
        {
            { " STREET", " ST" },  { " ROAD", " RD" },     { " AVENUE", " AVE" },
            { " DRIVE", " DR" },   { " BOULEVARD", " BLVD"},{ " LANE", " LN" },
            { " COURT", " CT" },   { " CIRCLE", " CIR" },   { " PLACE", " PL" },
            { " TERRACE", " TER" },{ " HIGHWAY", " HWY" },  { " PARKWAY", " PKWY" },
            { " NORTH ", " N " },  { " SOUTH ", " S " },    { " EAST ", " E " },
            { " WEST ", " W " },
            { " APARTMENT ", " # " }, { " APT ", " # " }, { " UNIT ", " # " },
            { " STE ", " # " },       { " SUITE ", " # " },
        };

        internal static string NormAddress(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.ToUpperInvariant().Trim();
            s = Regex.Replace(s, @"[,\.\-]", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            // Apply longest-match-first suffix replacements
            var keys = new List<string>(_suffixMap.Keys);
            keys.Sort((a, b) => b.Length.CompareTo(a.Length));
            foreach (string k in keys)
                s = s.Replace(k, _suffixMap[k]);
            // Strip city / state / zip tail (e.g. ", CLEARWATER, FL 33756")
            int comma = s.IndexOf(',');
            if (comma > 0) s = s.Substring(0, comma).Trim();
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        internal static string GetHouseNumber(string normAddr)
        {
            if (string.IsNullOrEmpty(normAddr)) return "";
            var m = Regex.Match(normAddr, @"^\s*(\d+)\b");
            return m.Success ? m.Groups[1].Value : "";
        }

        // ── CSV reader (UTF-8 BOM tolerant, Windows line endings) ─────────────

        private static IEnumerable<Dictionary<string, string>> ReadCsv(string path)
        {
            // Read all bytes; detect BOM manually to strip it
            byte[] raw = File.ReadAllBytes(path);
            bool hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            string text = hasBom
                ? Encoding.UTF8.GetString(raw, 3, raw.Length - 3)
                : Encoding.UTF8.GetString(raw);

            using var reader = new StringReader(text);
            string headerLine = reader.ReadLine();
            if (headerLine == null) yield break;

            string[] headers = ParseCsvLine(headerLine);

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] values = ParseCsvLine(line);
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < headers.Length; i++)
                    row[headers[i].Trim()] = i < values.Length ? (values[i] ?? "").Trim() : "";
                yield return row;
            }
        }

        private static string[] ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var sb     = new StringBuilder();
            bool inQ   = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQ)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    { sb.Append('"'); i++; }
                    else if (c == '"') inQ = false;
                    else sb.Append(c);
                }
                else
                {
                    if (c == '"') { inQ = true; }
                    else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(c);
                }
            }
            fields.Add(sb.ToString());
            return fields.ToArray();
        }

        private static string GetCol(Dictionary<string, string> row, string key)
        {
            return row.TryGetValue(key, out string v) ? (v ?? "").Trim() : "";
        }

        // ══════════════════════════════════════════════════════════════════════
        // LOCAL SHAPEFILE LOOKUP (Charlotte)
        // ══════════════════════════════════════════════════════════════════════

        private bool TryLocalParcel(
            SiteData site, string countyFolder,
            string parcelField, string addrField, string cityField,
            string yearField, string constField, string dorField)
        {
            if (string.IsNullOrWhiteSpace(_localDataFolder)) return false;

            string dir = Path.Combine(_localDataFolder, "parcels", countyFolder);
            if (!Directory.Exists(dir)) return false;

            string[] shpFiles = Directory.GetFiles(dir, "*.shp");
            if (shpFiles.Length == 0) return false;

            try
            {
                var fields = new List<string> { parcelField, addrField, cityField, yearField, constField };
                if (dorField != null) fields.Add(dorField);

                var rec = ShapefileReader.FindContainingRecord(
                    shpFiles[0], site.Latitude, site.Longitude, fields.ToArray());
                if (rec == null) return false;

                site.ParcelId = GetOrEmpty(rec, parcelField);

                if (rec.TryGetValue(yearField, out string yr) &&
                    int.TryParse(yr, out int yrVal) && yrVal > 0)
                    site.YearBuilt = yrVal;

                site.ConstructionClass = GetOrEmpty(rec, constField);

                string addr = GetOrEmpty(rec, addrField);
                string city = GetOrEmpty(rec, cityField);
                if (!string.IsNullOrWhiteSpace(addr) && string.IsNullOrWhiteSpace(site.MatchedAddress))
                    site.MatchedAddress = $"{addr}, {city}, FL";

                site.ParcelSuccess = true;
                site.ParcelSource  = $"Local {countyFolder} shapefile";
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // ONLINE LOOKUPS (unchanged)
        // ══════════════════════════════════════════════════════════════════════

        private async Task<(bool, SiteData)> QueryPinellasAsync(SiteData site)
        {
            try
            {
                string url =
                    "https://egis.pinellas.gov/gis/rest/services/PublicWebGIS/Parcels/MapServer/1/query" +
                    $"?geometry={site.Longitude:F6},{site.Latitude:F6}" +
                    "&geometryType=esriGeometryPoint&spatialRel=esriSpatialRelIntersects" +
                    "&outFields=PARCELNO,PHYADDR1,PHYCITY,ACTYRBLT,NUMSTORIES,CONSTCLASS" +
                    "&returnGeometry=false&f=json";

                string json = await _http.GetStringAsync(url);
                var attrs = FirstAttributes(json);
                if (attrs == null) return (false, site);

                site.ParcelId          = attrs["PARCELNO"]?.Value<string>()  ?? "";
                site.YearBuilt         = attrs["ACTYRBLT"]?.Value<int>()     ?? 0;
                site.ConstructionClass = attrs["CONSTCLASS"]?.Value<string>() ?? "";
                site.ParcelSuccess     = true;
                site.ParcelSource      = "Pinellas EGIS (online)";

                string addr = attrs["PHYADDR1"]?.Value<string>() ?? "";
                string city = attrs["PHYCITY"]?.Value<string>()  ?? "";
                if (!string.IsNullOrWhiteSpace(addr) && string.IsNullOrWhiteSpace(site.MatchedAddress))
                    site.MatchedAddress = $"{addr}, {city}, FL";

                return (true, site);
            }
            catch
            {
                site.ParcelSuccess = false;
                return (false, site);
            }
        }

        private async Task<(bool, SiteData)> QueryCountyPaAsync(
            SiteData site,
            (string Name, string Url,
             string ParcelField, string AddrField, string CityField,
             string YrField, string ConstField,
             double MinLat, double MaxLat, double MinLon, double MaxLon) pa)
        {
            try
            {
                string url = pa.Url
                    .Replace("{lat:F6}", site.Latitude.ToString("F6"))
                    .Replace("{lon:F6}", site.Longitude.ToString("F6"));

                string json = await _http.GetStringAsync(url);
                var attrs   = FirstAttributes(json);
                if (attrs == null) return (false, site);

                site.ParcelId          = attrs[pa.ParcelField]?.Value<string>() ?? "";
                site.YearBuilt         = attrs[pa.YrField]?.Value<int>()        ?? 0;
                site.ConstructionClass = attrs[pa.ConstField]?.Value<string>()  ?? "";
                site.ParcelSuccess     = true;
                site.ParcelSource      = $"{pa.Name} County PA (online)";

                string addr = attrs[pa.AddrField]?.Value<string>() ?? "";
                string city = attrs[pa.CityField]?.Value<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(addr) && string.IsNullOrWhiteSpace(site.MatchedAddress))
                    site.MatchedAddress = $"{addr}, {city}, FL";

                return (true, site);
            }
            catch
            {
                site.ParcelSuccess = false;
                return (false, site);
            }
        }

        private async Task<(bool, SiteData)> QueryFgioAsync(SiteData site)
        {
            foreach (string template in new[] { FgioUrl, FgioFallbackUrl })
            {
                try
                {
                    string url  = string.Format(template, site.Longitude, site.Latitude);
                    string json = await _http.GetStringAsync(url);
                    var attrs   = FirstAttributes(json);
                    if (attrs == null) continue;

                    site.ParcelId          = CoalesceStr(attrs, "PARCELID", "PARID", "STRAP") ?? "";
                    site.YearBuilt         = attrs["YRBLT"]?.Value<int>() ?? 0;
                    site.ConstructionClass = CoalesceStr(attrs, "CONSTCLASS", "CONST_CLASS", "DORUC") ?? "";

                    string addr = CoalesceStr(attrs, "SITUSADDR", "SITEADDR", "PHYADDR1") ?? "";
                    string city = CoalesceStr(attrs, "SITUSCITY", "SITECITY",  "PHYCITY")  ?? "";
                    if (!string.IsNullOrWhiteSpace(addr) && string.IsNullOrWhiteSpace(site.MatchedAddress))
                        site.MatchedAddress = $"{addr}, {city}, FL";

                    site.ParcelSuccess = true;
                    site.ParcelSource  = "Florida statewide parcel (online)";
                    return (true, site);
                }
                catch { /* try next */ }
            }

            site.ParcelSuccess = false;
            return (false, site);
        }

        private async Task<(bool, SiteData)> QuerySwfwmdAsync(SiteData site, int layerId)
        {
            try
            {
                string url = string.Format(SwfwmdBase, layerId, site.Longitude, site.Latitude);
                string json = await _http.GetStringAsync(url);
                var attrs = FirstAttributes(json);
                if (attrs == null) return (false, site);

                site.ParcelId  = attrs["PARNO"]?.Value<string>() ?? "";
                site.YearBuilt = attrs["YRBLT_ACT"]?.Value<int>() ?? 0;
                if (site.YearBuilt == 0)
                    site.YearBuilt = attrs["YRBLT_EFF"]?.Value<int>() ?? 0;

                site.ConstructionClass = attrs["PARUSEDESC"]?.Value<string>() ?? "";
                site.ParcelSuccess = true;
                site.ParcelSource  = "SWFWMD parcel service (online, no CONSTCLASS)";

                string addr = attrs["SITEADD"]?.Value<string>() ?? "";
                string city = attrs["SCITY"]?.Value<string>()   ?? "";
                if (!string.IsNullOrWhiteSpace(addr) && string.IsNullOrWhiteSpace(site.MatchedAddress))
                    site.MatchedAddress = $"{addr}, {city}, FL";

                return (true, site);
            }
            catch
            {
                site.ParcelSuccess = false;
                return (false, site);
            }
        }

        // ── Shared helpers ────────────────────────────────────────────────────

        private static string GetOrEmpty(Dictionary<string, string> d, string key) =>
            d.TryGetValue(key, out string v) ? v ?? "" : "";

        private static bool IsInBbox(double lat, double lon,
            double minLat, double maxLat, double minLon, double maxLon) =>
            lat >= minLat && lat <= maxLat && lon >= minLon && lon <= maxLon;

        private static JToken FirstAttributes(string json)
        {
            var features = JObject.Parse(json)["features"] as JArray;
            if (features == null || features.Count == 0) return null;
            return features[0]["attributes"];
        }

        private static string CoalesceStr(JToken attrs, params string[] fields)
        {
            foreach (string f in fields)
            {
                var v = attrs[f]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }
    }
}
