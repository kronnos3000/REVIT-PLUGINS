using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace WindCalc.Models
{
    /// <summary>
    /// Persisted configuration for the Wind Calculator plugin.
    /// Saved to %AppData%\WindCalc\config.json between sessions.
    /// </summary>
    [DataContract]
    public class WindCalcConfig
    {
        // ── API Keys ──────────────────────────────────────────────────────────
        [DataMember] public string AsceApiKey        { get; set; } = "";

        // ── Firm Standards ────────────────────────────────────────────────────
        [DataMember] public double FirmMinimumVult   { get; set; } = 150.0;   // mph

        // ── Default Project Values ────────────────────────────────────────────
        [DataMember] public string DefaultRiskCategory    { get; set; } = "II";
        [DataMember] public string DefaultExposureCategory { get; set; } = "C";

        // ── Local Data Paths ──────────────────────────────────────────────────
        [DataMember] public string LocalDataFolder   { get; set; } = DefaultLocalDataPath;

        // ── Code Edition ──────────────────────────────────────────────────────
        /// <summary>"9th" (FBC 2026, default) or "8th" (FBC 2023).</summary>
        [DataMember] public string CodeEdition { get; set; } = "9th";

        // ── Shared Param File ─────────────────────────────────────────────────
        [DataMember] public string SharedParamFilePath { get; set; } = "";

        // ── Serialization ─────────────────────────────────────────────────────

        private static string ConfigDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindCalc");

        private static string ConfigFilePath =>
            Path.Combine(ConfigDirectory, "config.json");

        private static string DefaultLocalDataPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WindCalc", "LocalData");

        public void Save()
        {
            Directory.CreateDirectory(ConfigDirectory);
            var serializer = new DataContractJsonSerializer(typeof(WindCalcConfig));
            using var stream = new FileStream(ConfigFilePath, FileMode.Create);
            serializer.WriteObject(stream, this);
        }

        public static WindCalcConfig LoadOrDefault()
        {
            if (!File.Exists(ConfigFilePath)) return new WindCalcConfig();
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(WindCalcConfig));
                using var stream = File.OpenRead(ConfigFilePath);
                return (WindCalcConfig)serializer.ReadObject(stream) ?? new WindCalcConfig();
            }
            catch
            {
                return new WindCalcConfig();
            }
        }
    }
}
