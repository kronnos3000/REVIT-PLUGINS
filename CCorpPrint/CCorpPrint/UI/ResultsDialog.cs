using System.Diagnostics;
using System.IO;
using Autodesk.Revit.UI;
using CCorpPrint.Models;

namespace CCorpPrint.UI
{
    /// <summary>
    /// End-of-job summary as a Revit TaskDialog with command links.
    /// </summary>
    public static class ResultsDialog
    {
        public static void Show(ResultSummary s)
        {
            var td = new TaskDialog("CCorp Print — Job complete")
            {
                MainInstruction = $"Mode: {s.Mode}   Total: {s.Total}   OK: {s.Succeeded}   " +
                                  $"Failed: {s.Failed.Count}   Skipped: {s.Skipped.Count}",
                MainContent     = $"Duration: {s.Duration.TotalSeconds:F1}s\n" +
                                  $"Job ID: {s.JobId}\n" +
                                  $"Output: {s.OutputFolder}",
            };

            if (!string.IsNullOrEmpty(s.OutputFolder) && Directory.Exists(s.OutputFolder))
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open output folder");
            if (!string.IsNullOrEmpty(s.LogFilePath) && File.Exists(s.LogFilePath))
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "View log file");

            if (s.Failed.Count > 0 || s.Skipped.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                if (s.Failed.Count > 0)
                {
                    sb.AppendLine("Failed:");
                    foreach (var f in s.Failed) sb.AppendLine($"  {f.SheetNumber}  {f.SheetName} — {f.Reason}");
                }
                if (s.Skipped.Count > 0)
                {
                    sb.AppendLine("Skipped:");
                    foreach (var k in s.Skipped) sb.AppendLine($"  {k.SheetNumber}  {k.SheetName} — {k.Reason}");
                }
                td.ExpandedContent = sb.ToString();
            }

            td.CommonButtons = TaskDialogCommonButtons.Close;
            var result = td.Show();

            if (result == TaskDialogResult.CommandLink1)
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + s.OutputFolder + "\""));
            else if (result == TaskDialogResult.CommandLink2)
                Process.Start(new ProcessStartInfo(s.LogFilePath) { UseShellExecute = true });
        }
    }
}
