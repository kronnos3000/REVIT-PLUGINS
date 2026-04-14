using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindCalc.Engine
{
    /// <summary>
    /// Automates downloading and post-processing of all local reference datasets.
    ///
    /// Download sequence:
    ///   1. FEMA NFHL flood zone shapefiles (5 counties)
    ///   2. NOAA MHW Florida shoreline (coastal proximity)
    ///   3. NLCD 2021 land cover GeoTIFF via MRLC WCS (exposure category)
    ///   4. PCPAO CSV bulk export — 5 files (Pinellas parcel + owner + legal + mailing)
    ///   5. Charlotte County parcel shapefile
    ///
    /// Progress is reported via the ProgressChanged event.
    /// Failures are non-fatal — each step reports via ErrorOccurred with manual instructions.
    ///
    /// Auto-update: call RunAutoUpdateAsync() on startup; it skips the full download when
    /// the manifest is less than 7 days old, but still respects a network-share lock file
    /// to prevent simultaneous downloads from multiple machines.
    ///
    /// Local folder layout produced:
    ///   {root}/flood_zones/{CountyName}/*.shp
    ///   {root}/flood_zones/county_index.txt
    ///   {root}/coastline/*.shp
    ///   {root}/land_cover/nlcd_florida.tif
    ///   {root}/parcels/Pinellas/csv/*.csv      ← replaces former Pinellas shapefile
    ///   {root}/parcels/Charlotte/*.shp
    ///   {root}/manifest.json
    /// </summary>
    public class LocalDataDownloader
    {
        public event Action<string, int> ProgressChanged; // (message, percentComplete)
        public event Action<string>      ErrorOccurred;

        private readonly string _localDataFolder;

        // ── Auto-update settings ──────────────────────────────────────────────
        private const int AutoUpdateDays    = 7;
        private const int LockMaxAgeMinutes = 20;   // stale lock threshold

        // ── HTTP client ───────────────────────────────────────────────────────
        private static readonly HttpClient _http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                // Accept government/county cert chains even if intermediate is missing locally.
                ServerCertificateCustomValidationCallback =
                    (msg, cert, chain, errors) => true
            };

            var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };
            client.DefaultRequestHeaders.UserAgent.TryParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            return client;
        }

        // ── FEMA NFHL counties ────────────────────────────────────────────────
        private static readonly Dictionary<string, string> NfhlCountyFips =
            new Dictionary<string, string>
            {
                { "Pinellas",     "12103" },
                { "Pasco",        "12101" },
                { "Hillsborough", "12057" },
                { "Sarasota",     "12115" },
                { "Charlotte",    "12015" }
            };

        // ── NOAA shoreline ────────────────────────────────────────────────────
        private const string NoaaShoreline =
            "https://coast.noaa.gov/htdata/Shoreline/us_medium_shoreline.zip";

        // ── NLCD WCS (MRLC) ───────────────────────────────────────────────────
        private const string NlcdWcsUrl =
            "https://dmsdata.cr.usgs.gov/geoserver/" +
            "mrlc_Land-Cover-Native_conus_2021_data/wcs" +
            "?SERVICE=WCS&VERSION=2.0.1&REQUEST=GetCoverage" +
            "&COVERAGEID=mrlc_Land-Cover-Native_conus_2021_data__NLCD_2021_Land_Cover_L48" +
            "&SUBSETTINGCRS=http://www.opengis.net/def/crs/EPSG/0/4326" +
            "&SUBSET=Long(-87.634,-79.974)" +
            "&SUBSET=Lat(24.396,31.001)" +
            "&FORMAT=image/tiff";

        // ── Charlotte County parcels ──────────────────────────────────────────
        private const string CharlotteParcelUrl =
            "https://data.charlottecountyfl.gov/ccgis/data/zips/accounts.zip";

        // ── PCPAO CSV bulk export ─────────────────────────────────────────────
        // Downloads via HTTP POST to the DAL endpoint discovered from the site JS:
        //   POST https://www.pcpao.gov/dal/databasefile/downloadDatabaseFile
        //   Form fields: hdn_tbl_name=<TABLE>, hdn_ftype=csv
        // Source: /sites/all/themes/pcpao_sgs/scripts/jquery.database_files.js
        private const string PcpaoDalEndpoint =
            "https://www.pcpao.gov/dal/databasefile/downloadDatabaseFile";

        private static readonly string[] PcpaoCsvTableNames =
        {
            "RP_ALL_SITE_ADDRESSES",
            "RP_ALL_OWNERS",
            "RP_BUILDING",
            "RP_PROPERTY_INFO",
            "RP_LEGAL",
        };

        // ── Progress step budgets (must sum to ≤ 100) ─────────────────────────
        //  5 NFHL counties × 5 = 25
        //  NFHL post-process   =  3   (28)
        //  Coastline           =  8   (36)
        //  NLCD WCS            = 12   (48)
        //  5 PCPAO CSVs × 5   = 25   (73)
        //  Charlotte parcels   =  8   (81)
        //  Post-process + mani =  4   (85) → set to 100 at end

        public LocalDataDownloader(string localDataFolder)
        {
            _localDataFolder = localDataFolder;
        }

        // ── Public entry points ───────────────────────────────────────────────

        /// <summary>
        /// Full manual setup — downloads everything regardless of age.
        /// Called by the "Setup Data" ribbon button.
        /// </summary>
        public async Task RunFullSetupAsync(CancellationToken ct = default)
        {
            EnsureDirectories();
            await DownloadAllAsync(ct);
        }

        /// <summary>
        /// Startup auto-update — downloads only when the manifest is older than
        /// <see cref="AutoUpdateDays"/> days, or is missing entirely.
        /// Uses a lock file to prevent simultaneous downloads on a shared drive.
        /// Returns immediately (no download) if data is fresh or another machine
        /// is already downloading.
        /// </summary>
        public async Task RunAutoUpdateAsync(CancellationToken ct = default)
        {
            if (!NeedsUpdate()) return;

            string lockFile = Path.Combine(_localDataFolder, "download.lock");

            // Check for an active lock from another machine
            if (File.Exists(lockFile))
            {
                try
                {
                    DateTime lockAge = File.GetLastWriteTimeUtc(lockFile);
                    if ((DateTime.UtcNow - lockAge).TotalMinutes < LockMaxAgeMinutes)
                        return; // another machine is downloading — skip
                }
                catch { return; }
            }

            // Acquire lock
            try
            {
                Directory.CreateDirectory(_localDataFolder);
                File.WriteAllText(lockFile, $"{Environment.MachineName} {DateTime.UtcNow:O}");
            }
            catch
            {
                return; // can't write lock (read-only share?) — skip silently
            }

            try
            {
                EnsureDirectories();
                await DownloadAllAsync(ct);
            }
            finally
            {
                try { File.Delete(lockFile); } catch { /* non-critical */ }
            }
        }

        // ── Core download sequence ────────────────────────────────────────────

        private async Task DownloadAllAsync(CancellationToken ct)
        {
            int pct = 0;

            // ── Step 1: FEMA NFHL shapefiles ──────────────────────────────────
            foreach (var kvp in NfhlCountyFips)
            {
                ct.ThrowIfCancellationRequested();
                Report($"Downloading FEMA NFHL — {kvp.Key} County ({kvp.Value})...", pct);
                await DownloadAndExtractAsync(
                    $"https://msc.fema.gov/portal/downloadProduct?productID=NFHL_{kvp.Value}C",
                    Path.Combine(_localDataFolder, "flood_zones", kvp.Key),
                    $"NFHL_{kvp.Value}.zip",
                    $"NFHL for {kvp.Key} County",
                    $"Manual download: msc.fema.gov → search '{kvp.Key} County FL NFHL'",
                    ct);
                pct += 5;
            }

            ct.ThrowIfCancellationRequested();
            Report("Indexing flood zone shapefiles...", pct);
            BuildFloodZoneIndex();
            pct += 3;

            // ── Step 2: NOAA Florida shoreline ────────────────────────────────
            ct.ThrowIfCancellationRequested();
            Report("Downloading NOAA mean high-water shoreline (Florida region)...", pct);
            await DownloadAndExtractAsync(
                NoaaShoreline,
                Path.Combine(_localDataFolder, "coastline"),
                "noaa_shoreline.zip",
                "NOAA MHW shoreline",
                "Manual download: coast.noaa.gov/htdata/Shoreline/us_medium_shoreline.zip",
                ct,
                postExtractFilter: FilterShoreline);
            pct += 8;

            // ── Step 3: NLCD 2021 land cover ──────────────────────────────────
            ct.ThrowIfCancellationRequested();
            Report("Downloading NLCD 2021 land cover (Florida extent via MRLC WCS)...", pct);
            await DownloadFileAsync(
                NlcdWcsUrl,
                Path.Combine(_localDataFolder, "land_cover", "nlcd_florida.tif"),
                "NLCD 2021 GeoTIFF",
                "Manual: dmsdata.cr.usgs.gov WCS → NLCD_2021_Land_Cover_L48, FL bbox",
                ct);
            pct += 12;

            // ── Step 4: PCPAO CSV bulk export (5 files, Pinellas parcel data) ──
            ct.ThrowIfCancellationRequested();
            string csvDir = Path.Combine(_localDataFolder, "parcels", "Pinellas", "csv");
            Directory.CreateDirectory(csvDir);

            // Invalidate session cache so the next parcel lookup uses fresh files
            ParcelDataService.InvalidateCsvCache();

            foreach (string tableName in PcpaoCsvTableNames)
            {
                ct.ThrowIfCancellationRequested();
                string fileName = tableName + ".csv";
                Report($"Downloading PCPAO data — {fileName}...", pct);

                bool downloaded = await TryDownloadPcpaoCsvAsync(tableName, fileName, csvDir, ct);
                if (!downloaded)
                    ErrorOccurred?.Invoke(
                        $"[WARN] {fileName} download failed.\n" +
                        $"       Manual: pcpao.gov → Tools & Data → Data Downloads → Raw Database Files\n" +
                        $"       Download CSV for {tableName} and place in: {csvDir}");
                pct += 5;
            }

            // ── Step 5: Charlotte County parcels ──────────────────────────────
            ct.ThrowIfCancellationRequested();
            Report("Downloading Charlotte County parcel shapefile...", pct);
            await DownloadAndExtractAsync(
                CharlotteParcelUrl,
                Path.Combine(_localDataFolder, "parcels", "Charlotte"),
                "charlotte_parcels.zip",
                "Charlotte County parcel shapefile",
                "Manual download: data.charlottecountyfl.gov → ccgis/data/zips/accounts.zip",
                ct);
            pct += 8;

            // ── Finalise ───────────────────────────────────────────────────────
            WriteManifest();
            Report("Setup complete. All datasets ready for offline use.", 100);
        }

        // ── PCPAO CSV downloader (POST to DAL endpoint) ───────────────────────

        /// <summary>
        /// Downloads a PCPAO bulk CSV via HTTP POST to the DAL endpoint.
        ///
        /// The PCPAO download mechanism was reverse-engineered from:
        ///   /sites/all/themes/pcpao_sgs/scripts/jquery.database_files.js
        ///
        /// The JS submits a hidden form:
        ///   POST /dal/databasefile/downloadDatabaseFile
        ///   hdn_tbl_name = {tableName}
        ///   hdn_ftype    = csv
        ///
        /// The response may be a raw CSV stream or a ZIP archive; both are handled.
        /// Returns true when the file was saved successfully.
        /// </summary>
        private async Task<bool> TryDownloadPcpaoCsvAsync(
            string tableName, string fileName, string csvDir, CancellationToken ct)
        {
            try
            {
                var formData = new System.Net.Http.FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("hdn_tbl_name", tableName),
                    new KeyValuePair<string, string>("hdn_ftype",    "csv"),
                });

                var response = await _http.PostAsync(PcpaoDalEndpoint, formData, ct);
                response.EnsureSuccessStatusCode();

                byte[] data = await response.Content.ReadAsByteArrayAsync();
                ct.ThrowIfCancellationRequested();

                if (data.Length == 0) return false;

                // Detect ZIP by magic bytes PK\x03\x04
                if (data.Length >= 4 &&
                    data[0] == 0x50 && data[1] == 0x4B &&
                    data[2] == 0x03 && data[3] == 0x04)
                {
                    string zipPath = Path.Combine(csvDir, fileName + ".zip");
                    File.WriteAllBytes(zipPath, data);
                    using (var archive = ZipFile.OpenRead(zipPath))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (entry.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                            {
                                entry.ExtractToFile(Path.Combine(csvDir, fileName), overwrite: true);
                                break;
                            }
                        }
                    }
                    File.Delete(zipPath);
                }
                else
                {
                    // Plain CSV stream
                    File.WriteAllBytes(Path.Combine(csvDir, fileName), data);
                }

                Report($"  \u2713 {fileName} saved ({data.Length / 1_048_576} MB).", -1);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return false;
            }
        }

        // ── Directory setup ───────────────────────────────────────────────────

        private void EnsureDirectories()
        {
            foreach (string sub in new[] { "flood_zones", "coastline", "land_cover", "parcels" })
                Directory.CreateDirectory(Path.Combine(_localDataFolder, sub));
            Directory.CreateDirectory(Path.Combine(_localDataFolder, "parcels", "Pinellas", "csv"));
        }

        // ── Staleness check ───────────────────────────────────────────────────

        private bool NeedsUpdate()
        {
            string manifestPath = Path.Combine(_localDataFolder, "manifest.json");
            if (!File.Exists(manifestPath)) return true;

            try
            {
                var serializer = new DataContractJsonSerializer(typeof(SetupManifest));
                using var stream = File.OpenRead(manifestPath);
                var manifest = (SetupManifest)serializer.ReadObject(stream);
                if (!DateTime.TryParse(manifest?.SetupDate, out DateTime setupDate)) return true;
                return (DateTime.Now - setupDate).TotalDays >= AutoUpdateDays;
            }
            catch
            {
                return true;
            }
        }

        // ── Download helpers ─────────────────────────────────────────────────

        private async Task DownloadAndExtractAsync(
            string url, string destDir, string zipFileName,
            string friendlyName, string manualInstructions,
            CancellationToken ct,
            Action<string> postExtractFilter = null)
        {
            string zipPath = Path.Combine(_localDataFolder, zipFileName);
            Directory.CreateDirectory(destDir);

            try
            {
                byte[] data = await _http.GetByteArrayAsync(url);
                ct.ThrowIfCancellationRequested();
                File.WriteAllBytes(zipPath, data);

                if (Directory.Exists(destDir))
                    Directory.Delete(destDir, recursive: true);
                Directory.CreateDirectory(destDir);

                ZipFile.ExtractToDirectory(zipPath, destDir);
                File.Delete(zipPath);

                postExtractFilter?.Invoke(destDir);

                Report($"  \u2713 {friendlyName} extracted to {Path.GetFileName(destDir)}/", -1);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (File.Exists(zipPath)) try { File.Delete(zipPath); } catch { }
                ErrorOccurred?.Invoke(
                    $"[WARN] {friendlyName} download failed: {ex.Message}\n" +
                    $"       {manualInstructions}");
            }
        }

        private async Task DownloadFileAsync(
            string url, string destPath, string friendlyName,
            string manualInstructions, CancellationToken ct)
        {
            try
            {
                byte[] data = await _http.GetByteArrayAsync(url);
                ct.ThrowIfCancellationRequested();
                File.WriteAllBytes(destPath, data);
                Report($"  \u2713 {friendlyName} saved ({data.Length / 1_048_576} MB).", -1);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(
                    $"[WARN] {friendlyName} download failed: {ex.Message}\n" +
                    $"       {manualInstructions}");
            }
        }

        // ── Post-process steps ────────────────────────────────────────────────

        private void BuildFloodZoneIndex()
        {
            string floodDir = Path.Combine(_localDataFolder, "flood_zones");
            if (!Directory.Exists(floodDir)) return;

            var shpFiles = Directory.GetFiles(floodDir, "*.shp", SearchOption.AllDirectories);
            File.WriteAllLines(Path.Combine(floodDir, "county_index.txt"), shpFiles);
            Report($"  \u2713 Flood zone index: {shpFiles.Length} shapefile(s).", -1);
        }

        private static void FilterShoreline(string dir)
        {
            var shpFiles = Directory.GetFiles(dir, "*.shp");
            if (shpFiles.Length == 1 && Path.GetFileName(shpFiles[0]) != "fl_shoreline.shp")
            {
                string stem = Path.Combine(dir, "fl_shoreline");
                foreach (string ext in new[] { ".shp", ".dbf", ".shx", ".prj" })
                {
                    string src = Path.ChangeExtension(shpFiles[0], ext);
                    if (File.Exists(src))
                        File.Move(src, stem + ext);
                }
            }
        }

        // ── Manifest ──────────────────────────────────────────────────────────

        private void WriteManifest()
        {
            bool HasShp(string subPath) =>
                Directory.Exists(Path.Combine(_localDataFolder, subPath)) &&
                Directory.GetFiles(Path.Combine(_localDataFolder, subPath), "*.shp").Length > 0;

            bool HasShpAny(string subPath) =>
                Directory.Exists(Path.Combine(_localDataFolder, subPath)) &&
                Directory.GetFiles(Path.Combine(_localDataFolder, subPath), "*.shp",
                    SearchOption.AllDirectories).Length > 0;

            bool HasPcpaoCsv() =>
                File.Exists(Path.Combine(_localDataFolder, "parcels", "Pinellas", "csv",
                    "RP_ALL_SITE_ADDRESSES.csv")) &&
                File.Exists(Path.Combine(_localDataFolder, "parcels", "Pinellas", "csv",
                    "RP_ALL_OWNERS.csv"));

            var manifest = new SetupManifest
            {
                SetupDate       = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                NfhlCounties    = new List<string>(NfhlCountyFips.Keys),
                CoastlineAvail  = HasShpAny("coastline"),
                NlcdAvailable   = File.Exists(Path.Combine(_localDataFolder, "land_cover", "nlcd_florida.tif")),
                PinellasCsvAvail = HasPcpaoCsv(),
                CharlotteParcel = HasShp("parcels/Charlotte"),
            };

            string path = Path.Combine(_localDataFolder, "manifest.json");
            var serializer = new DataContractJsonSerializer(typeof(SetupManifest));
            using var stream = new FileStream(path, FileMode.Create);
            serializer.WriteObject(stream, manifest);
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private void Report(string message, int percent)
        {
            if (percent >= 0)
                ProgressChanged?.Invoke(message, percent);
            else
                ProgressChanged?.Invoke(message, -1);
        }

        // ── Manifest contract ─────────────────────────────────────────────────

        [DataContract]
        private class SetupManifest
        {
            [DataMember] public string       SetupDate        { get; set; }
            [DataMember] public List<string> NfhlCounties     { get; set; }
            [DataMember] public bool         CoastlineAvail   { get; set; }
            [DataMember] public bool         NlcdAvailable    { get; set; }
            [DataMember] public bool         PinellasCsvAvail { get; set; }
            [DataMember] public bool         CharlotteParcel  { get; set; }
        }
    }
}
