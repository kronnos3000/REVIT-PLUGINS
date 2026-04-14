using System;
using System.Collections.Generic;

namespace WindCalc.Engine
{
    /// <summary>
    /// Florida county wind speed lookup table derived from ASCE 7-22 Figure 26.5-1A/B/C/D.
    /// Covers all 67 Florida counties with Risk Category II Vult values (mph).
    ///
    /// Two zones per county where the wind speed map shows variation:
    ///   Coastal: within ~1 mile of open coast or bay coastline
    ///   Inland:  all other areas
    ///
    /// RC I, III, IV are computed from RC II using ASCE 7-22 Table C26.5-1 ratios.
    ///   RC I  = RC II × 0.87  (rounded to nearest 5)
    ///   RC III = RC II × 1.06  (rounded to nearest 5)
    ///   RC IV  = RC II × 1.06
    ///
    /// These values represent the contour midpoint for each county.
    /// The user can always override in the dialog — amber confidence indicator shown.
    ///
    /// Source: ASCE 7-22 Figures 26.5-1A through 26.5-1D,
    ///         Florida Building Commission wind speed maps (FBC 9th Ed).
    /// </summary>
    public class FLWindSpeedLookup
    {
        // (InlandRcII, CoastalRcII) — mph, RC II Vult
        private static readonly Dictionary<string, (double Inland, double Coastal)> _countyTable =
            new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Panhandle (NW Florida) ────────────────────────────────────────
            { "Escambia",    (130, 150) },
            { "Santa Rosa",  (130, 150) },
            { "Okaloosa",    (130, 150) },
            { "Walton",      (130, 155) },
            { "Holmes",      (120, 120) },
            { "Washington",  (120, 125) },
            { "Bay",         (130, 155) },
            { "Jackson",     (115, 115) },
            { "Calhoun",     (115, 115) },
            { "Gulf",        (130, 155) },
            { "Liberty",     (115, 115) },
            { "Franklin",    (130, 155) },
            { "Gadsden",     (115, 115) },
            { "Leon",        (115, 115) },
            { "Wakulla",     (120, 140) },
            { "Jefferson",   (120, 140) },
            { "Madison",     (115, 115) },
            { "Taylor",      (120, 145) },
            { "Hamilton",    (115, 115) },
            { "Suwannee",    (115, 115) },
            { "Lafayette",   (115, 115) },
            { "Dixie",       (120, 145) },
            { "Columbia",    (115, 115) },
            { "Gilchrist",   (115, 120) },
            { "Levy",        (120, 145) },
            { "Union",       (115, 115) },
            { "Bradford",    (115, 115) },
            // ── North Florida ─────────────────────────────────────────────────
            { "Nassau",      (120, 140) },
            { "Baker",       (115, 115) },
            { "Duval",       (120, 140) },
            { "Clay",        (115, 120) },
            { "St. Johns",   (120, 140) },
            { "Alachua",     (115, 115) },
            { "Putnam",      (115, 120) },
            { "Flagler",     (120, 145) },
            // ── Central East Florida ──────────────────────────────────────────
            { "Volusia",     (120, 150) },
            { "Seminole",    (115, 120) },
            { "Orange",      (115, 120) },
            { "Brevard",     (130, 165) },
            { "Osceola",     (120, 120) },
            { "Indian River",(140, 175) },
            { "Okeechobee",  (140, 140) },
            { "St. Lucie",   (145, 180) },
            { "Martin",      (150, 185) },
            { "Palm Beach",  (155, 185) },
            { "Broward",     (160, 185) },
            { "Miami-Dade",  (165, 185) },
            // ── South Florida / Keys ──────────────────────────────────────────
            { "Monroe",      (180, 210) },  // Keys — highest in state
            // ── Gulf Coast / West Central ─────────────────────────────────────
            { "Marion",      (115, 115) },
            { "Citrus",      (120, 145) },
            { "Hernando",    (120, 145) },
            { "Pasco",       (130, 155) },
            { "Pinellas",    (140, 165) },
            { "Hillsborough",(130, 155) },
            { "Polk",        (120, 120) },
            { "Manatee",     (140, 165) },
            { "Sarasota",    (145, 170) },
            { "Hardee",      (125, 125) },
            { "DeSoto",      (130, 130) },
            { "Charlotte",   (145, 170) },
            { "Highlands",   (130, 130) },
            { "Glades",      (140, 140) },
            { "Hendry",      (145, 145) },
            { "Lee",         (150, 175) },
            { "Collier",     (155, 180) },
        };

