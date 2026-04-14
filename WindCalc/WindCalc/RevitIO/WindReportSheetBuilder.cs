using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using WindCalc.Engine;
using WindCalc.Models;

namespace WindCalc.RevitIO
{
    /// <summary>
    /// Creates a ViewDrafting "DESIGN DATA – Wind Analysis" matching the
    /// Design_Data_Complete.docx reference format.
    ///
    /// Root causes fixed vs. prior version:
    ///   1. TEXT_SIZE was being set on the TextNote instance, which mutated the
    ///      shared TextNoteType and changed every note in the view.
    ///      Fix: create one TextNoteType per unique text size; pass the correct
    ///      TypeId to TextNote.Create.
    ///   2. Text was placed at (x, bot+M) — Revit TextNote origin is the TOP of the
    ///      text, so text flowed below the cell.
    ///      Fix: place at (x, yTop-M) so text descends within the cell.
    ///
    /// Layout at 1:12 scale — all model coordinates in feet (1 ft = 1" on paper):
    ///   Total view: 18" × 11"  →  1.5 ft × 0.9167 ft
    ///   Left panel: 0 – 8.5"  (0 – 0.7083 ft)
    ///   Right panel: 8.5" – 18"  (0.7083 – 1.5 ft)
    /// </summary>
    public class WindReportSheetBuilder
    {
        private readonly Document _doc;

        // ── Unit helpers ──────────────────────────────────────────────────────
        // At 1:12 scale: 1 model foot = 1 paper inch.
        // All layout values below are in paper-inches; In() converts to model-feet.
        private static double In(double inches) => inches / 12.0;

        // ── Layout constants ──────────────────────────────────────────────────
        // All values are paper-inches at 1:12 scale (1 model-foot = 1 paper-inch).
        // Minimum row height = 1/4"  →  RowH = 0.25"
        // Minimum text size  = 1/8"  →  SmPt = 0.125"
        // ViewH set to 16" so all rows always fit without clipping.
        private static readonly double ViewW  = In(18.0);
        private static readonly double ViewH  = In(16.0);
        private static readonly double Mg     = In(0.08);    // cell margin

        private static readonly double PanelX = In(8.5);     // left/right panel divider

        // Left panel column stops
        private static readonly double L0  = In(0.0);
        private static readonly double L1  = In(5.5);        // label/value split
        private static readonly double L2  = PanelX;

        // Right panel column stops
        private static readonly double R0  = PanelX;
        private static readonly double R2  = ViewW;
        private static readonly double RZW = In(3.8);        // zone label column width (right panel)

        // Row heights — minimum data row is 1/4"
        private static readonly double TitleH  = In(0.42);
        private static readonly double SecH    = In(0.30);
        private static readonly double SubH    = In(0.26);
        private static readonly double RowH    = In(0.25);   // minimum 1/4"

        // Text vertical offset: places text origin (TOP of text) so text is
        // vertically centered inside a 1/4" row with 1/8" text height.
        // = (0.25" - 0.125") / 2 = 0.0625" from cell top
        private static readonly double TxtOff  = In(0.0625);

        // Text sizes in paper inches — minimum 1/8" = 0.125"
        // Actual type names come from the project (Chief Blueprint font).
        private const double TitlePt  = 3.0 / 16.0;  // 3/16" = 18pt
        private const double HdrPt    = 5.0 / 32.0;  // 5/32" = 14pt
        private const double BodyPt   = 1.0 / 8.0;   // 1/8"  = 12pt
        private const double SmPt     = 1.0 / 8.0;   // 1/8"  = 12pt (minimum)

        // ── Colors ────────────────────────────────────────────────────────────
        private static Color C(byte r, byte g, byte b) => new Color(r, g, b);
        private static readonly Color DarkBlue  = C( 26,  82, 118);
        private static readonly Color MidBlue   = C( 93, 173, 226);
        private static readonly Color DkGreen   = C( 30, 100,  40);
        private static readonly Color DkRed     = C(183,  28,  28);
        private static readonly Color DkOrange  = C(200,  70,   0);
        private static readonly Color LtGreen   = C(198, 239, 206);
        private static readonly Color LtRed     = C(255, 199, 206);
        private static readonly Color LtBlue    = C(189, 215, 238);
        private static readonly Color LtYellow  = C(255, 249, 196);
        private static readonly Color LtGray    = C(242, 242, 242);
        private static readonly Color White     = C(255, 255, 255);
        private static readonly Color CodeRed   = C(192,   0,   0);
        private static readonly Color Black     = C(  0,   0,   0);
        private static readonly Color MidGray   = C(180, 180, 180);
        private static readonly Color NavyGray  = C( 68,  84, 106);

