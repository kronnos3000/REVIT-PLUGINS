using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using WindCalc.Models;

namespace WindCalc.RevitIO
{
    /// <summary>
    /// Creates and populates Wind Calculator shared parameters on the Revit project.
    /// If the shared parameter file path is not configured, a temporary file is created.
    /// </summary>
    public class SharedParamWriter
    {
        private const string GroupName = "Wind Analysis";

        // Revit 2025 uses ForgeTypeId to identify parameter spec types.
        // SpecTypeId nested classes are types, not values — use raw ForgeTypeId strings instead.
        private static readonly ForgeTypeId _numType  = new ForgeTypeId("autodesk.spec:spec.measurableSpec.number-2.0.0");
        private static readonly ForgeTypeId _textType = new ForgeTypeId("autodesk.spec:spec.string-2.0.0");

        private static readonly List<(string name, ForgeTypeId type, string description)> ParamDefs =
            new List<(string, ForgeTypeId, string)>
        {
            // ── Wind analysis ─────────────────────────────────────────────────
            ("WIND_VULT_RC_II",         _numType,  "Applied design wind speed Vult for Risk Category II (mph)"),
            ("WIND_VULT_RC_III",        _numType,  "Design wind speed Vult for Risk Category III (mph)"),
            ("WIND_VASD_RC_II",         _numType,  "Allowable Stress Design wind speed Vasd RC II (mph)"),
            ("WIND_ASCE_COMPUTED",      _numType,  "Raw ASCE 7-22 computed Vult before firm minimum (mph)"),
            ("WIND_EXPOSURE_CATEGORY",  _textType, "Wind exposure category per ASCE 7-22 (B, C, or D)"),
            ("WIND_RISK_CATEGORY",      _textType, "Building risk category per ASCE 7-22 (I, II, III, IV)"),
            ("SITE_ELEVATION_FT",       _numType,  "Ground elevation NAVD88 (ft)"),
            ("SITE_LATITUDE",           _numType,  "Site latitude (decimal degrees)"),
            ("SITE_LONGITUDE",          _numType,  "Site longitude (decimal degrees)"),
            ("FLOOD_ZONE",              _textType, "FEMA flood zone designation"),
            ("FLOOD_BFE_FT",            _numType,  "FEMA Base Flood Elevation (ft NAVD88)"),
            ("ROOF_TYPE",               _textType, "Roof type (Hip, Gable, Flat, Mansard, etc.)"),
            ("ROOF_PITCH",              _textType, "Roof pitch (e.g. 4:12)"),
            ("ROOF_RIDGE_HEIGHT_FT",    _numType,  "Ridge height above grade (ft)"),
            ("ROOF_EAVE_HEIGHT_FT",     _numType,  "Eave height above grade (ft)"),
            ("ROOF_MEAN_HEIGHT_FT",     _numType,  "Mean roof height above grade (ft)"),
            ("WIND_WINDBORNE_DEBRIS",   _textType, "Windborne debris region — YES or NO (ASCE 7-22 §26.11.1)"),
            ("WIND_160MPH_ENVELOPE",    _textType, "160 MPH impact-resistant envelope required — YES or NO (FBC 9th Ed SB 1218)"),
            ("CODE_EDITION",            _textType, "Florida Building Code edition used for this analysis"),

            // ── Parcel / property data ─────────────────────────────────────────
            // Names match the pyRevit PCPAO Sync tool so LookupParameter reuses
            // existing shared params already bound in the firm's Revit template.
            ("Parcel Number",           _textType, "County parcel / folio number"),
            ("Year Built",              _numType,  "Year the structure was originally built"),
            ("Construction Class",      _textType, "Building construction class from county property appraiser"),
            ("Site Owner",              _textType, "Property owner name from county records"),
            ("Legal Description",       _textType, "Legal description of the parcel from county records"),
            ("Client Address",          _textType, "Owner mailing address from county property records"),
            ("Client Name",             _textType, "Property owner / client name (mirrors Site Owner)"),
        };

        private readonly Document _doc;
        private readonly string   _sharedParamFilePath;

        public SharedParamWriter(Document doc, string sharedParamFilePath)
        {
            _doc                 = doc;
            _sharedParamFilePath = sharedParamFilePath;
        }

        public void WriteResult(WindCalcResult result)
        {
            EnsureSharedParameters();

            // Bind to ProjectInformation category so params appear in Project Info
            var categories = _doc.Settings.Categories;
            var projInfoCat = _doc.Settings.Categories.get_Item(BuiltInCategory.OST_ProjectInformation);
            var catSet = new CategorySet();
            catSet.Insert(projInfoCat);

            // Map of param name → value to write
            var values = BuildValueMap(result);

            using var tx = new Transaction(_doc, "Write Wind Calc Parameters");
            tx.Start();

            foreach (var def in ParamDefs)
            {
                var param = GetOrCreateProjectParam(def.name, def.type, catSet);
                if (param == null) continue;

                if (values.TryGetValue(def.name, out object val))
                    SetParamValue(param, val);
            }

            tx.Commit();
        }

