using System;
using System.Collections.Generic;

namespace CCorpPrint.Models
{
    public class ResultSummary
    {
        public string JobId { get; set; }
        public OutputMode Mode { get; set; }
        public int Total { get; set; }
        public int Succeeded { get; set; }
        public List<SheetResult> Failed { get; set; } = new List<SheetResult>();
        public List<SheetResult> Skipped { get; set; } = new List<SheetResult>();
        public TimeSpan Duration { get; set; }
        public string OutputFolder { get; set; }
        public string LogFilePath { get; set; }
    }

    public class SheetResult
    {
        public string SheetNumber { get; set; }
        public string SheetName { get; set; }
        public string Reason { get; set; }
    }
}
