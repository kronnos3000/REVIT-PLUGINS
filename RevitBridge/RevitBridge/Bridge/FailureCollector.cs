using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitBridge.Services;

namespace RevitBridge.Bridge
{
    /// <summary>
    /// Attached to every bridge Transaction. Records warnings (optionally deleting them so
    /// no popup appears) and turns any error-severity failure into a rollback, reporting
    /// the failure text and element ids back through the envelope.
    /// </summary>
    public sealed class FailureCollector : IFailuresPreprocessor
    {
        private readonly bool _dismissWarnings;
        public List<JObject> Warnings { get; } = new List<JObject>();
        public List<JObject> Errors { get; } = new List<JObject>();

        public FailureCollector(bool dismissWarnings) { _dismissWarnings = dismissWarnings; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
        {
            bool hasError = false;
            foreach (var f in fa.GetFailureMessages())
            {
                var entry = new JObject
                {
                    ["message"] = f.GetDescriptionText(),
                    ["element_ids"] = new JArray(f.GetFailingElementIds().Select(i => i.AsLong())),
                };
                var sev = f.GetSeverity();
                if (sev == FailureSeverity.Warning)
                {
                    entry["dismissed"] = _dismissWarnings;
                    Warnings.Add(entry);
                    if (_dismissWarnings) fa.DeleteWarning(f);
                }
                else
                {
                    entry["severity"] = sev.ToString();
                    Errors.Add(entry);
                    hasError = true;
                }
            }
            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
