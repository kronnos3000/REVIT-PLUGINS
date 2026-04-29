using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using CCorpPrint.Models;
using CCorpPrint.RevitIO;
using CCorpPrint.Services;

namespace CCorpPrint.Engine
{
    /// <summary>
    /// PDF and physical-printer dispatcher.
    ///
    /// PDF export (separate or combined) does not modify the document and runs
    /// outside any transaction. Physical print uses PrintManager + a temporary
    /// ViewSheetSet wrapped in a TransactionGroup that is rolled back so the
    /// model is left untouched.
    ///
    /// All entry points must be called from Revit's UI thread — PrintManager
    /// is single-threaded and stateful.
    /// </summary>
    public class PrintEngine
    {
        private readonly Document _doc;
        private readonly PrintConfig _cfg;
        private readonly Logger _log;

        public PrintEngine(Document doc, PrintConfig cfg, Logger log)
        {
            _doc = doc;
            _cfg = cfg;
            _log = log;
        }

        public ResultSummary Run(PrintJob job)
        {
            switch (job.Mode)
            {
                case OutputMode.SeparatePdf:     return ExportSeparatePdfs(job);
                case OutputMode.CombinedPdf:    return ExportCombinedPdf(job);
                case OutputMode.PhysicalPrinter: return PrintToPhysicalPrinter(job);
                default: throw new InvalidOperationException("Unknown OutputMode: " + job.Mode);
            }
        }

        // ── PDF: separate ────────────────────────────────────────────────────

        private ResultSummary ExportSeparatePdfs(PrintJob job)
        {
            var summary = NewSummary(job);
            var sw = Stopwatch.StartNew();
            Directory.CreateDirectory(job.OutputFolder);

            // Revit's PDFExportOptions.FileName is unreliable in separate mode —
            // depending on version it's either ignored (writes "Sheet-<name>.pdf"
            // from a built-in rule) or it appends rather than replaces. The only
            // dependable solution: snapshot the folder, export, find the new file,
            // and rename it to the name we resolved.
            var opts = new PDFExportOptions
            {
                Combine         = false,
                StopOnError     = false,
                AlwaysUseRaster = false,
                ColorDepth      = ColorDepthType.Color,
                ExportQuality   = PDFExportQualityType.DPI300,
                PaperFormat     = ExportPaperFormat.Default,
                PaperPlacement  = PaperPlacementType.Center,
            };

            foreach (var entry in job.Sheets)
            {
                if (ShouldSkip(entry, summary)) continue;
                try
                {
                    var before = SnapshotPdfs(job.OutputFolder);
                    opts.FileName = entry.ResolvedFileName;
                    _doc.Export(job.OutputFolder, new List<ElementId> { entry.Sheet.Id }, opts);

                    string desiredPath = Path.Combine(job.OutputFolder, entry.ResolvedFileName + ".pdf");
                    string actualPath  = FindNewPdf(job.OutputFolder, before, entry.ResolvedFileName);

                    if (actualPath != null && !PathsEqual(actualPath, desiredPath))
                    {
                        if (File.Exists(desiredPath)) File.Delete(desiredPath);
                        File.Move(actualPath, desiredPath);
                        _log.Info(job.JobId, entry.Sheet.SheetNumber,
                            $"Exported and renamed: '{Path.GetFileName(actualPath)}' -> '{Path.GetFileName(desiredPath)}'");
                    }
                    else if (actualPath != null)
                    {
                        _log.Info(job.JobId, entry.Sheet.SheetNumber, $"Exported -> {desiredPath}");
                    }
                    else
                    {
                        _log.Warn(job.JobId, entry.Sheet.SheetNumber,
                            $"Export call returned but no new PDF was found in {job.OutputFolder}");
                    }
                    summary.Succeeded++;
                }
                catch (Exception ex)
                {
                    summary.Failed.Add(new SheetResult
                    {
                        SheetNumber = entry.Sheet.SheetNumber,
                        SheetName   = entry.Sheet.Name,
                        Reason      = ex.Message
                    });
                    _log.Error(job.JobId, entry.Sheet.SheetNumber, "Export failed: " + ex.Message);
                }
            }

            sw.Stop();
            summary.Duration = sw.Elapsed;
            return summary;
        }

        // ── PDF: combined ────────────────────────────────────────────────────

        private ResultSummary ExportCombinedPdf(PrintJob job)
        {
            var summary = NewSummary(job);
            var sw = Stopwatch.StartNew();
            Directory.CreateDirectory(job.OutputFolder);

            var opts = new PDFExportOptions
            {
                Combine         = true,
                StopOnError     = false,
                AlwaysUseRaster = false,
                ColorDepth      = ColorDepthType.Color,
                ExportQuality   = PDFExportQualityType.DPI300,
                PaperFormat     = ExportPaperFormat.Default,
                PaperPlacement  = PaperPlacementType.Center,
                FileName        = job.CombinedFileName,
            };

            try
            {
                _doc.Export(job.OutputFolder, job.SheetIds, opts);
                summary.Succeeded = job.Sheets.Count;
                _log.Info(job.JobId, "*", $"Combined export -> {Path.Combine(job.OutputFolder, job.CombinedFileName + ".pdf")}");
            }
            catch (Exception ex)
            {
                foreach (var entry in job.Sheets)
                {
                    summary.Failed.Add(new SheetResult
                    {
                        SheetNumber = entry.Sheet.SheetNumber,
                        SheetName   = entry.Sheet.Name,
                        Reason      = ex.Message
                    });
                }
                _log.Error(job.JobId, "*", "Combined export failed: " + ex.Message);
            }

            sw.Stop();
            summary.Duration = sw.Elapsed;
            return summary;
        }

