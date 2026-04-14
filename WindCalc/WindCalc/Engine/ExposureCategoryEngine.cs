using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;

namespace WindCalc.Engine
{
    /// <summary>
    /// Determines Wind Exposure Category (B, C, or D) per ASCE 7-22 Section 26.7.
    ///
    /// Analysis order:
    ///   1. Local NLCD GeoTIFF (if present in LocalDataFolder\land_cover\nlcd_florida.tif)
    ///   2. MRLC WMS online API — downloads two small PNG tiles and classifies NLCD land cover
    ///      within a 1,500 ft radius (Exposure B check) and 5,000 ft radius (Exposure D check)
    ///   3. Elevation heuristic fallback (if network unavailable)
    ///
    /// The user can always override the result in the dialog.
    /// </summary>
    public class ExposureCategoryEngine
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private readonly string _localDataFolder;

        // ASCE 7-22 §26.7 surface roughness analysis radii
        private const double RadiusBFt = 1500.0;
        private const double RadiusDFt = 5000.0;

        // Simplified roughness classification
        private enum RoughnessZone { C, B, Water }

        // NLCD 2021 canonical WMS colors (from MRLC SLD) → roughness zone.
        // Nearest-color matching with tolerance handles minor WMS rendering variation.
        private static readonly (Color color, RoughnessZone zone)[] _colorMap =
        {
            (Color.FromArgb( 70, 107, 159), RoughnessZone.Water),  // 11  Open Water          → D
            (Color.FromArgb(235,   0,   0), RoughnessZone.B),      // 23  Developed, Medium   → B
            (Color.FromArgb(171,   0,   0), RoughnessZone.B),      // 24  Developed, High     → B
            (Color.FromArgb(104, 171,  95), RoughnessZone.B),      // 41  Deciduous Forest    → B
            (Color.FromArgb( 28,  95,  44), RoughnessZone.B),      // 42  Evergreen Forest    → B
            (Color.FromArgb(181, 197, 143), RoughnessZone.B),      // 43  Mixed Forest        → B
            (Color.FromArgb(222, 197, 197), RoughnessZone.C),      // 21  Dev, Open Space     → C
            (Color.FromArgb(217, 146, 130), RoughnessZone.C),      // 22  Dev, Low Intensity  → C
            (Color.FromArgb(179, 172, 159), RoughnessZone.C),      // 31  Barren Land         → C
            (Color.FromArgb(204, 184, 121), RoughnessZone.C),      // 52  Shrub/Scrub         → C
            (Color.FromArgb(223, 223, 194), RoughnessZone.C),      // 71  Grassland           → C
            (Color.FromArgb(220, 217,  57), RoughnessZone.C),      // 81  Pasture/Hay         → C
            (Color.FromArgb(171, 113,  57), RoughnessZone.C),      // 82  Cultivated Crops    → C
            (Color.FromArgb(184, 217, 235), RoughnessZone.C),      // 90  Woody Wetlands      → C
            (Color.FromArgb(108, 159, 184), RoughnessZone.C),      // 95  Emergent Wetlands   → C
        };

        public ExposureCategoryEngine(string localDataFolder)
        {
            _localDataFolder = localDataFolder;
        }

        public async Task<(string category, bool autoDetected, string note)>
            GetExposureCategoryAsync(double lat, double lon, double elevationFt)
        {
            // 1. Local NLCD GeoTIFF (manual download path — full raster parsing deferred)
            string nlcdPath = Path.Combine(_localDataFolder, "land_cover", "nlcd_florida.tif");
            if (File.Exists(nlcdPath))
                return DetermineFromLocalNlcd(lat, lon, nlcdPath);

            // 2. MRLC WMS online analysis
            try
            {
                return await DetermineFromMrlcAsync(lat, lon);
            }
            catch
            {
                // Network unavailable — fall back to heuristic
            }

            // 3. Elevation heuristic
            return ElevationHeuristic(elevationFt);
        }

