using System;
using System.IO;
using CCorpPrint.Models;

namespace CCorpPrint.Services
{
    /// <summary>
    /// Append-only daily log at %AppData%\CCorpPrint\logs\yyyy-MM-dd.log.
    /// Thread-safe — print engine runs on UI thread but background tasks
    /// may still write here.
    /// </summary>
    public class Logger
    {
        private readonly object _lock = new object();
        private readonly bool _enabled;
        private readonly string _dir;

        public string LogFilePath => Path.Combine(_dir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");

        public Logger(bool enabled)
        {
            _enabled = enabled;
            _dir = Path.Combine(PrintConfig.ConfigDirectory, "logs");
        }

        public string BeginJob(OutputMode mode)
        {
            var jobId = Guid.NewGuid().ToString("N").Substring(0, 8);
            Info(jobId, "*", "Begin job mode=" + mode);
            return jobId;
        }

        public void Info(string jobId,  string sheetNumber, string message) => Write("INFO",  jobId, sheetNumber, message);
        public void Warn(string jobId,  string sheetNumber, string message) => Write("WARN",  jobId, sheetNumber, message);
        public void Error(string jobId, string sheetNumber, string message) => Write("ERROR", jobId, sheetNumber, message);

        private void Write(string level, string jobId, string sheetNumber, string message)
        {
            if (!_enabled) return;
            try
            {
                Directory.CreateDirectory(_dir);
                var line = $"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff} | {level,-5} | {jobId} | {sheetNumber} | {message}";
                lock (_lock)
                {
                    File.AppendAllText(LogFilePath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never throw into the print path.
            }
        }
    }
}
