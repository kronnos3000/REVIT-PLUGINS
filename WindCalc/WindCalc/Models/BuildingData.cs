namespace WindCalc.Models
{
    /// <summary>
    /// Building geometry data — auto-detected from Revit roof elements,
    /// displayed in Tab 2 where user can review and override.
    /// </summary>
    public class BuildingData
    {
        public string RoofType          { get; set; } = "";   // Hip, Gable, Flat, Mansard
        public string RoofPitch         { get; set; } = "";   // e.g. "4:12"
        public double RidgeHeightFt     { get; set; }
        public double EaveHeightFt      { get; set; }
        public double MeanRoofHeightFt  => (RidgeHeightFt + EaveHeightFt) / 2.0;
        public int    Stories           { get; set; } = 1;

        public bool   AutoDetected      { get; set; }   // true if read from Revit model
        public string DetectionNote     { get; set; } = "";
    }
}
