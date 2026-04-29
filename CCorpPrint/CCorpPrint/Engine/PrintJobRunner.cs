using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using CCorpPrint.Models;
using CCorpPrint.Services;

namespace CCorpPrint.Engine
{
    /// <summary>
    /// Builds a PrintJob from user selections + a NamingTemplate, resolving
    /// per-sheet filenames up front (so we can show a preview, deduplicate
    /// collisions, and surface missing tokens before any print starts), then
    /// hands off to PrintEngine.
    /// </summary>
    public class PrintJobRunner
    {
        private readonly Document _doc;
        private readonly PrintConfig _cfg;
        private readonly Logger _log;

        public PrintJobRunner(Document doc, PrintConfig cfg, Logger log)
        {
            _doc = doc;
            _cfg = cfg;
            _log = log;
        }

        public ResultSummary Run(
            IList<ViewSheet> sheets,
            NamingTemplate template,
            OutputMode mode,
            string outputFolder,
            string printerName)
        {
            var jobId = _log.BeginJob(mode);
            var engine = new NameTemplateEngine(_cfg, _doc);

            // Resolve per-sheet names
            var resolved = new List<ResolvedName>();
            for (int i = 0; i < sheets.Count; i++)
                resolved.Add(engine.Resolve(template.PerSheet ?? "{Sheet Number}", sheets[i], i + 1, sheets.Count, jobId));

            var rawNames = resolved.Select(r => r.FileName).ToList();
            var unique = engine.Deduplicate(rawNames, outputFolder, ".pdf");

            var entries = new List<SheetEntry>();
            for (int i = 0; i < sheets.Count; i++)
            {
                entries.Add(new SheetEntry
                {
                    Sheet            = sheets[i],
                    ResolvedFileName = unique[i],
                    MissingTokens    = resolved[i].MissingTokens,
                });
            }

            string combined = null;
            if (mode == OutputMode.CombinedPdf)
            {
                var first = sheets.FirstOrDefault();
                var resCombined = engine.Resolve(
                    template.Combined ?? "{ProjectFileName}_FullSet_{Today:yyyy-MM-dd}",
                    first, 1, sheets.Count, jobId);
                combined = string.IsNullOrEmpty(resCombined.FileName) ? "Combined" : resCombined.FileName;
            }

            var job = new PrintJob
            {
                JobId            = jobId,
                Mode             = mode,
                OutputFolder     = outputFolder,
                Sheets           = entries,
                CombinedFileName = combined,
                PrinterName      = printerName,
            };

            var printer = new PrintEngine(_doc, _cfg, _log);
            return printer.Run(job);
        }
    }
}