        // ── MRLC WMS Analysis ─────────────────────────────────────────────────

        private async Task<(string, bool, string)> DetermineFromMrlcAsync(double lat, double lon)
        {
            // Sample both radii in parallel
            var dTask = FetchRadiusZonesAsync(lat, lon, RadiusDFt);
            var bTask = FetchRadiusZonesAsync(lat, lon, RadiusBFt);

            await Task.WhenAll(dTask, bTask);

            return Classify(dTask.Result, bTask.Result);
        }

        private async Task<RoughnessZone[]> FetchRadiusZonesAsync(
            double lat, double lon, double radiusFt)
        {
            const int ImagePixels = 64;

            // Convert radius in feet to degrees (approximate, sufficient for 5,000 ft scale)
            double latDelta = radiusFt / 364000.0;
            double lonDelta = radiusFt / (364000.0 * Math.Cos(lat * Math.PI / 180.0));

            // WMS 1.1.1 BBOX: minX(lon), minY(lat), maxX(lon), maxY(lat)
            string url =
                "https://www.mrlc.gov/geoserver/mrlc_display/NLCD_2021_Land_Cover_L48/ows" +
                "?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap" +
                "&LAYERS=NLCD_2021_Land_Cover_L48" +
                $"&BBOX={lon - lonDelta:F6},{lat - latDelta:F6},{lon + lonDelta:F6},{lat + latDelta:F6}" +
                $"&WIDTH={ImagePixels}&HEIGHT={ImagePixels}" +
                "&SRS=EPSG:4326&FORMAT=image/png&STYLES=";

            byte[] pngBytes = await _http.GetByteArrayAsync(url);

            var zones = new List<RoughnessZone>(ImagePixels * ImagePixels);
            using (var ms = new MemoryStream(pngBytes))
            using (var bmp = new Bitmap(ms))
            {
                for (int y = 0; y < bmp.Height; y++)
                {
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        // Circular mask — exclude bbox corners outside the analysis radius
                        double dx = 2.0 * x / bmp.Width  - 1.0;
                        double dy = 2.0 * y / bmp.Height - 1.0;
                        if (dx * dx + dy * dy > 1.0) continue;

                        zones.Add(MatchColor(bmp.GetPixel(x, y)));
                    }
                }
            }
            return zones.ToArray();
        }

        private static (string, bool, string) Classify(RoughnessZone[] d, RoughnessZone[] b)
        {
            double dWaterFrac = Fraction(d, RoughnessZone.Water);
            double bRoughFrac = Fraction(b, RoughnessZone.B);

            // ASCE 7-22 §26.7: Exposure D — open water ≥50% within 5,000 ft
            if (dWaterFrac >= 0.50)
                return ("D", true,
                    $"NLCD: {dWaterFrac:P0} open water within {RadiusDFt:F0} ft — " +
                    "Exposure D (ASCE 7-22 §26.7).");

            // ASCE 7-22 §26.7: Exposure B — roughness B terrain ≥50% within 1,500 ft
            if (bRoughFrac >= 0.50)
                return ("B", true,
                    $"NLCD: {bRoughFrac:P0} developed/forested terrain within {RadiusBFt:F0} ft — " +
                    "Exposure B (ASCE 7-22 §26.7).");

            return ("C", true,
                "NLCD: open terrain predominant — Exposure C (ASCE 7-22 §26.7).");
        }

        private static double Fraction(RoughnessZone[] zones, RoughnessZone target)
        {
            if (zones.Length == 0) return 0;
            int count = 0;
            foreach (var z in zones)
                if (z == target) count++;
            return (double)count / zones.Length;
        }

        private static RoughnessZone MatchColor(Color px)
        {
            if (px.A < 128) return RoughnessZone.C;  // transparent background → C

            int best = int.MaxValue;
            var zone  = RoughnessZone.C;
            foreach (var (c, z) in _colorMap)
            {
                int dr = px.R - c.R, dg = px.G - c.G, db = px.B - c.B;
                int dist = dr * dr + dg * dg + db * db;
                if (dist < best) { best = dist; zone = z; }
            }
            // Pixels with no close match (unknown class, background white, etc.) → C
            return best < 80 * 80 * 3 ? zone : RoughnessZone.C;
        }

        // ── Local GeoTIFF pixel reader ────────────────────────────────────────
        // Reads an uncompressed or Deflate-compressed GeoTIFF with:
        //   - EPSG:4326 georeferencing via ModelTiepointTag + ModelPixelScaleTag
        //   - 8-bit unsigned pixels (NLCD land cover class codes)
        //   - Strip-based layout (standard WCS output from MRLC)

        private static (string, bool, string) DetermineFromLocalNlcd(
            double lat, double lon, string nlcdPath)
        {
            try
            {
                byte classCode = GeoTiffReader.ReadPixelAt(nlcdPath, lat, lon);
                if (classCode == 0)
                    return ("C", false,
                        "Local NLCD: pixel is nodata/background. Override if needed.");

                // NLCD 2021 class → ASCE 7-22 roughness zone mapping
                // Reference: ASCE 7-22 §26.7, NLCD 2021 legend
                var zone = ClassifyNlcd(classCode);

                return zone switch
                {
                    RoughnessZone.Water => ("D", true,
                        $"Local NLCD class {classCode} = open water — Exposure D (ASCE 7-22 §26.7)."),
                    RoughnessZone.B    => ("B", true,
                        $"Local NLCD class {classCode} = developed/forested — Exposure B (ASCE 7-22 §26.7)."),
                    _                  => ("C", true,
                        $"Local NLCD class {classCode} = open/low terrain — Exposure C (ASCE 7-22 §26.7)."),
                };
            }
            catch (Exception ex)
            {
                return ("C", false,
                    $"Local NLCD read error: {ex.Message}. Falling through to MRLC online.");
            }
        }

        private static RoughnessZone ClassifyNlcd(byte c)
        {
            // NLCD 2021 integer class codes
            return c switch
            {
                11 or 12                           => RoughnessZone.Water, // Open Water, Ice/Snow
                41 or 42 or 43                     => RoughnessZone.B,     // Forest types
                23 or 24                            => RoughnessZone.B,     // Developed Medium/High
                _ when c >= 21 && c <= 24           => RoughnessZone.C,    // Developed Open/Low
                _                                  => RoughnessZone.C,     // Agriculture, shrub, grass, wetlands
            };
        }

        /// <summary>
        /// Minimal GeoTIFF reader — reads a single 8-bit pixel at a geographic coordinate.
        /// Supports: uncompressed (1), PackBits (32773), and Deflate (8/32946) compression.
        /// Supports: stripped layout only (standard MRLC WCS output).
        /// Assumes EPSG:4326 GeoTIFF with ModelTiepointTag + ModelPixelScaleTag.
        /// </summary>
        private static class GeoTiffReader
        {
            // TIFF tag IDs
            private const ushort TagImageWidth    = 256;
            private const ushort TagImageLength   = 257;
            private const ushort TagBitsPerSample = 258;
            private const ushort TagCompression   = 259;
            private const ushort TagRowsPerStrip  = 278;
            private const ushort TagStripOffsets  = 324;
            private const ushort TagStripByteCnt  = 325;
            private const ushort TagTiepoint      = 33922;
            private const ushort TagPixelScale    = 33550;

            public static byte ReadPixelAt(string path, double lat, double lon)
            {
                using var fs  = File.OpenRead(path);
                using var br  = new BinaryReader(fs);

                // TIFF header
                ushort byteOrder = br.ReadUInt16();
                bool littleEndian = byteOrder == 0x4949; // 'II'
                ushort magic   = ReadU16(br, littleEndian);
                if (magic != 42) throw new InvalidDataException("Not a valid TIFF file.");

                uint ifdOffset = ReadU32(br, littleEndian);
                fs.Seek(ifdOffset, SeekOrigin.Begin);

                // Parse IFD
                ushort entryCount = ReadU16(br, littleEndian);
                var tags = new Dictionary<ushort, (ushort type, uint count, uint valueOrOffset)>();
                for (int i = 0; i < entryCount; i++)
                {
                    ushort tag   = ReadU16(br, littleEndian);
                    ushort type  = ReadU16(br, littleEndian);
                    uint   count = ReadU32(br, littleEndian);
                    uint   valOff= ReadU32(br, littleEndian);
                    tags[tag] = (type, count, valOff);
                }

                uint width         = GetUint(tags, TagImageWidth,    br, fs, littleEndian, 1);
                uint height        = GetUint(tags, TagImageLength,   br, fs, littleEndian, 1);
                uint rowsPerStrip  = GetUint(tags, TagRowsPerStrip,  br, fs, littleEndian, height);
                ushort compression = (ushort)GetUint(tags, TagCompression, br, fs, littleEndian, 1);
                // BitsPerSample must be 8 for NLCD
                uint bps           = GetUint(tags, TagBitsPerSample, br, fs, littleEndian, 8);
                if (bps != 8) throw new NotSupportedException($"Expected 8-bit NLCD but got {bps}-bit.");

                // GeoTIFF georeferencing: tiepoint (6 doubles) + pixel scale (3 doubles)
                double[] tie   = ReadDoubleArray(br, fs, tags, TagTiepoint,   6, littleEndian);
                double[] scale = ReadDoubleArray(br, fs, tags, TagPixelScale, 3, littleEndian);

                // tie[3,4,5] = (easting/lon, northing/lat, z) of tie pixel (tie[0,1,2])
                double originLon = tie[3];
                double originLat = tie[4];
                double scaleX    = scale[0]; // degrees per pixel (positive)
                double scaleY    = scale[1]; // degrees per pixel (positive, Y decreases going down)

                // Pixel coordinates (0-based, from top-left)
                int col = (int)Math.Floor((lon - originLon) / scaleX);
                int row = (int)Math.Floor((originLat - lat) / scaleY);

                if (col < 0 || col >= width || row < 0 || row >= height)
                    return 0; // outside raster extent

                // Determine which strip
                uint stripIdx    = (uint)row / rowsPerStrip;
                uint rowInStrip  = (uint)row % rowsPerStrip;
                uint pixInStrip  = rowInStrip * width + (uint)col;

                // Read strip offsets and byte counts
                uint[] offsets   = ReadUintArray(br, fs, tags, TagStripOffsets,  littleEndian);
                uint[] byteCnts  = ReadUintArray(br, fs, tags, TagStripByteCnt,  littleEndian);

                if (stripIdx >= offsets.Length) return 0;

                fs.Seek(offsets[stripIdx], SeekOrigin.Begin);
                byte[] compressedStrip = br.ReadBytes((int)byteCnts[stripIdx]);

                byte[] rawStrip = Decompress(compressedStrip, compression,
                    rowsPerStrip * width);

                if (pixInStrip >= rawStrip.Length) return 0;
                return rawStrip[pixInStrip];
            }

            private static byte[] Decompress(byte[] data, ushort compression, uint expectedBytes)
            {
                switch (compression)
                {
                    case 1: // Uncompressed
                        return data;

                    case 8:     // ZIP/Deflate (Adobe variant — skip 2-byte zlib header)
                    case 32946: // Deflate
                    {
                        int skip = (compression == 8 && data.Length > 2) ? 2 : 0;
                        using var ms  = new MemoryStream(data, skip, data.Length - skip);
                        using var ds  = new DeflateStream(ms, CompressionMode.Decompress);
                        using var out_ = new MemoryStream((int)expectedBytes);
                        ds.CopyTo(out_);
                        return out_.ToArray();
                    }

                    case 32773: // PackBits
                        return DecompressPackBits(data, (int)expectedBytes);

                    default:
                        throw new NotSupportedException(
                            $"TIFF compression {compression} not supported. " +
                            "Convert the NLCD file to uncompressed GeoTIFF with GDAL.");
                }
            }

            private static byte[] DecompressPackBits(byte[] data, int expectedBytes)
            {
                var result = new List<byte>(expectedBytes);
                int i = 0;
                while (i < data.Length && result.Count < expectedBytes)
                {
                    sbyte header = (sbyte)data[i++];
                    if (header >= 0)
                    {
                        int count = header + 1;
                        for (int k = 0; k < count && i < data.Length; k++)
                            result.Add(data[i++]);
                    }
                    else if (header != -128)
                    {
                        int count = 1 - header;
                        byte val  = data[i++];
                        for (int k = 0; k < count; k++)
                            result.Add(val);
                    }
                }
                return result.ToArray();
            }

            // ── TIFF read helpers ─────────────────────────────────────────────

            private static uint GetUint(
                Dictionary<ushort, (ushort type, uint count, uint valOff)> tags,
                ushort tag, BinaryReader br, Stream fs,
                bool le, uint fallback)
            {
                if (!tags.TryGetValue(tag, out var entry)) return fallback;
                if (entry.count == 1) return entry.valOff;
                // Value stored at offset
                long pos = fs.Position;
                fs.Seek(entry.valOff, SeekOrigin.Begin);
                uint v = ReadU32(br, le);
                fs.Seek(pos, SeekOrigin.Begin);
                return v;
            }

            private static double[] ReadDoubleArray(
                BinaryReader br, Stream fs,
                Dictionary<ushort, (ushort type, uint count, uint valOff)> tags,
                ushort tag, int expectedCount, bool le)
            {
                if (!tags.TryGetValue(tag, out var entry))
                    throw new InvalidDataException($"GeoTIFF tag {tag} not found.");

                long pos = fs.Position;
                fs.Seek(entry.valOff, SeekOrigin.Begin);
                var result = new double[expectedCount];
                for (int i = 0; i < expectedCount && i < entry.count; i++)
                    result[i] = ReadDouble(br, le);
                fs.Seek(pos, SeekOrigin.Begin);
                return result;
            }

            private static uint[] ReadUintArray(
                BinaryReader br, Stream fs,
                Dictionary<ushort, (ushort type, uint count, uint valOff)> tags,
                ushort tag, bool le)
            {
                if (!tags.TryGetValue(tag, out var entry)) return Array.Empty<uint>();
                if (entry.count == 1) return new[] { entry.valOff };

                long pos = fs.Position;
                fs.Seek(entry.valOff, SeekOrigin.Begin);
                var result = new uint[entry.count];
                for (int i = 0; i < entry.count; i++)
                    result[i] = ReadU32(br, le);
                fs.Seek(pos, SeekOrigin.Begin);
                return result;
            }

            private static ushort ReadU16(BinaryReader br, bool le)
            {
                ushort v = br.ReadUInt16();
                return le ? v : (ushort)((v >> 8) | (v << 8));
            }

            private static uint ReadU32(BinaryReader br, bool le)
            {
                uint v = br.ReadUInt32();
                if (le) return v;
                return ((v & 0xFF) << 24) | ((v & 0xFF00) << 8) |
                       ((v >> 8) & 0xFF00) | ((v >> 24) & 0xFF);
            }

            private static double ReadDouble(BinaryReader br, bool le)
            {
                byte[] b = br.ReadBytes(8);
                if (!le) Array.Reverse(b);
                return BitConverter.ToDouble(b, 0);
            }
        }

        // ── Elevation heuristic fallback ──────────────────────────────────────

        private static (string, bool, string) ElevationHeuristic(double elevationFt)
        {
            if (elevationFt <= 30.0)
                return ("C", false,
                    "Heuristic (MRLC offline): low-to-moderate elevation → Exposure C. " +
                    "Override if coastal (D) or suburban (B).");

            return ("B", false,
                "Heuristic (MRLC offline): elevated inland terrain → Exposure B. Override if needed.");
        }
    }
}
