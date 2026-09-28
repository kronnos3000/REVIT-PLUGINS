using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RevitBridge.Services
{
    /// <summary>
    /// Append-only daily audit log at %APPDATA%\CCorp\RevitBridge\logs\yyyy-MM-dd.log.
    /// One line per bridge call (time | level | method | doc | params hash | ok/err | changed counts),
    /// plus free-form Info/Error lines for startup and pipe events. Thread-safe: the pipe
    /// server writes from background threads, commands from Revit's main thread.
    /// Also keeps the last 20 calls in memory for the Status dialog.
    /// </summary>
    internal static class Logger
    {
        private static readonly object Lock = new object();
        private static readonly LinkedList<string> Recent = new LinkedList<string>();
        private const int RecentMax = 20;

        public static string LogFilePath => Path.Combine(BridgePaths.LogsDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");

        public static void Info(string message)  => Write("INFO",  message);
        public static void Warn(string message)  => Write("WARN",  message);
        public static void Error(string message) => Write("ERROR", message);

        public static void Call(string method, string doc, string paramsHash, bool ok, string errorCode,
                                int created, int modified, int deleted, long elapsedMs, bool dryRun)
        {
            var outcome = ok ? "ok" : "err:" + (errorCode ?? "?");
            var line = $"{method} | doc={doc ?? "-"} | params={paramsHash} | {outcome}" +
                       $" | +{created} ~{modified} -{deleted}{(dryRun ? " (dry_run)" : "")} | {elapsedMs}ms";
            Write("CALL", line);
            lock (Lock)
            {
                Recent.AddFirst($"{DateTime.Now:HH:mm:ss}  {method}  {outcome}  +{created} ~{modified} -{deleted}{(dryRun ? " dry" : "")}  {elapsedMs}ms");
                while (Recent.Count > RecentMax) Recent.RemoveLast();
            }
        }

        public static IReadOnlyList<string> RecentCalls()
        {
            lock (Lock) return Recent.ToList();
        }

        private static void Write(string level, string message)
        {
            try
            {
                Directory.CreateDirectory(BridgePaths.LogsDir);
                var line = $"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff} | {level,-5} | {message}";
                lock (Lock)
                {
                    File.AppendAllText(LogFilePath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never throw into the bridge path.
            }
        }
    }
}