        // ── Revit element caches ──────────────────────────────────────────────
        private View                                  _view;
        private FillPatternElement                    _solidFill;
        private readonly Dictionary<uint, ElementId>  _fillTypeCache = new Dictionary<uint, ElementId>();
        private ElementId                             _ttTitle, _ttHdr, _ttBody, _ttSm;

        public WindReportSheetBuilder(Document doc) => _doc = doc;

        // ═══════════════════════════════════════════════════════════════════════
        //  Entry point
        // ═══════════════════════════════════════════════════════════════════════

        public View BuildOrUpdateSheet(WindCalcResult result)
        {
            // Delete any existing DESIGN DATA drafting view
            foreach (var v in new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewDrafting)).Cast<ViewDrafting>()
                .Where(v => v.Name.StartsWith("DESIGN DATA")).ToList())
                _doc.Delete(v.Id);

            var vft = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .First(t => t.ViewFamily == ViewFamily.Drafting);

            _view      = ViewDrafting.Create(_doc, vft.Id);
            _view.Name = "DESIGN DATA \u2013 Wind Analysis";
            _view.Scale = 1;   // 12" = 1'-0" (full scale)

            // Use the project's Chief Blueprint text types.
            // Each helper tries the exact name first, then creates a transparent
            // duplicate if only the non-TRANS variant exists, then falls back to
            // GetOrCreateTextType if neither exists.
            _ttTitle = FindTextType("18pt - 3/16\" - Chief Blueprint TRANS",
                                    "18pt - 3/16\" - Chief Blueprint",  TitlePt);
            _ttHdr   = FindTextType("14pt - 5/32\" - Chief Blueprint TRANS",
                                    "14pt - 5/32\" - Chief Blueprint",  HdrPt);
            _ttBody  = FindTextType("12pt - 1/8\" - Chief Blueprint TRANS",
                                    "12pt - 1/8\" - Chief Blueprint",   BodyPt);
            _ttSm    = _ttBody;   // same type, same minimum size

            _solidFill = GetSolidFillPattern();

            double vasd = result.Site.AsceVasdRcII > 0
                ? result.Site.AsceVasdRcII
                : result.AppliedVult / Math.Sqrt(1.6);

            var calc = new WindPressureCalculator(
                result.AppliedVult, vasd,
                result.ExposureCategory,
                result.Building.MeanRoofHeightFt,
                result.Building.RoofType,
                result.Building.RoofPitch);

            DrawAll(result, calc, vasd);
            return _view;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Master layout
        // ═══════════════════════════════════════════════════════════════════════

