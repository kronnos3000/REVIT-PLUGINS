using System.Runtime.Serialization;

namespace CCorpPrint.Models
{
    /// <summary>
    /// Maps a titleblock by its sheet width/height (in inches, type params)
    /// to a printer paper-size name. First matching rule wins.
    /// </summary>
    [DataContract]
    public class PaperSizeRule
    {
        [DataMember] public double WidthInches  { get; set; }
        [DataMember] public double HeightInches { get; set; }
        [DataMember] public double ToleranceInches { get; set; } = 0.25;
        [DataMember] public string PaperSizeName { get; set; } = "";

        public bool Matches(double w, double h)
        {
            return System.Math.Abs(w - WidthInches)  <= ToleranceInches
                && System.Math.Abs(h - HeightInches) <= ToleranceInches;
        }
    }
}
