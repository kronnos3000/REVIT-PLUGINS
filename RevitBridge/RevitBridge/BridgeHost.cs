using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitBridge.Bridge;
using RevitBridge.Services;

namespace RevitBridge
{
    /// <summary>
    /// Owns the bridge's process-wide state: the request queue, the ExternalEvent, the pipe
    /// server and the session file. Start/Stop are driven by App startup and the ribbon toggle.
    /// </summary>
    internal static class BridgeHost
    {
        private static readonly ConcurrentQueue<BridgeRequest> Queue = new ConcurrentQueue<BridgeRequest>();
        private static ExternalEvent _event;
        private static PipeServer _server;

        public static int Pid { get; } = Process.GetCurrentProcess().Id;
        public static string PipeName { get; } = BridgePaths.PipeName(Pid);
        public static string RevitYear { get; set; }
        public static string VersionBuild { get; set; }
        public static DateTime StartedUtc { get; private set; }
        public static bool IsRunning => _server != null;

        public static string SessionFile => Path.Combine(BridgePaths.SessionsDir, Pid + ".json");

        /// <summary>Must be called on the main thread (ExternalEvent.Create requires an API context).</summary>
        public static void Initialize()
        {
            if (_event == null) _event = ExternalEvent.Create(new BridgeHandler(Queue));
        }

        public static void Start()
        {
            if (_server != null) return;
            _server = new PipeServer(PipeName, Queue, _event);
            _server.Start();
            StartedUtc = DateTime.UtcNow;
            WriteSessionFile();
        }

        public static void Stop()
        {
            if (_server == null) return;
            _server.Dispose();
            _server = null;
            DeleteSessionFile();
        }

        private static void WriteSessionFile()
        {
            try
            {
                Directory.CreateDirectory(BridgePaths.SessionsDir);
                var o = new JObject
                {
                    ["pid"] = Pid,
                    ["revitYear"] = RevitYear,
                    ["versionBuild"] = VersionBuild,
                    ["pipe"] = PipeName,
                    ["startedUtc"] = StartedUtc.ToString("o"),
                    ["bridgeVersion"] = typeof(BridgeHost).Assembly.GetName().Version.ToString(),
                };
                File.WriteAllText(SessionFile, o.ToString());
            }
            catch (Exception ex) { Logger.Error("Session file write failed: " + ex.Message); }
        }

        public static void DeleteSessionFile()
        {
            try { if (File.Exists(SessionFile)) File.Delete(SessionFile); } catch { }
        }
    }
}
