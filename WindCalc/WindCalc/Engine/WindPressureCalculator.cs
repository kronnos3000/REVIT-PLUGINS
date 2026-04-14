using System;

namespace WindCalc.Engine
{
    /// <summary>
    /// Computes ASCE 7-22 C&C (Components and Cladding) design wind pressures
    /// for low-rise residential buildings.
    ///
    /// Velocity pressure: qh = 0.00256 * Kz * Kzt * Vasd² (psf)
    ///   — Kd is omitted here; the simplified residential method incorporates
    ///     directionality within the GCp values per FBC 2023 practice.
    ///
    /// Net pressure: p = qh * (GCp - GCpi)
    ///   — Positive:  qh * (GCp_pos + GCpi)  [interior suction worst case]
    ///   — Negative:  qh * (GCp_neg - GCpi)  [interior pressure worst case]
    /// </summary>
    public class WindPressureCalculator
    {
        // ── Constants ────────────────────────────────────────────────────────
        private const double Kzt  = 1.0;   // Topographic factor (flat terrain)
        private const double GCpi = 0.18;  // Enclosed building (ASCE 7-22 §26.13)

        // ── Public results ────────────────────────────────────────────────────
        public double Kz            { get; }   // Velocity pressure exposure coeff.
        public double Lambda        { get; }   // Height & exposure adj. coeff.
        public double Qh            { get; }   // Velocity pressure, psf
        public double SlopeAngleDeg { get; }

        private readonly string _roofType;

        public WindPressureCalculator(double vult, double vasd, string exposure,
            double meanRoofHeightFt, string roofType, string roofPitch)
        {
            _roofType     = roofType ?? "";
            SlopeAngleDeg = ParsePitchToDegrees(roofPitch);
            // WindborneDebrisArea is now computed in SiteData (see WindCalcEngine)
            // General threshold: Vult ≥ 140 mph; coastal (≤1 mi from coast): Vult ≥ 130 mph
            _ = vult; // consumed by caller, not needed for pressure calcs

            Kz     = CalcKz(meanRoofHeightFt, exposure);
            Lambda = CalcLambda(meanRoofHeightFt, exposure);
            Qh     = 0.00256 * Kz * Kzt * vasd * vasd;
        }

        // ── Zone pressures ────────────────────────────────────────────────────

        public struct ZonePressures { public double Pos, Neg; }

        public ZonePressures[] GetRoofZones()
        {
            var gcp = RoofGCp(SlopeAngleDeg);
            var z   = new ZonePressures[3];
            for (int i = 0; i < 3; i++)
                z[i] = Net(gcp[i, 0], gcp[i, 1]);
            return z;
        }

        public ZonePressures[] GetOverhangZones()
        {
            // ASCE 7-22 §30.11: overhang GCp = roof zone GCp (top) + 0.8 (bottom uplift)
            var roofGcp = RoofGCp(SlopeAngleDeg);
            var z = new ZonePressures[3];
            for (int i = 0; i < 3; i++)
            {
                double pos = roofGcp[i, 0] + 0.8;
                double neg = roofGcp[i, 1] - 0.8;
                z[i] = Net(pos, neg);
            }
            return z;
        }

        public ZonePressures[] GetWallZones()
        {
            // ASCE 7-22 Figure 30.3-1: Zones 4 (interior) and 5 (corner)
            // Effective wind area ~10 sf representative values
            return new[]
            {
                Net( 0.835, -0.918),  // Zone 4 interior
                Net( 0.835, -1.177),  // Zone 5 corner
            };
        }

