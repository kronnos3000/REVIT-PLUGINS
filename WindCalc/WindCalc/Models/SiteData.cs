namespace WindCalc.Models
{
    /// <summary>
    /// Site-specific data fetched from external services.
    /// </summary>
    public class SiteData
    {
        // ── Address / Location ────────────────────────────────────────────────
        public string InputAddress      { get; set; } = "";
        public string MatchedAddress    { get; set; } = "";
        public string County            { get; set; } = "";
        public double Latitude          { get; set; }
        public double Longitude         { get; set; }
        public bool   GeocodingSuccess  { get; set; }
        public string GeocodingError    { get; set; } = "";

        // ── Wind Speed (ASCE 7-22) ────────────────────────────────────────────
        public double AsceVultRcI       { get; set; }   // Risk Category I, mph
        public double AsceVultRcII      { get; set; }   // Risk Category II, mph
        public double AsceVultRcIII     { get; set; }   // Risk Category III, mph
        public double AsceVultRcIV      { get; set; }   // Risk Category IV, mph
        public double AsceVasdRcII      { get; set; }   // Vasd for RC II, mph
        public bool   WindSpeedSuccess   { get; set; }
        public bool   AsceApiKey_Missing { get; set; }
        public string WindSpeedError    { get; set; } = "";
        /// <summary>"FGDL local shapefiles" or "ASCE Hazard Tool API" — shown in UI confidence indicator.</summary>
        public string WindSpeedSource   { get; set; } = "";

        // ── Applied Wind Speed (after firm minimum) ───────────────────────────
        public double AppliedVult       { get; set; }   // max(ASCE RC II, firm minimum)
        public bool   FirmMinOverrideActive { get; set; }

        // ── Elevation ─────────────────────────────────────────────────────────
        public double ElevationFt       { get; set; }   // NAVD88
        public bool   ElevationSuccess  { get; set; }
        public string ElevationError    { get; set; } = "";

        // ── Flood Zone ────────────────────────────────────────────────────────
        public string FloodZone         { get; set; } = "";  // e.g. "AE", "X", "VE"
        public double FloodBfeFt        { get; set; }        // Base Flood Elevation, ft; NaN if N/A
        public bool   FloodZoneSuccess  { get; set; }
        public string FloodZoneError    { get; set; } = "";

        // ── Exposure Category ─────────────────────────────────────────────────
        public string ExposureCategory  { get; set; } = "C";  // B, C, or D
        public bool   ExposureAutoDetected { get; set; }
        public string ExposureError     { get; set; } = "";

        // ── Risk Category (user-selected, default II) ─────────────────────────
        public string RiskCategory      { get; set; } = "II";

        // ── Parcel Data (Pinellas / county PA) ───────────────────────────────
        public string ParcelId          { get; set; } = "";
        public int    YearBuilt         { get; set; }
        public string ConstructionClass { get; set; } = "";
        public bool   ParcelSuccess     { get; set; }
        /// <summary>Human-readable source for parcel data (for UI tooltip).</summary>
        public string ParcelSource      { get; set; } = "";

        // ── Extended parcel data (from PCPAO CSV — Pinellas only) ─────────────
        /// <summary>Property owner name from county records.</summary>
        public string ParcelOwner          { get; set; } = "";
        /// <summary>Legal description of the parcel.</summary>
        public string LegalDescription     { get; set; } = "";
        /// <summary>Owner mailing address from county records.</summary>
        public string OwnerMailingAddress  { get; set; } = "";

        // ── Derived flags (computed after fetch + user inputs) ────────────────
        /// <summary>Engineer-confirmed: site is within 1 mile of mean high-water line.</summary>
        public bool CoastalProximity        { get; set; }
        /// <summary>True when windborne debris region thresholds are met.
        /// General: Vult ≥ 140 mph.  Coastal (within 1 mi): Vult ≥ 130 mph.</summary>
        public bool WindborneDebrisArea     { get; set; }
        /// <summary>SB 1218/HB 911 — new R-1/R-2 residential envelope must withstand ≥ 160 mph.
        /// True when applied Vult ≥ 130 mph (FBC coastal territory).</summary>
        public bool Envelope160MphRequired  { get; set; }
    }
}