        private void DrawAll(WindCalcResult r, WindPressureCalculator calc, double vasd)
        {
            double y = ViewH;

            // ── Title bar ─────────────────────────────────────────────────────
            double titleBot = y - TitleH;
            FillRect(L0, titleBot, R2, y, DarkBlue);
            BorderRect(L0, titleBot, R2, y);
            TxtC(L0, R2, titleBot, y, "DESIGN DATA", _ttTitle, White);
            y = titleBot;

            // Vertical panel divider
            DrawLine(PanelX, 0, PanelX, y);
            // Outer border
            BorderRect(L0, 0, R2, y);

            // Panels drawn independently — each returns how far down it reached
            DrawLeftPanel(r, calc, y);
            DrawRightPanel(r, calc, vasd, y);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  LEFT PANEL
        // ═══════════════════════════════════════════════════════════════════════

        private void DrawLeftPanel(WindCalcResult r, WindPressureCalculator calc, double yTop)
        {
            double y = yTop;
            string exp = r.ExposureCategory;
            string rc  = r.RiskCategory;

            // ── Building Code Information ─────────────────────────────────────
            y = SecHeader("BUILDING CODE INFORMATION", L0, L2, y);
            foreach (string code in BuildCodeList(r.CodeEdition))
                y = DataRow(code, "", L0, L2, L2, y, CodeRed, Black);

            // ── DATUM ─────────────────────────────────────────────────────────
            y = SecHeader("DATUM", L0, L2, y);

            bool windborne = r.WindborneDebrisArea;
            bool env160    = r.Envelope160MphRequired;

            y = DataRow("WINDBORNE DEBRIS AREA",
                windborne ? "YES" : "NO", L0, L1, L2, y,
                windborne ? DkRed : MidGray, windborne ? DkRed : Black);

            y = DataRow(r.CodeEdition == "9th"
                    ? "IMPACT RESISTANCE (SB 1218 / HB 911) \u2013 160 MPH ENVELOPE"
                    : "IMPACT-RESISTANT ENVELOPE REQUIRED",
                env160 ? "YES" : "NO", L0, L1, L2, y,
                env160 ? DkOrange : MidGray, env160 ? DkOrange : Black);

            y = DataRow("V\u1d41\u2097\u209c  ULTIMATE DESIGN WIND SPEED",
                $"{r.AppliedVult:F0} MPH", L0, L1, L2, y, LtBlue, Black);
            y = DataRow("V\u1d43\u02e2\u1d48  NOMINAL DESIGN WIND SPEED",
                $"{vasdFmt(r, r.Site.AsceVasdRcII)} MPH", L0, L1, L2, y, LtBlue, Black);

            y = DataRow("RISK CATEGORY",                          rc,         L0, L1, L2, y, LtGray, Black);
            y = DataRow("SURFACE ROUGHNESS",                      exp,        L0, L1, L2, y, LtGray, Black);
            y = DataRow("EXPOSURE CATEGORY",                      exp,        L0, L1, L2, y, LtGray, Black);
            y = DataRow("DESIGN (ENCLOSURE)",                     "ENCLOSED", L0, L1, L2, y, LtGray, Black);
            y = DataRow("INTERNAL PRESSURE COEFFICIENT (\u00b1)", "0.18",     L0, L1, L2, y, LtGray, Black);
            y = DataRow("BUILDING HEIGHT (MAXIMUM)",
                $"{r.Building.RidgeHeightFt:F1} FT",  L0, L1, L2, y, LtGray, Black);
            y = DataRow("EAVE HEIGHT",
                $"{r.Building.EaveHeightFt:F1} FT",   L0, L1, L2, y, LtGray, Black);
            y = DataRow("MEAN ROOF HEIGHT",
                $"{r.Building.MeanRoofHeightFt:F1} FT", L0, L1, L2, y, LtGray, Black);
            y = DataRow("HEIGHT & EXPOSURE ADJ. COEFF. (K\u03bb)",
                calc.Lambda.ToString("F3"),            L0, L1, L2, y, LtGray, Black);
            y = DataRow("VELOCITY PRESSURE EXPOSURE COEFF. (Kz)",
                calc.Kz.ToString("F2"),                L0, L1, L2, y, LtGray, Black);
            y = DataRow("TOPOGRAPHIC FACTOR (Kzt)",   "1.00",     L0, L1, L2, y, LtGray, Black);
            y = DataRow("GROUND ELEVATION FACTOR (Ke)", "1.00",   L0, L1, L2, y, LtGray, Black);
            y = DataRow("DIRECTIONALITY FACTOR (Kd)", "0.85",     L0, L1, L2, y, LtGray, Black);
            y = DataRow("VELOCITY PRESSURE (qh)",
                $"{calc.Qh:F2} PSF",                  L0, L1, L2, y, LtGray, Black);
            y = DataRow("GROUND ELEVATION",
                r.Site.ElevationFt > 0 ? $"{r.Site.ElevationFt:F1} FT NAVD88" : "—",
                L0, L1, L2, y, LtGray, Black);
            y = DataRow("FLOOD ZONE",
                string.IsNullOrWhiteSpace(r.FloodZone) ? "—" : r.FloodZone,
                L0, L1, L2, y,
                (r.FloodZone ?? "").StartsWith("X", StringComparison.OrdinalIgnoreCase) ? LtGray : LtRed,
                Black);

            // ── LOADING ───────────────────────────────────────────────────────
            y = SecHeader("LOADING", L0, L2, y);
            y = DataRow("LIVE LOAD (ROOF)",    "20 PSF",        L0, L1, L2, y, LtGray, Black);
            y = DataRow("DEAD LOAD (ROOF)",    "15 PSF",        L0, L1, L2, y, LtGray, Black);
            y = DataRow("LIVE LOAD (FLOOR)",   "40 PSF",        L0, L1, L2, y, LtGray, Black);
            y = DataRow("CONCRETE",            "3000 PSI",      L0, L1, L2, y, LtGray, Black);
            y = DataRow("LUMBER",
                r.CodeEdition == "9th" ? "SP #2 / NDS-2024" : "SP #2 / NDS-2018",
                L0, L1, L2, y, LtGray, Black);
            y = DataRow("SOIL BEARING CAPACITY", "1500 PSF ASSUMED", L0, L1, L2, y, LtGray, Black);

            // ── TYPE OF CONSTRUCTION ──────────────────────────────────────────
            y = SecHeader("TYPE OF CONSTRUCTION", L0, L2, y);
            y = DataRow("EXISTING:",   "TYPE V-B",              L0, L1, L2, y, LtGray, Black);
            y = DataRow("PROPOSED:",   "TYPE V-B",              L0, L1, L2, y, LtGray, Black);
            y = DataRow("TYPE OF WORK", "NEW CONSTRUCTION",     L0, L1, L2, y, LtGray, Black);
            y = DataRow("OCCUPANCY",   "R-2 RESIDENTIAL",       L0, L1, L2, y, LtGray, Black);

            // ── PE EXEMPTIONS ─────────────────────────────────────────────────
            {
                y -= In(0.04);
                y = SecHeader("PROFESSIONAL ENGINEERING EXEMPTIONS", L0, L2, y);
                const string pe =
                    "PER CHAPTER 471, FLORIDA STATUTES \u00a7471.003(2)(f): Any electrical, plumbing, " +
                    "air-conditioning, or mechanical contractor whose practice includes the design and fabrication " +
                    "of systems they install under Chapter 489 when: (1) electrical/plumbing system value \u2264 $125,000; " +
                    "and (2a) \u2264 600A (240V) residential or \u2264 800A commercial, or (2b) plumbing < 250 fixture units, " +
                    "or (2c) HVAC \u2264 15-ton/system capacity, or project accommodates \u2264 100 persons.";
                TxtWrap(L0 + Mg, y - TxtOff, L2 - Mg, pe, _ttSm, Black);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  RIGHT PANEL
        // ═══════════════════════════════════════════════════════════════════════

        private void DrawRightPanel(WindCalcResult r, WindPressureCalculator calc, double vasd, double yTop)
        {
            double y = yTop;

            // ── Header ────────────────────────────────────────────────────────
            y = SecHeader("DESIGN WIND LOADS \u2013 DOORS, WINDOWS, COMPONENTS AND CLADDING", R0, R2, y);

            // Roof info row
            double cw  = (R2 - R0) / 3.0;
            double rb  = y - SubH;
            FillRect(R0, rb, R2, y, LtGray);
            BorderRect(R0,      rb, R0 + cw,     y);
            BorderRect(R0 + cw, rb, R0 + cw * 2, y);
            BorderRect(R0 + cw * 2, rb, R2,      y);
            TxtL(R0 + Mg,           y - Mg, $"ROOF SLOPE  {calc.SlopeRangeDescription}",      _ttSm, Black);
            TxtL(R0 + cw + Mg,      y - Mg, $"PITCH  {r.Building.RoofPitch}",                 _ttSm, Black);
            TxtL(R0 + cw * 2 + Mg, y - TxtOff, $"TYPE \u2013 {(r.Building.RoofType ?? "").ToUpper()}", _ttSm, Black);
            y = rb;

            // Zone table columns: zone label | POS | NEG
            double zX = R0;
            double pX = R0 + RZW;
            double nX = R0 + RZW + (R2 - R0 - RZW) / 2.0;

            // Column headers
            double hb = y - SubH;
            FillRect(zX, hb, pX, y, LtGray);
            FillRect(pX, hb, nX, y, DkGreen);
            FillRect(nX, hb, R2, y, DkRed);
            BorderRect(zX, hb, pX, y);
            BorderRect(pX, hb, nX, y);
            BorderRect(nX, hb, R2, y);
            TxtL(zX + Mg, y - TxtOff, "Vasd",  _ttSm, Black);
            TxtC(pX, nX, hb, y, "POS",     _ttSm, White);
            TxtC(nX, R2, hb, y, "NEG",     _ttSm, White);
            y = hb;

            // Roof zones
            var roofZ = calc.GetRoofZones();
            for (int i = 0; i < 3; i++)
                y = ZoneRow($"ZONE  {i + 1}", roofZ[i], zX, pX, nX, y);

            // Overhang
            y = SubHeader("OVERHANG", R0, R2, y);
            var ohZ = calc.GetOverhangZones();
            for (int i = 0; i < 3; i++)
                y = ZoneRow($"ZONE  OH{i + 1}", ohZ[i], zX, pX, nX, y);

            // Wall
            y = SubHeader("WALL", R0, R2, y);
            var wallZ = calc.GetWallZones();
            y = ZoneRow("ZONE  4", wallZ[0], zX, pX, nX, y);
            y = ZoneRow("ZONE  5", wallZ[1], zX, pX, nX, y);

            // Garage doors
            y = SubHeader("GARAGE DOOR", R0, R2, y);
            y = ZoneRow("(9X7)",  calc.GetGarageDoorPressures(false), zX, pX, nX, y);
            y = ZoneRow("(16X7)", calc.GetGarageDoorPressures(true),  zX, pX, nX, y);
            y = ZoneRow("(18X7)", calc.GetGarageDoorPressures(true),  zX, pX, nX, y);

            // Entry doors / windows / sliding doors
            y = SubHeader("ENTRY DOORS / WINDOWS / SLIDING DOORS", R0, R2, y);
            y = ZoneRow("ENTRY DOOR (3070)",    wallZ[0], zX, pX, nX, y);
            y = ZoneRow("SLIDING GLASS (6068)", wallZ[1], zX, pX, nX, y);
            y = ZoneRow("WINDOW (per schedule)", wallZ[1], zX, pX, nX, y);

            // ── Florida Product Approvals ──────────────────────────────────────
            y -= In(0.02);
            y = SecHeader("FLORIDA PRODUCT APPROVALS", R0, R2, y);
            y = PaRow("WINDOWS: FL#  ___________",  "ENTRY DOORS: FL#  ___________",  R0, R2, y);
            y = PaRow("SLIDING DOORS: FL#  _______", "GARAGE DOORS: FL#  ___________", R0, R2, y);
            y = PaRow("SKYLIGHTS: FL#  __________",  "ROOF COVERING: FL#  __________", R0, R2, y);
            y = PaRow("SHUTTERS: FL#  ___________  (if applicable)", "", R0, R2, y);

            // ── C&C Wind Zone Reference ───────────────────────────────────────
            {
                y -= In(0.02);
                y = SecHeader("C&C WIND ZONE REFERENCE \u2013 ASCE 7-22", R0, R2, y);

                // Zone dimension formula
                y = DataRowFull("a = max [ min(0.1B, 0.1L, 0.4h),  min(0.04B, 0.04L),  3 ft ]", R0, R2, y, NavyGray, White);

                // Zone description table
                double zDivW = (R2 - R0) * 0.40;
                double zDimW = (R2 - R0) * 0.20;
                double zDescW = R2 - R0 - zDivW - zDimW;
                double zD2 = R0 + zDivW, zD3 = zD2 + zDescW;

                // Header row
                double hbZ = y - SubH;
                FillRect(R0, hbZ, R2, y, MidBlue);
                BorderRect(R0, hbZ, zD2, y);
                BorderRect(zD2, hbZ, zD3, y);
                BorderRect(zD3, hbZ, R2, y);
                TxtL(R0 + Mg, y - TxtOff, "Zone",        _ttSm, White);
                TxtL(zD2 + Mg, y - TxtOff, "Description", _ttSm, White);
                TxtL(zD3 + Mg, y - TxtOff, "Dimension",   _ttSm, White);
                y = hbZ;

                y = ZoneDescRow("Zone 3",   "Corner \u2013 L-shaped at each roof corner",       "a",           R0, zD2, zD3, y);
                y = ZoneDescRow("Zone 2",   "Edge/Perimeter \u2013 strip along each roof edge", "a",           R0, zD2, zD3, y);
                y = ZoneDescRow("Zone 1",   "Interior/Field \u2013 remaining roof area",        "Remainder",   R0, zD2, zD3, y);
                y = ZoneDescRow("Zone OH1-3","Overhang soffit \u2013 same zones as above",      "Same a",      R0, zD2, zD3, y);
                y = ZoneDescRow("Zone 5",   "Corner wall \u2013 width = a from each corner",    "a",           R0, zD2, zD3, y);
                y = ZoneDescRow("Zone 4",   "Interior wall \u2013 all wall beyond a",           "Remainder",   R0, zD2, zD3, y);
            }

            // ── ASCE 7-22 vs 7-16 Key Changes ────────────────────────────────
            {
                y -= In(0.02);
                y = SecHeader("ASCE 7-22 vs. 7-16 \u2013 KEY C&C CHANGES", R0, R2, y);
                double cW = (R2 - R0) / 3.0;

                // Header
                double hbA = y - SubH;
                FillRect(R0, hbA, R2, y, NavyGray);
                BorderRect(R0, hbA, R0 + cW, y);
                BorderRect(R0 + cW, hbA, R0 + cW * 2, y);
                BorderRect(R0 + cW * 2, hbA, R2, y);
                TxtL(R0 + Mg, y - TxtOff, "Item",            _ttSm, White);
                TxtL(R0 + cW + Mg, y - TxtOff, "ASCE 7-16", _ttSm, White);
                TxtL(R0 + cW * 2 + Mg, y - TxtOff, "ASCE 7-22 (FBC 9th)", _ttSm, White);
                y = hbA;

                y = CompRow("Flat roof zones",   "4 zones: 1\u2019, 1, 2, 3",  "3 zones: 1, 2, 3",     R0, cW, y);
                y = CompRow("Kd location",        "In qh equation",              "Moved to C&C p eq.",   R0, cW, y);
                y = CompRow("qh equation",         "0.00256\u00b7Kz\u00b7Kzt\u00b7Kd\u00b7Ke\u00b7V\u00b2", "0.00256\u00b7Kz\u00b7Kzt\u00b7Ke\u00b7V\u00b2", R0, cW, y);
                y = CompRow("C&C pressure",        "p = qh[(GCp)\u2212(GCpi)]", "p = qh\u00b7Kd[(GCp)\u2212(GCpi)]", R0, cW, y);
                y = CompRow("Gable roof zones",    "Zone 2r, 2e distinct",       "Unified Zone 2",       R0, cW, y);
                y = CompRow("Min design pressure", "\u00b116 psf",               "\u00b116 psf (unchanged)", R0, cW, y);

                // Note row
                {
                    double nb = y - SubH;
                    FillRect(R0, nb, R2, y, LtYellow);
                    BorderRect(R0, nb, R2, y);
                    TxtL(R0 + Mg, y - TxtOff,
                        "ASD pressures = Ultimate \u00d7 0.6.   EWA for roofing: use 10 ft\u00b2 min. unless noted.",
                        _ttSm, Black);
                    y = nb;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Row primitives
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>Section header bar — dark blue, white centered text.</summary>
        private double SecHeader(string text, double x0, double x1, double yTop)
        {
            double bot = yTop - SecH;
            FillRect(x0, bot, x1, yTop, DarkBlue);
            BorderRect(x0, bot, x1, yTop);
            TxtC(x0, x1, bot, yTop, text, _ttHdr, White);
            return bot;
        }

        /// <summary>Sub-header bar — mid blue, white left-aligned text.</summary>
        private double SubHeader(string text, double x0, double x1, double yTop)
        {
            double bot = yTop - SubH;
            FillRect(x0, bot, x1, yTop, MidBlue);
            BorderRect(x0, bot, x1, yTop);
            TxtL(x0 + Mg, yTop - TxtOff, text, _ttSm, White);
            return bot;
        }

        /// <summary>Two-column label/value data row.</summary>
        private double DataRow(string label, string value,
            double x0, double xDiv, double x1,
            double yTop, Color bg, Color valColor)
        {
            double bot = yTop - RowH;
            FillRect(x0, bot, x1, yTop, bg);
            BorderRect(x0, bot, xDiv, yTop);
            if (xDiv < x1) BorderRect(xDiv, bot, x1, yTop);
            TxtL(x0 + Mg,   yTop - Mg, label, _ttSm, Black);
            if (!string.IsNullOrEmpty(value))
                TxtL(xDiv + Mg, yTop - TxtOff, value, _ttSm, valColor);
            return bot;
        }

        /// <summary>Full-width single-column colored data row.</summary>
        private double DataRowFull(string text, double x0, double x1, double yTop, Color bg, Color fg)
        {
            double bot = yTop - RowH;
            FillRect(x0, bot, x1, yTop, bg);
            BorderRect(x0, bot, x1, yTop);
            TxtL(x0 + Mg, yTop - TxtOff, text, _ttSm, fg);
            return bot;
        }

        /// <summary>Product approval two-column row.</summary>
        private double PaRow(string left, string right, double x0, double x1, double yTop)
        {
            double bot = yTop - RowH;
            double xMid = (x0 + x1) / 2.0;
            FillRect(x0, bot, x1, yTop, LtGray);
            BorderRect(x0, bot, string.IsNullOrEmpty(right) ? x1 : xMid, yTop);
            if (!string.IsNullOrEmpty(right)) BorderRect(xMid, bot, x1, yTop);
            TxtL(x0 + Mg, yTop - TxtOff, left, _ttSm, Black);
            if (!string.IsNullOrEmpty(right))
                TxtL(xMid + Mg, yTop - TxtOff, right, _ttSm, Black);
            return bot;
        }

        /// <summary>Zone description row (3 columns).</summary>
        private double ZoneDescRow(string zone, string desc, string dim,
            double x0, double xD2, double xD3, double yTop)
        {
            double bot = yTop - RowH;
            FillRect(x0, bot, R2, yTop, LtGray);
            BorderRect(x0, bot, xD2, yTop);
            BorderRect(xD2, bot, xD3, yTop);
            BorderRect(xD3, bot, R2, yTop);
            TxtL(x0  + Mg, yTop - TxtOff, zone, _ttSm, Black);
            TxtL(xD2 + Mg, yTop - TxtOff, desc, _ttSm, Black);
            TxtL(xD3 + Mg, yTop - TxtOff, dim,  _ttSm, Black);
            return bot;
        }

        /// <summary>ASCE 7-22 vs 7-16 comparison row (3 columns).</summary>
        private double CompRow(string item, string old, string newVal,
            double x0, double colW, double yTop)
        {
            double bot = yTop - RowH;
            FillRect(x0, bot, R2, yTop, LtGray);
            BorderRect(x0,        bot, x0 + colW,     yTop);
            BorderRect(x0 + colW, bot, x0 + colW * 2, yTop);
            BorderRect(x0 + colW * 2, bot, R2,         yTop);
            TxtL(x0          + Mg, yTop - TxtOff, item,   _ttSm, Black);
            TxtL(x0 + colW   + Mg, yTop - TxtOff, old,    _ttSm, Black);
            TxtL(x0 + colW*2 + Mg, yTop - TxtOff, newVal, _ttSm, Black);
            return bot;
        }

        /// <summary>Pressure zone row — zone name, positive (green), negative (red).</summary>
        private double ZoneRow(string name, WindPressureCalculator.ZonePressures z,
            double zX, double pX, double nX, double yTop)
        {
            double bot = yTop - RowH;
            FillRect(zX, bot, pX, yTop, LtGray);
            FillRect(pX, bot, nX, yTop, LtGreen);
            FillRect(nX, bot, R2, yTop, LtRed);
            BorderRect(zX, bot, pX, yTop);
            BorderRect(pX, bot, nX, yTop);
            BorderRect(nX, bot, R2, yTop);
            TxtL(zX + Mg, yTop - TxtOff, name,                      _ttSm, Black);
            TxtC(pX, nX,  bot, yTop, $"+{z.Pos:F2}",            _ttSm, DkGreen);
            TxtC(nX, R2,  bot, yTop, z.Neg.ToString("F2"),       _ttSm, DkRed);
            return bot;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Text primitives  (FIX: origin is TOP of text — place at yTop-Mg, not bot+Mg)
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>Left-aligned text. Origin placed at (x, yTop-Mg) — text descends into cell.</summary>
        private void TxtL(double x, double yOrigin, string text, ElementId typeId, Color color)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                var opts = new TextNoteOptions(typeId)
                    { HorizontalAlignment = HorizontalTextAlignment.Left };
                var tn = TextNote.Create(_doc, _view.Id, new XYZ(x, yOrigin, 0), text, opts);
                SetTextColor(tn, color);
            }
            catch { }
        }

        /// <summary>Center-aligned text in a cell defined by (x0..x1, bot..yTop).</summary>
        private void TxtC(double x0, double x1, double bot, double yTop,
            string text, ElementId typeId, Color color)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                double cx = (x0 + x1) / 2.0;
                // Origin = TOP of text. Place TxtOff below yTop for vertical centering.
                double cy = yTop - TxtOff;
                var opts = new TextNoteOptions(typeId)
                    { HorizontalAlignment = HorizontalTextAlignment.Center };
                var tn = TextNote.Create(_doc, _view.Id, new XYZ(cx, cy, 0), text, opts);
                SetTextColor(tn, color);
            }
            catch { }
        }

        /// <summary>Word-wrapped text block; origin at top-left.</summary>
        private void TxtWrap(double x, double yOrigin, double xRight, string text,
            ElementId typeId, Color color)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                double width = xRight - x;
                var opts = new TextNoteOptions(typeId)
                    { HorizontalAlignment = HorizontalTextAlignment.Left };
                var tn = TextNote.Create(_doc, _view.Id, new XYZ(x, yOrigin, 0), width, text, opts);
                SetTextColor(tn, color);
            }
            catch { }
        }

        private void SetTextColor(TextNote tn, Color color)
        {
            try
            {
                var ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(color);
                _view.SetElementOverrides(tn.Id, ogs);
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Fill / Line primitives
        // ═══════════════════════════════════════════════════════════════════════

        private void FillRect(double x0, double y0, double x1, double y1, Color color)
        {
            if (_solidFill == null) return;
            uint key = (uint)((color.Red << 16) | (color.Green << 8) | color.Blue);
            if (!_fillTypeCache.TryGetValue(key, out ElementId frtId))
                frtId = MakeFillType(color, key);
            if (frtId == null || frtId == ElementId.InvalidElementId) return;
            try
            {
                var loop = new CurveLoop();
                loop.Append(Autodesk.Revit.DB.Line.CreateBound(P(x0, y0), P(x1, y0)));
                loop.Append(Autodesk.Revit.DB.Line.CreateBound(P(x1, y0), P(x1, y1)));
                loop.Append(Autodesk.Revit.DB.Line.CreateBound(P(x1, y1), P(x0, y1)));
                loop.Append(Autodesk.Revit.DB.Line.CreateBound(P(x0, y1), P(x0, y0)));
                FilledRegion.Create(_doc, frtId, _view.Id, new List<CurveLoop> { loop });
            }
            catch { }
        }

        private void BorderRect(double x0, double y0, double x1, double y1)
        {
            DrawLine(x0, y0, x1, y0);
            DrawLine(x1, y0, x1, y1);
            DrawLine(x1, y1, x0, y1);
            DrawLine(x0, y1, x0, y0);
        }

        private void DrawLine(double x0, double y0, double x1, double y1)
        {
            try { _doc.Create.NewDetailCurve(_view,
                Autodesk.Revit.DB.Line.CreateBound(P(x0, y0), P(x1, y1))); }
            catch { }
        }

        private static XYZ P(double x, double y) => new XYZ(x, y, 0);

        // ═══════════════════════════════════════════════════════════════════════
        //  Resource setup
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Gets or creates a TextNoteType with a specific paper-size text height.
        /// FIX: TEXT_SIZE is set on the TYPE (here), not on each TextNote instance.
        /// </summary>
        private ElementId GetOrCreateTextType(string typeName, double sizeInches)
        {
            double sizeInFeet = sizeInches / 12.0;

            var existing = new FilteredElementCollector(_doc)
                .OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
                .FirstOrDefault(t => t.Name == typeName);
            if (existing != null) return existing.Id;

            // Duplicate the smallest existing type as our template
            var source = new FilteredElementCollector(_doc)
                .OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
                .OrderBy(t => t.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0)
                .First();

            var nt = source.Duplicate(typeName) as TextNoteType;
            nt.get_Parameter(BuiltInParameter.TEXT_SIZE).Set(sizeInFeet);
            return nt.Id;
        }

        /// <summary>
        /// Looks up a TextNoteType by preferred name (e.g. "12pt - 1/8" - Chief Blueprint TRANS").
        /// If not found, tries the alternate name and duplicates it as a transparent variant.
        /// Falls back to GetOrCreateTextType if neither exists in the project.
        /// </summary>
        private ElementId FindTextType(string preferredName, string alternateName, double fallbackSizeInches)
        {
            var all = new FilteredElementCollector(_doc)
                .OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();

            // 1. Exact preferred name
            var t = all.FirstOrDefault(x => x.Name == preferredName);
            if (t != null) return t.Id;

            // 2. Alternate name — duplicate it with transparent background
            var src = all.FirstOrDefault(x => x.Name == alternateName);
            if (src != null)
            {
                string transName = preferredName;
                var existing = all.FirstOrDefault(x => x.Name == transName);
                if (existing != null) return existing.Id;

                var dup = src.Duplicate(transName) as TextNoteType;
                // 0 = Transparent, 1 = Opaque
                dup.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)?.Set(0);
                return dup.Id;
            }

            // 3. Fall back: create a type from scratch with the right size
            return GetOrCreateTextType("WC_" + preferredName.Split('-')[0].Trim(), fallbackSizeInches);
        }

        private ElementId MakeFillType(Color color, uint key)
        {
            try
            {
                string typeName = $"WC_{key:X6}";
                var existing = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                    .FirstOrDefault(t => t.Name == typeName);

                var template = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().First();

                FilledRegionType frt = existing ?? template.Duplicate(typeName) as FilledRegionType;
                frt.ForegroundPatternId    = _solidFill.Id;
                frt.ForegroundPatternColor = color;
                frt.BackgroundPatternId    = _solidFill.Id;
                frt.BackgroundPatternColor = color;
                _fillTypeCache[key] = frt.Id;
                return frt.Id;
            }
            catch
            {
                _fillTypeCache[key] = ElementId.InvalidElementId;
                return ElementId.InvalidElementId;
            }
        }

        private FillPatternElement GetSolidFillPattern() =>
            new FilteredElementCollector(_doc)
                .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill);

        // ═══════════════════════════════════════════════════════════════════════
        //  Static helpers
        // ═══════════════════════════════════════════════════════════════════════

        private static string vasdFmt(WindCalcResult r, double vasdRcII)
        {
            double v = vasdRcII > 0 ? vasdRcII : r.AppliedVult / Math.Sqrt(1.6);
            return v.ToString("F0");
        }

        private static string[] BuildCodeList(string edition)
        {
            if (edition == "9th")
                return new[]
                {
                    "2026 FLORIDA RESIDENTIAL BUILDING CODE - (9TH EDITION)",
                    "2026 FLORIDA BUILDING CODE - (9TH EDITION)",
                    "2026 FLORIDA PLUMBING CODE",
                    "2026 FLORIDA MECHANICAL CODE",
                    "2026 FLORIDA FUEL GAS CODE",
                    "2026 FLORIDA FIRE PREVENTION CODE",
                    "2026 FLORIDA ACCESSIBILITY CODE",
                    "2026 FLORIDA ENERGY CONSERVATION CODE",
                    "NATIONAL ELECTRIC CODE 2023 (NFPA 70)",
                    "ASCE 7-22",
                    "NDS-2024",
                };
            return new[]
            {
                "2023 FLORIDA RESIDENTIAL BUILDING CODE - (8TH EDITION)",
                "2023 FLORIDA BUILDING CODE - (8TH EDITION)",
                "2023 FLORIDA PLUMBING CODE",
                "2023 FLORIDA MECHANICAL CODE",
                "2023 FLORIDA FUEL GAS CODE",
                "2023 FLORIDA FIRE PREVENTION CODE",
                "2023 FLORIDA ACCESSIBILITY CODE",
                "2023 FLORIDA ENERGY CONSERVATION CODE",
                "NATIONAL ELECTRIC CODE 2020 (NFPA 70)",
                "ASCE 7-22",
            };
        }
    }
}
