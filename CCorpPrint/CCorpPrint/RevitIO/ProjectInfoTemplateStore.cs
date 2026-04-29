using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using Autodesk.Revit.DB;
using CCorpPrint.Models;

namespace CCorpPrint.RevitIO
{
    /// <summary>
    /// Reads and writes CCorpPrint per-project naming templates to a single
    /// shared parameter on ProjectInformation, so they travel with the .rvt.
    /// </summary>
    public class ProjectInfoTemplateStore
    {
        private readonly Document _doc;

        public ProjectInfoTemplateStore(Document doc) { _doc = doc; }

        public List<NamingTemplate> Load()
        {
            var p = _doc.ProjectInformation.LookupParameter(SharedParamWriter.ParamName);
            if (p == null || !p.HasValue) return new List<NamingTemplate>();

            var json = p.AsString();
            if (string.IsNullOrWhiteSpace(json)) return new List<NamingTemplate>();

            try
            {
                var ser = new DataContractJsonSerializer(typeof(List<NamingTemplate>));
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
                return (List<NamingTemplate>)ser.ReadObject(ms) ?? new List<NamingTemplate>();
            }
            catch
            {
                return new List<NamingTemplate>();
            }
        }

        /// <summary>
        /// Persist templates to ProjectInformation. Wraps everything in its own
        /// Transaction so the caller doesn't need to open one.
        /// </summary>
        public void Save(List<NamingTemplate> templates)
        {
            using var tx = new Transaction(_doc, "CCorpPrint: save naming templates");
            tx.Start();

            var writer = new SharedParamWriter(_doc);
            var p = writer.EnsureParameter();
            if (p == null || p.IsReadOnly)
            {
                tx.RollBack();
                return;
            }

            var ser = new DataContractJsonSerializer(typeof(List<NamingTemplate>));
            using var ms = new MemoryStream();
            ser.WriteObject(ms, templates ?? new List<NamingTemplate>());
            var json = Encoding.UTF8.GetString(ms.ToArray());
            p.Set(json);

            tx.Commit();
        }

        /// <summary>
        /// Returns a merged list: project-stored templates win on name conflict.
        /// </summary>
        public List<NamingTemplate> MergeWithUser(List<NamingTemplate> userTemplates)
        {
            var project = Load();
            var merged = new List<NamingTemplate>(project);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in project) seen.Add(t.Name ?? "");
            foreach (var t in userTemplates ?? new List<NamingTemplate>())
            {
                if (seen.Add(t.Name ?? "")) merged.Add(t);
            }
            return merged;
        }
    }
}
