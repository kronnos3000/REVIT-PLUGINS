using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using CCorpPrint.Services;

namespace CCorpPrint
{
    /// <summary>
    /// Entry point for the CCorpPrint Revit plugin.
    /// Adds a "Sheet Printing" panel to the shared "CCorp Tools" ribbon tab.
    /// </summary>
    [Regeneration(RegenerationOption.Manual)]
    public class App : IExternalApplication
    {
        private const string TabName   = "CCorp Tools";
        private const string PanelName = "Sheet Printing";

        internal static UpdateInfo PendingUpdate;
        private ControlledApplication _controlledApp;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // Tab is shared with WindCalc and any other CCorp addin.
                try { application.CreateRibbonTab(TabName); }
                catch { /* already exists */ }

                // Don't double-create the panel if reload happens.
                var existing = application.GetRibbonPanels(TabName);
                RibbonPanel panel = existing.FirstOrDefault(p => p.Name == PanelName)
                                    ?? application.CreateRibbonPanel(TabName, PanelName);

                if (panel.GetItems().Count == 0)
                {
                    AddLargeButton(panel,
                        name:      "Print\nSheets",
                        className: "CCorpPrint.Commands.PrintSheetsCommand",
                        tooltip:   "Batch-print sheets to PDF or a physical printer\n" +
                                   "with parameter-driven file names. Tabs inside the\n" +
                                   "dialog cover printing, naming templates, and settings.",
                        iconFile:  "PrintSheets.png");
                }

                _controlledApp = application.ControlledApplication;
                SubscribeApplicationClosing(_controlledApp);
                TriggerVersionCheck();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("CCorp Print — Startup Error", ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        // ── Update prompt at shutdown (ApplicationClosing fires while UI is still alive) ─

        private static void SubscribeApplicationClosing(ControlledApplication ctrl)
        {
            // ApplicationClosingEventArgs is internal in Revit 2025+; subscribe
            // via reflection + Expression.Lambda so we never name the type.
            var evt = ctrl.GetType().GetEvent("ApplicationClosing");
            if (evt == null) return;
            var argsType   = evt.EventHandlerType.GetGenericArguments()[0];
            var senderParm = Expression.Parameter(typeof(object), "s");
            var argsParm   = Expression.Parameter(argsType, "e");
            var callTarget = typeof(App).GetMethod(
                nameof(OnRevitClosing),
                BindingFlags.NonPublic | BindingFlags.Static,
                null, Type.EmptyTypes, null);
            var body   = Expression.Call(callTarget);
            var lambda = Expression.Lambda(evt.EventHandlerType, body, senderParm, argsParm);
            evt.AddEventHandler(ctrl, lambda.Compile());
        }

        private static void OnRevitClosing()
        {
            var info = PendingUpdate;
            if (info == null || string.IsNullOrEmpty(info.LocalInstallerPath) ||
                !File.Exists(info.LocalInstallerPath))
            {
                return;
            }

            try
            {
                var td = new TaskDialog("CCorpPrint update available")
                {
                    MainInstruction = $"Version {info.Version} is available (you have {UpdateChecker.CurrentVersion}).",
                    MainContent     = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                                        ? "Run the installer now? Revit will finish closing first."
                                        : info.ReleaseNotes + "\n\nRun the installer now? Revit will finish closing first.",
                    CommonButtons   = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton   = TaskDialogResult.Yes,
                };
                if (td.Show() == TaskDialogResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(info.LocalInstallerPath) { UseShellExecute = true });
                }
            }
            catch
            {
                // Never block Revit's exit on a dialog failure.
            }
        }

        private static void TriggerVersionCheck()
        {
            Task.Run(async () =>
            {
                try { PendingUpdate = await UpdateChecker.CheckAsync(); }
                catch { /* offline, rate-limited, etc. */ }
            });
        }

        // ── Ribbon helpers ──────────────────────────────────────────────────

        private static string AssemblyPath => Assembly.GetExecutingAssembly().Location;

        private void AddLargeButton(RibbonPanel panel, string name,
            string className, string tooltip, string iconFile)
        {
            var data = new PushButtonData(
                name.Replace("\n", ""),
                name,
                AssemblyPath,
                className)
            {
                ToolTip    = tooltip,
                LargeImage = LoadImage(iconFile),
                Image      = LoadImage(iconFile, small: true)
            };
            panel.AddItem(data);
        }

        private BitmapImage LoadImage(string filename, bool small = false)
        {
            try
            {
                string dir  = Path.GetDirectoryName(AssemblyPath) ?? "";
                string path = Path.Combine(dir, "Resources", filename);
                if (!File.Exists(path)) return null;

                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource        = new Uri(path);
                img.DecodePixelWidth = small ? 16 : 32;
                img.EndInit();
                return img;
            }
            catch
            {
                return null;
            }
        }
    }
}