        public ZonePressures GetGarageDoorPressures(bool largeDoor)
        {
            // Larger effective wind area → reduced GCp per ASCE 7-22 Fig 30.3-1
            return largeDoor ? Net(0.79, -0.90) : Net(0.835, -0.968);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private ZonePressures Net(double gcpPos, double gcpNeg) => new ZonePressures
        {
            Pos = Math.Round(Qh * (gcpPos + GCpi), 2),
            Neg = Math.Round(Qh * (gcpNeg - GCpi), 2)
        };

        /// GCp table for gable/hip roofs — ASCE 7-22 Figure 30.3-2A
        /// Rows: zones 1, 2, 3.  Columns: pos, neg.
        private static double[,] RoofGCp(double angle)
        {
            if (angle <= 10)
                return new double[,] { { 0.3, -1.0 }, { 0.3, -1.8 }, { 0.3, -2.8 } };
            if (angle <= 20)
                return new double[,] { { 0.5, -1.0 }, { 0.5, -1.8 }, { 0.5, -2.8 } };
            if (angle <= 30)
                return new double[,] { { 0.5, -1.3 }, { 0.5, -2.1 }, { 0.5, -2.6 } };
            if (angle <= 45)
                return new double[,] { { 0.8, -0.9 }, { 0.8, -1.8 }, { 0.8, -2.8 } };
            return new double[,]     { { 0.8, -0.8 }, { 0.8, -1.2 }, { 0.8, -1.6 } };
        }

        /// Parse "5:12" → degrees.  Returns 15° if unparseable.
        private static double ParsePitchToDegrees(string pitch)
        {
            if (string.IsNullOrWhiteSpace(pitch)) return 15.0;
            var parts = pitch.Split(':');
            if (parts.Length == 2
                && double.TryParse(parts[0].Trim(), out double rise)
                && double.TryParse(parts[1].Trim(), out double run)
                && run > 0)
                return Math.Atan(rise / run) * 180.0 / Math.PI;
            return 15.0;
        }

        // ASCE 7-22 Table 26.10-1 — Kz at mean roof height
        private static double CalcKz(double h, string exp)
        {
            switch ((exp ?? "C").ToUpper())
            {
                case "B":
                    if (h <= 15) return 0.57;
                    if (h <= 20) return Lerp(15, 0.57, 20, 0.62, h);
                    if (h <= 25) return Lerp(20, 0.62, 25, 0.66, h);
                    if (h <= 30) return Lerp(25, 0.66, 30, 0.70, h);
                    if (h <= 40) return Lerp(30, 0.70, 40, 0.76, h);
                    return 0.81;
                case "D":
                    if (h <= 15) return 1.03;
                    if (h <= 20) return Lerp(15, 1.03, 20, 1.08, h);
                    if (h <= 25) return Lerp(20, 1.08, 25, 1.12, h);
                    return 1.16;
                default: // C
                    if (h <= 15) return 0.85;
                    if (h <= 20) return Lerp(15, 0.85, 20, 0.90, h);
                    if (h <= 25) return Lerp(20, 0.90, 25, 0.94, h);
                    if (h <= 30) return Lerp(25, 0.94, 30, 0.98, h);
                    return 1.03;
            }
        }

        // ASCE 7-22 Figure 27.5-1 — λ height/exposure adjustment
        private static double CalcLambda(double h, string exp)
        {
            switch ((exp ?? "C").ToUpper())
            {
                case "B":
                    if (h <= 15) return 0.78;
                    if (h <= 20) return Lerp(15, 0.78, 20, 0.82, h);
                    if (h <= 25) return Lerp(20, 0.82, 25, 0.87, h);
                    if (h <= 30) return Lerp(25, 0.87, 30, 1.00, h);
                    if (h <= 40) return Lerp(30, 1.00, 40, 1.05, h);
                    return 1.09;
                case "D":
                    if (h <= 15) return 1.47;
                    if (h <= 20) return Lerp(15, 1.47, 20, 1.55, h);
                    if (h <= 30) return Lerp(20, 1.55, 30, 1.70, h);
                    return 1.83;
                default: // C
                    if (h <= 15) return 1.21;
                    if (h <= 20) return Lerp(15, 1.21, 20, 1.29, h);
                    if (h <= 30) return Lerp(20, 1.29, 30, 1.43, h);
                    return 1.56;
            }
        }

        private static double Lerp(double x0, double y0, double x1, double y1, double x) =>
            y0 + (y1 - y0) * (x - x0) / (x1 - x0);

        // ── Slope description for display ─────────────────────────────────────
        public string SlopeRangeDescription
        {
            get
            {
                double a = SlopeAngleDeg;
                if (a <= 10) return "≤10°";
                if (a <= 20) return $"{a:F0}° (10°-20°)";
                if (a <= 30) return $"{a:F0}° (20°-30°)";
                return $"{a:F0}° (>30°)";
            }
        }
    }
}
