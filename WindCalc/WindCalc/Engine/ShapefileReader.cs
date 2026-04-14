using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WindCalc.Engine
{
    /// <summary>
    /// Lightweight ESRI Shapefile reader for point-in-polygon queries.
    /// Supports only Polygon (type 5) and PolygonZ (type 15) shapes, which
    /// cover FEMA NFHL flood zone shapefiles.
    ///
    /// Does NOT require NetTopologySuite — pure .NET Framework 4.8.
    ///
    /// Reference: ESRI Shapefile Technical Description (July 1998)
    /// </summary>
    public class ShapefileReader
    {
        // ── Shape type constants ───────────────────────────────────────────
        private const int ShapeNull    = 0;
        private const int ShapePoint   = 1;
        private const int ShapePolygon = 5;
        private const int ShapePolygonZ = 15;
        private const int ShapePolygonM = 25;

        // ── Point-in-polygon query ─────────────────────────────────────────

        /// <summary>
        /// Returns the first record in the shapefile where the given lat/lon
        /// is inside the polygon, along with requested DBF field values.
        /// Returns null if no match found.
        /// </summary>
        public static Dictionary<string, string> FindContainingRecord(
            string shpPath, double lat, double lon, params string[] fieldNames)
        {
            string dbfPath = Path.ChangeExtension(shpPath, ".dbf");
            if (!File.Exists(shpPath) || !File.Exists(dbfPath))
                throw new FileNotFoundException($"Shapefile not found: {shpPath}");

            // Read DBF header to find field offsets
            var (dbfFields, recordSize, headerSize, recordCount) = ReadDbfHeader(dbfPath);
            var fieldIndices = ResolveFieldIndices(dbfFields, fieldNames);

            using var shpStream = new BinaryReader(File.OpenRead(shpPath), Encoding.ASCII);
            using var dbfStream = new BinaryReader(File.OpenRead(dbfPath), Encoding.ASCII);

            // Skip SHP file header (100 bytes)
            shpStream.BaseStream.Seek(100, SeekOrigin.Begin);

            // Skip DBF header
            dbfStream.BaseStream.Seek(headerSize, SeekOrigin.Begin);

            int recordIdx = 0;
            while (shpStream.BaseStream.Position < shpStream.BaseStream.Length)
            {
                // SHP record header: record number (big-endian), content length (big-endian)
                if (shpStream.BaseStream.Position + 8 > shpStream.BaseStream.Length) break;

                int recNum = ReadInt32BigEndian(shpStream);
                int contentLen = ReadInt32BigEndian(shpStream) * 2; // in 16-bit words → bytes

                long contentStart = shpStream.BaseStream.Position;

                int shapeType = shpStream.ReadInt32(); // little-endian

                bool match = false;

                if (shapeType == ShapePolygon || shapeType == ShapePolygonZ || shapeType == ShapePolygonM)
                {
                    // Bounding box (4 doubles: Xmin, Ymin, Xmax, Ymax)
                    double xMin = shpStream.ReadDouble();
                    double yMin = shpStream.ReadDouble();
                    double xMax = shpStream.ReadDouble();
                    double yMax = shpStream.ReadDouble();

                    // Quick bbox reject: lon=X, lat=Y
                    if (lon >= xMin && lon <= xMax && lat >= yMin && lat <= yMax)
                    {
                        int numParts  = shpStream.ReadInt32();
                        int numPoints = shpStream.ReadInt32();

                        int[] parts = new int[numParts];
                        for (int i = 0; i < numParts; i++)
                            parts[i] = shpStream.ReadInt32();

                        var pts = new (double x, double y)[numPoints];
                        for (int i = 0; i < numPoints; i++)
                            pts[i] = (shpStream.ReadDouble(), shpStream.ReadDouble());

                        // Test point-in-polygon for each ring
                        for (int p = 0; p < numParts && !match; p++)
                        {
                            int start = parts[p];
                            int end   = (p + 1 < numParts) ? parts[p + 1] : numPoints;
                            match = PointInRing(lon, lat, pts, start, end);
                        }
                    }
                }

                // Seek to end of record regardless (handles skipped/null shapes)
                shpStream.BaseStream.Seek(contentStart + contentLen, SeekOrigin.Begin);

                if (match && recordIdx < recordCount)
                {
                    // Read corresponding DBF record
                    dbfStream.BaseStream.Seek(headerSize + (long)recordIdx * recordSize, SeekOrigin.Begin);
                    byte deletionFlag = dbfStream.ReadByte();

                    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (fieldName, offset, length) in fieldIndices)
                    {
                        dbfStream.BaseStream.Seek(
                            headerSize + (long)recordIdx * recordSize + 1 + offset,
                            SeekOrigin.Begin);
                        byte[] raw = dbfStream.ReadBytes(length);
                        result[fieldName] = Encoding.ASCII.GetString(raw).Trim();
                    }
                    return result;
                }

                recordIdx++;
            }

            return null; // no polygon contains the point
        }

        // ── Ray-casting point-in-ring ──────────────────────────────────────

        private static bool PointInRing(double px, double py,
            (double x, double y)[] pts, int start, int end)
        {
            bool inside = false;
            int n = end - start;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = pts[start + i].x, yi = pts[start + i].y;
                double xj = pts[start + j].x, yj = pts[start + j].y;
                if ((yi > py) != (yj > py) &&
                    px < (xj - xi) * (py - yi) / (yj - yi) + xi)
                    inside = !inside;
            }
            return inside;
        }

        // ── DBF header parsing ─────────────────────────────────────────────

        private static (List<(string name, int offset, int length)> fields,
                        int recordSize, int headerSize, int recordCount)
            ReadDbfHeader(string dbfPath)
        {
            using var br = new BinaryReader(File.OpenRead(dbfPath), Encoding.ASCII);

            br.ReadByte(); // version
            br.ReadBytes(3); // last update date

            int recordCount = br.ReadInt32();
            int headerSize  = br.ReadInt16();
            int recordSize  = br.ReadInt16();
            br.ReadBytes(20); // reserved

            var fields  = new List<(string name, int offset, int length)>();
            int fieldOffset = 1; // starts at 1 (first byte of record is deletion flag)

            while (br.BaseStream.Position < headerSize - 1)
            {
                byte[] nameBytes = br.ReadBytes(11);
                string name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                char type   = (char)br.ReadByte();
                br.ReadBytes(4); // reserved
                int length = br.ReadByte();
                br.ReadBytes(15); // decimal count + reserved

                fields.Add((name, fieldOffset, length));
                fieldOffset += length;

                if (br.BaseStream.Position >= headerSize - 1) break;
            }

            return (fields, recordSize, headerSize, recordCount);
        }

        private static List<(string name, int offset, int length)> ResolveFieldIndices(
            List<(string name, int offset, int length)> allFields, string[] requested)
        {
            var result = new List<(string, int, int)>();
            foreach (string req in requested)
            {
                foreach (var f in allFields)
                {
                    if (string.Equals(f.name, req, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(f);
                        break;
                    }
                }
            }
            return result;
        }

        // ── Nearest-point on Polyline query ───────────────────────────────

        /// <summary>
        /// Returns the distance in miles from a lat/lon point to the nearest
        /// segment in a Polyline (type 3), PolylineZ (type 13), or PolylineM (type 23) shapefile.
        /// Only records whose bounding box overlaps the search radius are tested.
        /// Returns double.MaxValue if no segments are found.
        /// </summary>
        public static double FindNearestPolylineDistanceMiles(
            string shpPath, double lat, double lon, double searchRadiusMiles = 5.0)
        {
            if (!File.Exists(shpPath))
                throw new FileNotFoundException($"Shapefile not found: {shpPath}");

            double minDist = double.MaxValue;
            // Convert miles to rough degree buffer for bbox pre-filter
            double degBuf = searchRadiusMiles / 60.0;

            using var shpStream = new BinaryReader(File.OpenRead(shpPath), Encoding.ASCII);
            shpStream.BaseStream.Seek(100, SeekOrigin.Begin); // skip file header

            while (shpStream.BaseStream.Position + 8 <= shpStream.BaseStream.Length)
            {
                ReadInt32BigEndian(shpStream);                          // record number (skip)
                int contentLen  = ReadInt32BigEndian(shpStream) * 2;   // 16-bit words → bytes
                long contentStart = shpStream.BaseStream.Position;

                int shapeType = shpStream.ReadInt32();

                if (shapeType == 3 || shapeType == 13 || shapeType == 23) // Polyline / Z / M
                {
                    double xMin = shpStream.ReadDouble();
                    double yMin = shpStream.ReadDouble();
                    double xMax = shpStream.ReadDouble();
                    double yMax = shpStream.ReadDouble();

                    if (lon >= xMin - degBuf && lon <= xMax + degBuf &&
                        lat >= yMin - degBuf && lat <= yMax + degBuf)
                    {
                        int numParts  = shpStream.ReadInt32();
                        int numPoints = shpStream.ReadInt32();

                        int[] parts = new int[numParts];
                        for (int i = 0; i < numParts; i++)
                            parts[i] = shpStream.ReadInt32();

                        var pts = new (double x, double y)[numPoints];
                        for (int i = 0; i < numPoints; i++)
                            pts[i] = (shpStream.ReadDouble(), shpStream.ReadDouble());

                        for (int p = 0; p < numParts; p++)
                        {
                            int start = parts[p];
                            int end   = (p + 1 < numParts) ? parts[p + 1] : numPoints;

                            for (int i = start; i < end - 1; i++)
                            {
                                double d = PointToSegmentMiles(
                                    lon, lat,
                                    pts[i].x,     pts[i].y,
                                    pts[i + 1].x, pts[i + 1].y);
                                if (d < minDist) minDist = d;
                            }
                        }
                    }
                }

                shpStream.BaseStream.Seek(contentStart + contentLen, SeekOrigin.Begin);
            }

            return minDist;
        }

        // ── Geometry helpers ───────────────────────────────────────────────

        private static double PointToSegmentMiles(
            double px, double py,
            double ax, double ay,
            double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            double lenSq = dx * dx + dy * dy;
            double t = lenSq > 0
                ? Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / lenSq))
                : 0;
            return HaversineMiles(py, px, ay + t * dy, ax + t * dx);
        }

        private static double HaversineMiles(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 3958.8;
            double dLat = (lat2 - lat1) * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                     + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                     * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2 * Math.Asin(Math.Sqrt(a));
        }

        // ── Big-endian int helper ──────────────────────────────────────────

        private static int ReadInt32BigEndian(BinaryReader br)
        {
            byte[] b = br.ReadBytes(4);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return BitConverter.ToInt32(b, 0);
        }
    }
}
