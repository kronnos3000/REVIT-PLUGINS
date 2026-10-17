using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace WindCalc.RevitIO
{
    /// <summary>
    /// Defines and manages shared parameters for the A2 Product Approvals sheet.
    /// These use the CC_* naming convention and are distinct from the legacy WIND_* parameters.
    /// </summary>
    public static class A2SharedParams
    {
        // ─────────────────────────────────────────────────────────────────────────
        // Parameter Groups (matching CC_SharedParameters.txt)
        // ─────────────────────────────────────────────────────────────────────────
        public const string GroupWindAnalysis     = "Wind Analysis";      // Group 1
        public const string GroupProductApprovals = "Product Approvals";  // Group 5 (new)

        // ─────────────────────────────────────────────────────────────────────────
        // ForgeTypeId specs for Revit 2025+
        // ─────────────────────────────────────────────────────────────────────────
        private static readonly ForgeTypeId NumType  = new ForgeTypeId("autodesk.spec.aec:number-2.0.0");
        private static readonly ForgeTypeId TextType = new ForgeTypeId("autodesk.spec:spec.string-2.0.0");
        private static readonly ForgeTypeId IntType  = new ForgeTypeId("autodesk.spec.aec.structural:numberOfBars-2.0.0"); // Integer
        private static readonly ForgeTypeId LengthType = new ForgeTypeId("autodesk.spec.aec:length-2.0.0");
        private static readonly ForgeTypeId AreaType   = new ForgeTypeId("autodesk.spec.aec:area-2.0.0");
        private static readonly ForgeTypeId YesNoType  = new ForgeTypeId("autodesk.spec:spec.bool-1.0.0");

        // ─────────────────────────────────────────────────────────────────────────
        // Parameter Definition Structure
        // ─────────────────────────────────────────────────────────────────────────
        public class ParamDef
        {
            public string Name { get; set; }
            public ForgeTypeId Type { get; set; }
            public string Group { get; set; }
            public string Description { get; set; }
            public BuiltInCategory[] Categories { get; set; }
            public bool IsInstance { get; set; } = true; // true = instance, false = type
            public Guid? Guid { get; set; } // Optional: use if already in shared param file
        }

        // ─────────────────────────────────────────────────────────────────────────
        // WC-01: Wind Design Criteria (Project Information)
        // ─────────────────────────────────────────────────────────────────────────
        public static readonly ParamDef[] WC01_Params = new[]
        {
            new ParamDef { Name = "CC_Wind_RiskCategory", Type = TextType, Group = GroupWindAnalysis,
                Description = "Risk category per ASCE 7-22 (I–IV)",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation } },
            new ParamDef { Name = "CC_Wind_Vult", Type = NumType, Group = GroupWindAnalysis,
                Description = "Ultimate design wind speed (mph)",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation } },
            new ParamDef { Name = "CC_Wind_Vasd", Type = NumType, Group = GroupWindAnalysis,
                Description = "ASD wind speed = Vult x sqrt(0.6) (mph)",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation } },
            new ParamDef { Name = "CC_Wind_Exposure", Type = TextType, Group = GroupWindAnalysis,
                Description = "Exposure category (B/C/D)",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation } },
            new ParamDef { Name = "CC_Wind_Enclosure", Type = TextType, Group = GroupWindAnalysis,
                Description = "Enclosure classification (Enclosed/Partially enclosed/Open)",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation } },
            new ParamDef { Name = "CC_Wind_GCpi", Type = TextType, Group = GroupWindAnalysis,
                Description = "Internal pressure coefficient (e.g. +/-0.18)",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation } },
            new ParamDef { Name = "CC_Wind_MeanRoofHt", Type = LengthType, Group = GroupWindAnalysis,
                Description = "Mean roof height h (ft)",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation } },
            new ParamDef { Name = "CC_Wind_RoofSlope", Type = TextType, Group = GroupWindAnalysis,
                Description = "Roof slope (x:12)",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation } },
            new ParamDef { Name = "CC_Wind_RoofForm", Type = TextType, Group = GroupWindAnalysis,
                Description = "Roof form (Gable/Hip/Flat/Monoslope)",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation } },
            new ParamDef { Name = "CC_Wind_Kd", Type = NumType, Group = GroupWindAnalysis,
                Description = "Directionality factor Kd",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation } },
            new ParamDef { Name = "CC_Wind_Kzt", Type = NumType, Group = GroupWindAnalysis,
                Description = "Topographic factor Kzt",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation } },
            new ParamDef { Name = "CC_Wind_Ke", Type = NumType, Group = GroupWindAnalysis,
                Description = "Ground elevation factor Ke",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation } },
            new ParamDef { Name = "CC_Wind_qh", Type = NumType, Group = GroupWindAnalysis,
                Description = "Velocity pressure at mean roof height (psf)",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation } },
            new ParamDef { Name = "CC_Wind_EdgeDist_a", Type = LengthType, Group = GroupWindAnalysis,
                Description = "Edge distance a for zone assignment (ft)",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation } },
            new ParamDef { Name = "CC_Wind_WBDR", Type = YesNoType, Group = GroupWindAnalysis,
                Description = "Wind-borne debris region (YES/NO)",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation } },
            new ParamDef { Name = "CC_Wind_HVHZ", Type = YesNoType, Group = GroupWindAnalysis,
                Description = "High Velocity Hurricane Zone - Miami-Dade/Broward (YES/NO)",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation } },
        };

        // ─────────────────────────────────────────────────────────────────────────
        // WC-02: C&C Zone Pressures (Project Information)
        // 5 zones x 2 signs (pos/neg) x 2 load types (ult/ASD) = 20 params
        // ─────────────────────────────────────────────────────────────────────────
        public static ParamDef[] GetZonePressureParams()
        {
            var defs = new List<ParamDef>();
            for (int zone = 1; zone <= 5; zone++)
            {
                foreach (var sign in new[] { "Pos", "Neg" })
                {
                    foreach (var load in new[] { "Ult", "ASD" })
                    {
                        defs.Add(new ParamDef
                        {
                            Name = $"CC_Wind_Z{zone}_{sign}_{load}",
                            Type = NumType,
                            Group = GroupWindAnalysis,
                            Description = $"Zone {zone} {(sign == "Pos" ? "positive" : "negative")} {load} pressure (psf)",
                            Categories = new[] { BuiltInCategory.OST_ProjectInformation }
                        });
                    }
                }
            }
            // Governing DP (zone 5 ASD)
            defs.Add(new ParamDef
            {
                Name = "CC_Wind_GovDP_Pos",
                Type = NumType,
                Group = GroupWindAnalysis,
                Description = "Governing opening DP - positive ASD (psf)",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation }
            });
            defs.Add(new ParamDef
            {
                Name = "CC_Wind_GovDP_Neg",
                Type = NumType,
                Group = GroupWindAnalysis,
                Description = "Governing opening DP - negative ASD (psf)",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation }
            });
            defs.Add(new ParamDef
            {
                Name = "CC_Wind_EffArea",
                Type = AreaType,
                Group = GroupWindAnalysis,
                Description = "Default effective wind area (SF) - typically 10",
                Categories = new[] { BuiltInCategory.OST_ProjectInformation }
            });
            return defs.ToArray();
        }

        // ─────────────────────────────────────────────────────────────────────────
        // WC-04: Product Approval Schedule - TYPE Parameters (Windows/Doors)
        // ─────────────────────────────────────────────────────────────────────────
        public static readonly ParamDef[] WC04_TypeParams = new[]
        {
            new ParamDef { Name = "CC_FL_ApprovalNo", Type = TextType, Group = GroupProductApprovals,
                Description = "Florida product approval number (FL #####.##)",
                Categories = new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors },
                IsInstance = false },
            new ParamDef { Name = "CC_FL_ApprovalExpires", Type = TextType, Group = GroupProductApprovals,
                Description = "Florida approval expiration date (optional)",
                Categories = new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors },
                IsInstance = false },
            new ParamDef { Name = "CC_ImpactRated", Type = YesNoType, Group = GroupProductApprovals,
                Description = "Impact-rated glazing (YES/NO)",
                Categories = new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors },
                IsInstance = false },
            new ParamDef { Name = "CC_DP_Rated_Pos", Type = NumType, Group = GroupProductApprovals,
                Description = "Rated design pressure - positive ASD (psf)",
                Categories = new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors },
                IsInstance = false },
            new ParamDef { Name = "CC_DP_Rated_Neg", Type = NumType, Group = GroupProductApprovals,
                Description = "Rated design pressure - negative ASD (psf, magnitude)",
                Categories = new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors },
                IsInstance = false },
            new ParamDef { Name = "CC_Clear_Width", Type = LengthType, Group = GroupProductApprovals,
                Description = "Net clear opening width (in) - from manufacturer",
                Categories = new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors },
                IsInstance = false },
            new ParamDef { Name = "CC_Clear_Height", Type = LengthType, Group = GroupProductApprovals,
                Description = "Net clear opening height (in) - from manufacturer",
                Categories = new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors },
                IsInstance = false },
            new ParamDef { Name = "CC_Clear_Area", Type = AreaType, Group = GroupProductApprovals,
                Description = "Net clear opening area (SF) - computed from clear W x H",
                Categories = new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors },
                IsInstance = false },
        };

        // ─────────────────────────────────────────────────────────────────────────
        // WC-04: Product Approval Schedule - INSTANCE Parameters (Windows/Doors)
        // ─────────────────────────────────────────────────────────────────────────
        public static readonly ParamDef[] WC04_InstanceParams = new[]
        {
            new ParamDef { Name = "CC_Wind_Zone", Type = IntType, Group = GroupProductApprovals,
                Description = "Wind zone assignment (4 or 5) - computed from corner distance",
                Categories = new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors },
                IsInstance = true },
            new ParamDef { Name = "CC_DP_Req_Pos", Type = NumType, Group = GroupProductApprovals,
                Description = "Required design pressure - positive ASD (psf)",
                Categories = new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors },
                IsInstance = true },
            new ParamDef { Name = "CC_DP_Req_Neg", Type = NumType, Group = GroupProductApprovals,
                Description = "Required design pressure - negative ASD (psf, magnitude)",
                Categories = new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors },
                IsInstance = true },
            new ParamDef { Name = "CC_Wind_EffArea_Opening", Type = AreaType, Group = GroupProductApprovals,
                Description = "Effective wind area for this opening (SF) - default 10",
                Categories = new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors },
                IsInstance = true },
            new ParamDef { Name = "CC_Egress_Required", Type = YesNoType, Group = GroupProductApprovals,
                Description = "Designated emergency escape opening (YES/NO)",
                Categories = new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors },
                IsInstance = true },
            new ParamDef { Name = "CC_Egress_GradeFloor", Type = YesNoType, Group = GroupProductApprovals,
                Description = "Opening is on grade-floor level (5.0 SF threshold)",
                Categories = new[] { BuiltInCategory.OST_Windows },
                IsInstance = true },
            new ParamDef { Name = "CC_Egress_Status", Type = TextType, Group = GroupProductApprovals,
                Description = "Egress check result (PASS/FAIL/N/A)",
                Categories = new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors },
                IsInstance = true },
            new ParamDef { Name = "CC_Wind_Status", Type = TextType, Group = GroupProductApprovals,
                Description = "Wind check result (PASS/FAIL)",
                Categories = new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors },
                IsInstance = true },
        };

        // ─────────────────────────────────────────────────────────────────────────
        // Helper: Get all A2 parameter definitions
        // ─────────────────────────────────────────────────────────────────────────
        public static IEnumerable<ParamDef> GetAllParams()
        {
            foreach (var p in WC01_Params) yield return p;
            foreach (var p in GetZonePressureParams()) yield return p;
            foreach (var p in WC04_TypeParams) yield return p;
            foreach (var p in WC04_InstanceParams) yield return p;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Parameter Names (for easy lookup)
        // ─────────────────────────────────────────────────────────────────────────
        public static class Names
        {
            // WC-01
            public const string RiskCategory = "CC_Wind_RiskCategory";
            public const string Vult = "CC_Wind_Vult";
            public const string Vasd = "CC_Wind_Vasd";
            public const string Exposure = "CC_Wind_Exposure";
            public const string Enclosure = "CC_Wind_Enclosure";
            public const string GCpi = "CC_Wind_GCpi";
            public const string MeanRoofHt = "CC_Wind_MeanRoofHt";
            public const string RoofSlope = "CC_Wind_RoofSlope";
            public const string RoofForm = "CC_Wind_RoofForm";
            public const string Kd = "CC_Wind_Kd";
            public const string Kzt = "CC_Wind_Kzt";
            public const string Ke = "CC_Wind_Ke";
            public const string qh = "CC_Wind_qh";
            public const string EdgeDist_a = "CC_Wind_EdgeDist_a";
            public const string WBDR = "CC_Wind_WBDR";
            public const string HVHZ = "CC_Wind_HVHZ";

            // WC-02 governing
            public const string GovDP_Pos = "CC_Wind_GovDP_Pos";
            public const string GovDP_Neg = "CC_Wind_GovDP_Neg";
            public const string EffArea = "CC_Wind_EffArea";

            // WC-04 type params
            public const string FL_ApprovalNo = "CC_FL_ApprovalNo";
            public const string FL_ApprovalExpires = "CC_FL_ApprovalExpires";
            public const string ImpactRated = "CC_ImpactRated";
            public const string DP_Rated_Pos = "CC_DP_Rated_Pos";
            public const string DP_Rated_Neg = "CC_DP_Rated_Neg";
            public const string Clear_Width = "CC_Clear_Width";
            public const string Clear_Height = "CC_Clear_Height";
            public const string Clear_Area = "CC_Clear_Area";

            // WC-04 instance params
            public const string Wind_Zone = "CC_Wind_Zone";
            public const string DP_Req_Pos = "CC_DP_Req_Pos";
            public const string DP_Req_Neg = "CC_DP_Req_Neg";
            public const string Wind_EffArea_Opening = "CC_Wind_EffArea_Opening";
            public const string Egress_Required = "CC_Egress_Required";
            public const string Egress_GradeFloor = "CC_Egress_GradeFloor";
            public const string Egress_Status = "CC_Egress_Status";
            public const string Wind_Status = "CC_Wind_Status";

            /// <summary>
            /// Gets the zone pressure parameter name.
            /// </summary>
            /// <param name="zone">Zone number (1-5)</param>
            /// <param name="positive">True for positive pressure, false for negative</param>
            /// <param name="ultimate">True for ultimate load, false for ASD</param>
            public static string ZonePressure(int zone, bool positive, bool ultimate)
                => $"CC_Wind_Z{zone}_{(positive ? "Pos" : "Neg")}_{(ultimate ? "Ult" : "ASD")}";
        }
    }
}
