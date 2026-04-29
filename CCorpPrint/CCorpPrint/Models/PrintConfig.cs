using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace CCorpPrint.Models
{
    /// <summary>
    /// Persisted configuration for CCorpPrint, saved to %AppData%\CCorpPrint\config.json.
    /// </summary>
    [DataContract]
    public class PrintConfig
    {
        [DataMember] public List<NamingTemplate> Templates { get; set; } = new List<NamingTemplate>();
        [DataMember] public List<PaperSizeRule>  PaperSizeRules { get; set; } = new List<PaperSizeRule>();
        [DataMember] public MissingParamPolicy   MissingParamPolicy { get; set; } = MissingParamPolicy.LiteralToken;
        [DataMember] public string               DefaultOutputFolder { get; set; } = "";
        [DataMember] public bool                 UseDatedSubfolder { get; set; } = true;
        [DataMember] public GroupBy              DefaultGroupBy { get; set; } = GroupBy.None;
        [DataMember] public bool                 LoggingEnabled { get; set; } = true;
        [DataMember] public bool                 ReplaceWhitespaceWithUnderscore { get; set; } = true;
        [DataMember] public int                  MaxFilenameLength { get; set; } = 200;
        [DataMember] public DateTime             LastUpdateCheckUtc { get; set; }

        public static string ConfigDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CCorpPrint");

        private static string ConfigFilePath =>
            Path.Combine(ConfigDirectory, "config.json");

        public void Save()
        {
            Directory.CreateDirectory(ConfigDirectory);
            var serializer = new DataContractJsonSerializer(typeof(PrintConfig));
            using var stream = new FileStream(ConfigFilePath, FileMode.Create);
            serializer.WriteObject(stream, this);
        }

        public static PrintConfig LoadOrDefault()
        {
            if (!File.Exists(ConfigFilePath)) return WithDefaults(new PrintConfig());
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(PrintConfig));
                using var stream = File.OpenRead(ConfigFilePath);
                var cfg = (PrintConfig)serializer.ReadObject(stream) ?? new PrintConfig();
                return WithDefaults(cfg);
            }
            catch
            {
                return WithDefaults(new PrintConfig());
            }
        }

        private static PrintConfig WithDefaults(PrintConfig cfg)
        {
            if (cfg.PaperSizeRules == null || cfg.PaperSizeRules.Count == 0)
            {
                cfg.PaperSizeRules = new List<PaperSizeRule>
                {
                    new PaperSizeRule { WidthInches = 36, HeightInches = 24, PaperSizeName = "ARCH D" },
                    new PaperSizeRule { WidthInches = 24, HeightInches = 36, PaperSizeName = "ARCH D" },
                    new PaperSizeRule { WidthInches = 30, HeightInches = 42, PaperSizeName = "ARCH E1" },
                    new PaperSizeRule { WidthInches = 24, HeightInches = 18, PaperSizeName = "ARCH C" },
                    new PaperSizeRule { WidthInches = 18, HeightInches = 12, PaperSizeName = "ARCH B" },
                    new PaperSizeRule { WidthInches = 17, HeightInches = 11, PaperSizeName = "Tabloid" },
                    new PaperSizeRule { WidthInches = 11, HeightInches = 8.5, PaperSizeName = "Letter" },
                };
            }
            if (cfg.Templates == null) cfg.Templates = new List<NamingTemplate>();
            if (string.IsNullOrEmpty(cfg.DefaultOutputFolder))
            {
                cfg.DefaultOutputFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "CCorpPrint");
            }
            return cfg;
        }
    }
}