        private Dictionary<string, object> BuildValueMap(WindCalcResult r)
        {
            var map = new Dictionary<string, object>
            {
                // ── Wind analysis ─────────────────────────────────────────────
                ["WIND_VULT_RC_II"]        = r.Site.AppliedVult,
                ["WIND_VULT_RC_III"]       = r.Site.AsceVultRcIII,
                ["WIND_VASD_RC_II"]        = r.Site.AsceVasdRcII,
                ["WIND_ASCE_COMPUTED"]     = r.Site.AsceVultRcII,
                ["WIND_EXPOSURE_CATEGORY"] = r.Site.ExposureCategory,
                ["WIND_RISK_CATEGORY"]     = r.Site.RiskCategory,
                ["SITE_ELEVATION_FT"]      = r.Site.ElevationFt,
                ["SITE_LATITUDE"]          = r.Site.Latitude,
                ["SITE_LONGITUDE"]         = r.Site.Longitude,
                ["FLOOD_ZONE"]             = r.Site.FloodZone,
                ["FLOOD_BFE_FT"]           = double.IsNaN(r.Site.FloodBfeFt) ? (object)"N/A" : r.Site.FloodBfeFt,
                ["ROOF_TYPE"]              = r.Building.RoofType,
                ["ROOF_PITCH"]             = r.Building.RoofPitch,
                ["ROOF_RIDGE_HEIGHT_FT"]   = r.Building.RidgeHeightFt,
                ["ROOF_EAVE_HEIGHT_FT"]    = r.Building.EaveHeightFt,
                ["ROOF_MEAN_HEIGHT_FT"]    = r.Building.MeanRoofHeightFt,
                ["WIND_WINDBORNE_DEBRIS"]  = r.Site.WindborneDebrisArea      ? "YES" : "NO",
                ["WIND_160MPH_ENVELOPE"]   = r.Site.Envelope160MphRequired   ? "YES" : "NO",
                ["CODE_EDITION"]           = r.CodeEdition == "9th"
                    ? "FBC 9th Edition 2026 / ASCE 7-22"
                    : "FBC 8th Edition 2023 / ASCE 7-22",

                // ── Parcel / property data ────────────────────────────────────
                ["Parcel Number"]      = r.Site.ParcelId,
                ["Construction Class"] = r.Site.ConstructionClass,
                ["Site Owner"]         = r.Site.ParcelOwner,
                ["Legal Description"]  = r.Site.LegalDescription,
                ["Client Address"]     = r.Site.OwnerMailingAddress,
                ["Client Name"]        = r.Site.ParcelOwner,    // same value, different param slot
            };

            // Year Built is a number param; only write when we have a real value
            if (r.Site.YearBuilt > 0)
                map["Year Built"] = (double)r.Site.YearBuilt;

            return map;
        }

        private void EnsureSharedParameters()
        {
            // Use configured path or create a temp file
            string spFile = string.IsNullOrWhiteSpace(_sharedParamFilePath)
                ? Path.Combine(Path.GetTempPath(), "WindCalc_SharedParams.txt")
                : _sharedParamFilePath;

            if (!File.Exists(spFile))
                File.WriteAllText(spFile, "# Wind Calculator Shared Parameters\n");

            _doc.Application.SharedParametersFilename = spFile;
        }

        private Parameter GetOrCreateProjectParam(string paramName, ForgeTypeId paramType, CategorySet catSet)
        {
            try
            {
                // Check if already bound to ProjectInformation
                var projInfo = _doc.ProjectInformation;
                var existing = projInfo.LookupParameter(paramName);
                if (existing != null) return existing;

                // Create definition in shared param file
                var defFile = _doc.Application.OpenSharedParameterFile();
                var group   = defFile.Groups.get_Item(GroupName) ?? defFile.Groups.Create(GroupName);

                ExternalDefinition extDef;
                var existingDef = group.Definitions.get_Item(paramName);
                if (existingDef is ExternalDefinition ed)
                {
                    extDef = ed;
                }
                else
                {
                    var opts = new ExternalDefinitionCreationOptions(paramName, paramType)
                    {
                        Visible = true,
                        UserModifiable = true
                    };
                    extDef = group.Definitions.Create(opts) as ExternalDefinition;
                }

                if (extDef == null) return null;

                // Bind to project
                Binding binding = new InstanceBinding(catSet);
                _doc.ParameterBindings.Insert(extDef, binding);

                return _doc.ProjectInformation.LookupParameter(paramName);
            }
            catch
            {
                return null;
            }
        }

        private static void SetParamValue(Parameter param, object value)
        {
            if (param == null || param.IsReadOnly) return;

            switch (param.StorageType)
            {
                case StorageType.Double when value is double d:
                    param.Set(d);
                    break;
                case StorageType.String when value is string s:
                    param.Set(s);
                    break;
                case StorageType.String when value is double dbl:
                    param.Set(dbl.ToString("F2"));
                    break;
                case StorageType.String:
                    param.Set(value?.ToString() ?? "");
                    break;
            }
        }
    }
}