        // ── Physical printer ─────────────────────────────────────────────────

        private ResultSummary PrintToPhysicalPrinter(PrintJob job)
        {
            var summary = NewSummary(job);
            var sw = Stopwatch.StartNew();

            using var tg = new TransactionGroup(_doc, "CCorpPrint physical print");
            try
            {
                tg.Start();

                using (var tx = new Transaction(_doc, "Configure print set"))
                {
                    tx.Start();
                    var pm = _doc.PrintManager;
                    pm.PrintToFile = false;

                    _log.Info(job.JobId, "*", $"Requesting printer driver: '{job.PrinterName}'");
                    try { pm.SelectNewPrintDriver(job.PrinterName); }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        tg.RollBack();
                        foreach (var entry in job.Sheets)
                            summary.Failed.Add(new SheetResult
                            {
                                SheetNumber = entry.Sheet.SheetNumber,
                                SheetName   = entry.Sheet.Name,
                                Reason      = "Printer not found: " + ex.Message
                            });
                        _log.Error(job.JobId, "*", "Printer not found: " + ex.Message);
                        sw.Stop();
                        summary.Duration = sw.Elapsed;
                        return summary;
                    }

                    pm.PrintRange = PrintRange.Select;

                    var setSvc = new PrintSetService(_doc);
                    setSvc.ConfigureCurrentSheetSet(job.SheetIds, "CCORP_TEMP_" + job.JobId);

                    tx.Commit();
                }

                try
                {
                    string actual;
                    try { actual = _doc.PrintManager.PrinterName; } catch { actual = "(unknown)"; }
                    _log.Info(job.JobId, "*", $"PrintManager.PrinterName resolved to: '{actual}'");

                    _doc.PrintManager.SubmitPrint();
                    summary.Succeeded = job.Sheets.Count;
                    _log.Info(job.JobId, "*", $"Submitted {job.Sheets.Count} sheets to '{actual}'");
                }
                catch (Exception ex)
                {
                    foreach (var entry in job.Sheets)
                        summary.Failed.Add(new SheetResult
                        {
                            SheetNumber = entry.Sheet.SheetNumber,
                            SheetName   = entry.Sheet.Name,
                            Reason      = ex.Message
                        });
                    _log.Error(job.JobId, "*", "SubmitPrint failed: " + ex.Message);
                }
            }
            finally
            {
                if (tg.HasStarted() && !tg.HasEnded())
                {
                    try { tg.RollBack(); } catch { /* leave-no-trace effort */ }
                }
            }

            sw.Stop();
            summary.Duration = sw.Elapsed;
            return summary;
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private bool ShouldSkip(SheetEntry entry, ResultSummary summary)
        {
            if (_cfg.MissingParamPolicy == MissingParamPolicy.Error
                && entry.MissingTokens != null
                && entry.MissingTokens.Count > 0)
            {
                summary.Skipped.Add(new SheetResult
                {
                    SheetNumber = entry.Sheet.SheetNumber,
                    SheetName   = entry.Sheet.Name,
                    Reason      = "Missing tokens: " + string.Join(", ", entry.MissingTokens)
                });
                _log.Warn("skip", entry.Sheet.SheetNumber,
                    "Skipped (missing tokens): " + string.Join(", ", entry.MissingTokens));
                return true;
            }
            return false;
        }

        private ResultSummary NewSummary(PrintJob job)
        {
            return new ResultSummary
            {
                JobId        = job.JobId,
                Mode         = job.Mode,
                Total        = job.Sheets.Count,
                OutputFolder = job.OutputFolder,
                LogFilePath  = _log.LogFilePath,
            };
        }

        // ── filename rename helpers ──────────────────────────────────────────

        private static System.Collections.Generic.Dictionary<string, long> SnapshotPdfs(string folder)
        {
            var map = new System.Collections.Generic.Dictionary<string, long>(System.StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(folder)) return map;
            foreach (var f in Directory.EnumerateFiles(folder, "*.pdf"))
            {
                try { map[f] = new FileInfo(f).Length; } catch { }
            }
            return map;
        }

        // After an export, find the file Revit just produced. Strategy:
        //   1. A *.pdf that wasn't there before (set difference) — strongest signal.
        //   2. If multiple new files appear, prefer the one whose name contains
        //      the resolved name (collision-safe re-export of the same sheet).
        //   3. Fall back to the most recently modified PDF.
        private static string FindNewPdf(
            string folder,
            System.Collections.Generic.Dictionary<string, long> before,
            string desiredStem)
        {
            if (!Directory.Exists(folder)) return null;

            var current = Directory.EnumerateFiles(folder, "*.pdf").ToList();
            var added = current.Where(p => !before.ContainsKey(p)).ToList();

            if (added.Count == 1) return added[0];
            if (added.Count > 1)
            {
                var match = added.FirstOrDefault(p =>
                    Path.GetFileNameWithoutExtension(p).IndexOf(desiredStem, System.StringComparison.OrdinalIgnoreCase) >= 0);
                if (match != null) return match;
                return added.OrderByDescending(p => new FileInfo(p).LastWriteTimeUtc).First();
            }

            // No new files — Revit may have overwritten an existing one. Pick the
            // most recently touched PDF in the folder.
            var newest = current
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .FirstOrDefault();
            return newest?.FullName;
        }

        private static bool PathsEqual(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), System.StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase); }
        }
    }
}
