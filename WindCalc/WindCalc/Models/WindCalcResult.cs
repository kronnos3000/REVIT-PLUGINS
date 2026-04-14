namespace WindCalc.Models
{
    /// <summary>
    /// Combined result of wind calculation — site data + building data —
    /// ready to be written into Revit shared params and the report sheet.
    /// </summary>
    public class WindCalcResult
    {
        public SiteData     Site     { get; set; } = new SiteData();
        public BuildingData Building { get; set; } = new BuildingData();

        public bool Success  { get; set; }
        public string Error  { get; set; } = "";

        /// <summary>"9th" or "8th" — passed from WindCalcConfig at apply time.</summary>
        public string CodeEdition { get; set; } = "9th";

        // Convenience accessors for Revit writers
        public double AppliedVult              => Site.AppliedVult;
        public string ExposureCategory         => Site.ExposureCategory;
        public string RiskCategory             => Site.RiskCategory;
        public double ElevationFt              => Site.ElevationFt;
        public string FloodZone                => Site.FloodZone;
        public double FloodBfeFt               => Site.FloodBfeFt;
        public string MatchedAddress           => Site.MatchedAddress;
        public bool   WindborneDebrisArea      => Site.WindborneDebrisArea;
        public bool   Envelope160MphRequired   => Site.Envelope160MphRequired;
    }
}
