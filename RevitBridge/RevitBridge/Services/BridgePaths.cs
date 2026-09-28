using System;
using System.IO;

namespace RevitBridge.Services
{
    /// <summary>All on-disk locations used by the bridge, rooted at %APPDATA%\CCorp\RevitBridge.</summary>
    internal static class BridgePaths
    {
        public const string PipePrefix = "ccorp-revitbridge-";

        public static string Root =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CCorp", "RevitBridge");

        public static string SessionsDir => Path.Combine(Root, "sessions");
        public static string LogsDir     => Path.Combine(Root, "logs");
        public static string CapturesDir => Path.Combine(Root, "captures");

        public static string PipeName(int pid) => PipePrefix + pid;
    }
}
