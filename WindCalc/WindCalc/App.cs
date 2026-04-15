using System;
using System.Diagnostics;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WindCalc.Engine;
using WindCalc.Models;
using WindCalc.Services;

namespace WindCalc
{
    /// <summary>
    /// Entry point for the Wind Calculator Revit plugin.
    /// Registers the ribbon tab, panels, and buttons on Revit startup,
    /// and kicks off the 7-day silent auto-update for local datasets.
    /// </summary>
    [Regeneration(RegenerationOption.Manual)]
    public class App : IExternalApplication
    {
        private const string TabName = "CCorp Tools";

        internal static UpdateInfo PendingUpdate;
        private ControlledApplication _controlledApp;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // Create tab if it doesn't exist (shared with future CCorp plugins)
                try { application.CreateRibbonTab(TabName); }
                catch { /* tab already exists from another CCorp plugin */ }

                // ── Wind Analysis panel ───────────────────────────────────────
                RibbonPanel windPanel = application.CreateRibbonPanel(TabName, "Wind Analysis");

                AddLargeButton(windPanel,
                    name:      "Wind\nCalculator",
                    className: "WindCalc.Commands.WindCalcCommand",
                    tooltip:   "Fetch ASCE 7-22 wind data for this project's address,\n" +
                               "read roof geometry, and write results to Revit\n" +
                               "shared parameters and a Wind Analysis sheet.",
                    iconFile:  "WindCalc.png");

                windPanel.AddSeparator();

                AddLargeButton(windPanel,
                    name:      "Update\nStruct Notes",
                    className: "WindCalc.Commands.UpdateStructuralNotesCommand",
                    tooltip:   "Update all code-edition references on the S-001\n" +
                               "Structural Notes sheet. Prompts for target edition\n" +
                               "(FBC 8th / 9th) and replaces FBC, NEC, NDS, FFPC\n" +
                               "and SDPWS citations across all text notes on the sheet.",
                    iconFile:  "StructNotes.png");

                windPanel.AddSeparator();

                AddLargeButton(windPanel,
                    name:      "Place Wind\nCalc",
                    className: "WindCalc.Commands.InsertDataCommand",
                    tooltip:   "Place the DESIGN DATA drafting view onto the active sheet.\n" +
                               "Open a sheet first, then click to position the view.\n" +
                               "Run Wind Calculator first to generate the view.",
                    iconFile:  "PlaceWindCalc.png");

                windPanel.AddSeparator();

                AddLargeButton(windPanel,
                    name:      "Setup\nData",
                    className: "WindCalc.Commands.SetupLocalDataCommand",
                    tooltip:   "Download FEMA flood zone shapefiles and PCPAO parcel\n" +
                               "CSV data for offline lookups. Run once after installation\n" +
                               "or to force an immediate refresh (~600 MB download).\n" +
                               "Data also auto-refreshes every 7 days at startup.",
                    iconFile:  "Setup.png");

                // ── Labeling panel ────────────────────────────────────────────
                RibbonPanel labelPanel = application.CreateRibbonPanel(TabName, "Labeling");

                AddLargeButton(labelPanel,
                    name:      "Label\nWalls",
                    className: "WindCalc.Commands.WallLabelCommand",
                    tooltip:   "Click walls one at a time in order, then press ESC.\n" +
                               "Each wall receives a text label combining a true-north\n" +
                               "cardinal direction and a sequence number (e.g. N-1, SSW-3).\n" +
                               "Used to number walls for mechanical energy calculations.",
                    iconFile:  "WallLabel.png");

                // ── 7-day silent auto-update ──────────────────────────────────
                // Fire-and-forget: runs entirely on a background thread.
                // No Revit API access — only file I/O and HTTP downloads.
                // Errors are swallowed so a network outage never blocks startup.
                TriggerAutoUpdate();

                // ── Plugin version check (GitHub Releases) ────────────────────
                // Prompt at shutdown only — ApplicationClosing fires while the
                // main window is still alive; OnShutdown is too late for UI.
                // ApplicationClosingEventArgs is internal in Revit 2025+, so we
                // subscribe via a lambda whose args type is inferred — no need
                // to name the inaccessible type. Unsubscription is skipped: the
                // ControlledApplication is being torn down with the process.
                _controlledApp = application.ControlledApplication;
                SubscribeApplicationClosing(_controlledApp);
                TriggerVersionCheck();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Wind Calculator \u2013 Startup Error", ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        // ── Shutdown-time update prompt ───────────────────────────────────────

        // ApplicationClosingEventArgs is internal in Revit 2025+, which makes
        // the event itself inaccessible to the C# compiler. Subscribe via
        // reflection + Expression.Lambda so we never reference the type by name.
        private static void SubscribeApplicationClosing(ControlledApplication ctrl)
        {
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
                var td = new TaskDialog("WindCalc update available")
                {
                    MainInstruction = $"Version {info.Version} is available (you have {UpdateChecker.CurrentVersion}).",
                    MainContent     = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                                        ? "Would you like to run the installer now? Revit will finish closing first."
                                        : info.ReleaseNotes + "\n\nRun the installer now? Revit will finish closing first.",
                    CommonButtons   = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton   = TaskDialogResult.Yes,
                };
                if (td.Show() == TaskDialogResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(info.LocalInstallerPath)
                    {
                        UseShellExecute = true,
                    });
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
                catch { /* offline, rate-limited, etc. — ignore */ }
            });
        }

        // ── Auto-update ───────────────────────────────────────────────────────

        private static void TriggerAutoUpdate()
        {
            // Read config outside the task (main thread, no concurrency issues).
            WindCalcConfig config;
            try   { config = WindCalcConfig.LoadOrDefault(); }
            catch { return; }

            Task.Run(async () =>
            {
                try
                {
                    var downloader = new LocalDataDownloader(config.LocalDataFolder);
                    await downloader.RunAutoUpdateAsync();
                }
                catch
                {
                    // Silently swallow — startup should never fail due to a download issue.
                }
            });
        }

        // ── Ribbon helpers ────────────────────────────────────────────────────

        private static string AssemblyPath =>
            Assembly.GetExecutingAssembly().Location;

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