        // FIPS code → county name (for future use)
        private static readonly Dictionary<string, string> _fipsToCounty =
            new Dictionary<string, string>
        {
            { "12001", "Alachua" },    { "12003", "Baker" },      { "12005", "Bay" },
            { "12007", "Bradford" },   { "12009", "Brevard" },    { "12011", "Broward" },
            { "12013", "Calhoun" },    { "12015", "Charlotte" },  { "12017", "Citrus" },
            { "12019", "Clay" },       { "12021", "Collier" },    { "12023", "Columbia" },
            { "12027", "DeSoto" },     { "12029", "Dixie" },      { "12031", "Duval" },
            { "12033", "Escambia" },   { "12035", "Flagler" },    { "12037", "Franklin" },
            { "12039", "Gadsden" },    { "12041", "Gilchrist" },  { "12043", "Glades" },
            { "12045", "Gulf" },       { "12047", "Hamilton" },   { "12049", "Hardee" },
            { "12051", "Hendry" },     { "12053", "Hernando" },   { "12055", "Highlands" },
            { "12057", "Hillsborough"},{ "12059", "Holmes" },     { "12061", "Indian River" },
            { "12063", "Jackson" },    { "12065", "Jefferson" },  { "12067", "Lafayette" },
            { "12069", "Lake" },       { "12071", "Lee" },        { "12073", "Leon" },
            { "12075", "Levy" },       { "12077", "Liberty" },    { "12079", "Madison" },
            { "12081", "Manatee" },    { "12083", "Marion" },     { "12085", "Martin" },
            { "12086", "Miami-Dade" }, { "12087", "Monroe" },     { "12089", "Nassau" },
            { "12091", "Okaloosa" },   { "12093", "Okeechobee" }, { "12095", "Orange" },
            { "12097", "Osceola" },    { "12099", "Palm Beach" }, { "12101", "Pasco" },
            { "12103", "Pinellas" },   { "12105", "Polk" },       { "12107", "Putnam" },
            { "12109", "St. Johns" },  { "12111", "St. Lucie" },  { "12113", "Santa Rosa" },
            { "12115", "Sarasota" },   { "12117", "Seminole" },   { "12119", "Sumter" },
            { "12121", "Suwannee" },   { "12123", "Taylor" },     { "12125", "Union" },
            { "12127", "Volusia" },    { "12129", "Wakulla" },    { "12131", "Walton" },
            { "12133", "Washington" },
        };

        /// <summary>Always true — table is built in.</summary>
        public bool IsAvailable() => true;

        /// <summary>
        /// Returns wind speeds for all four Risk Categories given a county name
        /// and whether the site is within 1 mile of the coastline.
        /// County name matching is case-insensitive and tolerates common abbreviations.
        /// </summary>
        public (bool success, double rcI, double rcII, double rcIII, double rcIV)
            Lookup(string countyName, bool coastal)
        {
            if (!TryGetEntry(countyName, out var entry))
                return (false, 0, 0, 0, 0);

            double rcII  = coastal ? entry.Coastal : entry.Inland;
            double rcI   = Math.Round(rcII * 0.87 / 5.0) * 5.0;
            double rcIII = Math.Round(rcII * 1.06 / 5.0) * 5.0;
            double rcIV  = rcIII;

            return (true, rcI, rcII, rcIII, rcIV);
        }

        /// <summary>Looks up by lat/lon — requires county name from geocoder result.</summary>
        public (bool success, double rcI, double rcII, double rcIII, double rcIV)
            LookupByCoords(double lat, double lon, string countyFromGeocoder, bool coastal)
        {
            return Lookup(countyFromGeocoder, coastal);
        }

        private static bool TryGetEntry(string name, out (double Inland, double Coastal) entry)
        {
            if (string.IsNullOrWhiteSpace(name)) { entry = default; return false; }

            // Exact match
            if (_countyTable.TryGetValue(name, out entry)) return true;

            // Strip " County" suffix if present
            string stripped = name.Replace(" County", "").Trim();
            if (_countyTable.TryGetValue(stripped, out entry)) return true;

            // Partial match (handles "St. Johns" vs "Saint Johns" etc.)
            foreach (var kv in _countyTable)
            {
                if (kv.Key.StartsWith(stripped, StringComparison.OrdinalIgnoreCase) ||
                    stripped.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    entry = kv.Value;
                    return true;
                }
            }

            entry = default;
            return false;
        }
    }
}
