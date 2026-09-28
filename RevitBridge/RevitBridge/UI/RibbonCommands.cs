using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitBridge.Services;

namespace RevitBridge.UI
{
    [Transaction(TransactionMode.ReadOnly)]
    public class ToggleBridgeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (BridgeHost.IsRunning) BridgeHost.Stop();
            else BridgeHost.Start();
            App.RefreshToggle();
            Logger.Info("Bridge switched " + (BridgeHost.IsRunning ? "ON" : "OFF") + " from the ribbon.");
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class StatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            var sb = new StringBuilder();
            sb.AppendLine($"Bridge:   {(BridgeHost.IsRunning ? "ON" : "OFF")}");
            sb.AppendLine($"Pipe:     \\\\.\\pipe\\{BridgeHost.PipeName}");
            sb.AppendLine($"PID:      {BridgeHost.Pid}");
            sb.AppendLine($"Revit:    {BridgeHost.RevitYear} ({BridgeHost.VersionBuild})");
            sb.AppendLine($"Version:  {UpdateChecker.CurrentVersion}");
            sb.AppendLine($"Document: {(doc == null ? "(none)" : doc.Title)}");

            var recent = Logger.RecentCalls();
            var td = new TaskDialog("Claude Bridge Status")
            {
                MainInstruction = BridgeHost.IsRunning ? "Claude bridge is ON" : "Claude bridge is OFF",
                MainContent = sb.ToString(),
                ExpandedContent = recent.Count == 0 ? "No calls yet." : "Last calls:\n" + string.Join("\n", recent),
            };
            td.Show();
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class OpenLogCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var path = Logger.LogFilePath;
            try
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.AppendAllText(path, "");
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Open Log", "Could not open " + path + "\n" + ex.Message);
            }
            return Result.Succeeded;
        }
    }
}
