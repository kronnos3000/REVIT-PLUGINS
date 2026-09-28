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
using RevitBridge.Bridge;
using RevitBridge.Services;

namespace RevitBridge
{
    /// <summary>
    /// Entry point for the RevitBridge add-in. Starts the named-pipe bridge that the
    /// revit-bridge MCP server talks to, and adds a "Claude Bridge" panel to the shared
    /// "CCorp Tools" ribbon tab.
    /// </summary>
    [Regeneration(RegenerationOption.Manual)]
    public class App : IExternalApplication
    {
        private const string TabName   = "CCorp Tools";
        private const string PanelName = "Claude Bridge";

        internal static UpdateInfo PendingUpdate;
        internal static PushButton ToggleButton;
        private ControlledApplication _controlledApp;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                BridgeHost.RevitYear = application.ControlledApplication.VersionNumber;
                BridgeHost.VersionBuild = application.ControlledApplication.VersionBuild;
                PipeServer.SetMainWindow(application.MainWindowHandle);

                // Tab is shared with WindCalc, CCorpPrint and any other CCorp addin.
                try { application.CreateRibbonTab(TabName); }
                catch { /* already exists */ }

                var existing = application.GetRibbonPanels(TabName);
                RibbonPanel panel = existing.FirstOrDefault(p => p.Name == PanelName)
                                    ?? application.CreateRibbonPanel(TabName, PanelName);

                if (panel.GetItems().Count == 0)
                {
                    ToggleButton = AddLargeButton(panel,
                        name:      "Bridge\nOn",
                        className: "RevitBridge.UI.ToggleBridgeCommand",
                        tooltip:   "Switch the Claude bridge on or off.\n" +
                                   "When on, the revit-bridge MCP server can read and edit\n" +
                                   "this Revit session over a local named pipe (current user only).",
                        iconFile:  "BridgeOn.png");
                    AddLargeButton(panel,
                        name:      "Bridge\nStatus",
                        className: "RevitBridge.UI.StatusCommand",
                        tooltip:   "Pipe name, process id, active document and the last 20 Claude calls.",
                        iconFile:  "BridgeStatus.png");
                    AddLargeButton(panel,
                        name:      "Open\nLog",
                        className: "RevitBridge.UI.OpenLogCommand",
                        tooltip:   "Open today's bridge audit log.",
                        iconFile:  "BridgeLog.png");
                }

                // ExternalEvent.Create needs a valid API context; OnStartup is one.
                BridgeHost.Initialize();
                BridgeHost.Start();
                Logger.Info($"RevitBridge {UpdateChecker.CurrentVersion} started in Revit {BridgeHost.RevitYear} ({BridgeHost.VersionBuild}), pid {BridgeHost.Pid}.");

                _controlledApp = application.ControlledApplication;
                SubscribeApplicationClosing(_controlledApp);
                TriggerVersionCheck();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.Error("Startup failed: " + ex);
                TaskDialog.Show("CCorp RevitBridge — Startup Error", ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            // No UI here (the main window is already gone); just release the pipe.
            try { BridgeHost.Stop(); } catch { }
            return Result.Succeeded;
        }

        internal static void RefreshToggle()
        {
            if (ToggleButton == null) return;
            var on = BridgeHost.IsRunning;
            ToggleButton.ItemText = on ? "Bridge\nOn" : "Bridge\nOff";
            var icon = on ? "BridgeOn.png" : "BridgeOff.png";
            ToggleButton.LargeImage = LoadImage(icon);
            ToggleButton.Image = LoadImage(icon, small: true);
        }

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
            BridgeHost.DeleteSessionFile();

            var info = PendingUpdate;
            if (info == null || string.IsNullOrEmpty(info.LocalInstallerPath) ||
                !File.Exists(info.LocalInstallerPath))
            {
                return;
            }

            try
            {
                var td = new TaskDialog("RevitBridge update available")
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

        private static PushButton AddLargeButton(RibbonPanel panel, string name,
            string className, string tooltip, string iconFile)
        {
            var data = new PushButtonData(
                className,
                name,
                AssemblyPath,
                className)
            {
                ToolTip    = tooltip,
                LargeImage = LoadImage(iconFile),
                Image      = LoadImage(iconFile, small: true)
            };
            return panel.AddItem(data) as PushButton;
        }

        private static BitmapImage LoadImage(string filename, bool small = false)
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
