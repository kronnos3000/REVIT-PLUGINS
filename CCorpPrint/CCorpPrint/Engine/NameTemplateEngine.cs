using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using CCorpPrint.Models;

namespace CCorpPrint.Engine
{
    /// <summary>
    /// One resolved filename plus the list of tokens that could not be resolved
    /// from the document (so callers can apply the missing-param policy and report).
    /// </summary>
    public class ResolvedName
    {
        public string FileName { get; set; }
        public IReadOnlyList<string> MissingTokens { get; set; }
    }

    /// <summary>
    /// Token-based filename engine.
    ///
    /// Syntax:
    ///   {ParamName}              looked up by display name (case-insensitive;
    ///                            spaces and underscores are interchangeable)
    ///   {ParamName:format}       optional .NET composite format spec
    ///                            (e.g. {Today:yyyy-MM-dd}, {Index:000})
    ///   {{ }}                    literal braces
    ///
    /// Resolution order (first hit wins):
    ///   1. Built-ins (Today, Now, Year, Username, ProjectFileName,
    ///      SheetCount, Index, JobId)
    ///   2. Sheet parameters (built-in + shared/project bound to OST_Sheets)
    ///   3. ProjectInformation parameters
    ///
    /// Missing-token behaviour follows PrintConfig.MissingParamPolicy.
    /// All output is run through Sanitize() before being returned.
    /// </summary>
    public class NameTemplateEngine
    {
        private static readonly Regex TokenRegex = new Regex(
            @"\{(?<name>[A-Za-z0-9][A-Za-z0-9 _\-]*?)(?::(?<fmt>[^}]+))?\}",
            RegexOptions.Compiled);

        // Sentinel strings used to swap out literal {{ and }} during parsing.
        // Use ASCII control characters so they cannot appear in any user template.
        private const string OpenEscape  = "OPEN";
        private const string CloseEscape = "CLOSE";

        private static readonly char[] InvalidChars = new[]
        {
            '\\', '/', ':', '*', '?', '"', '<', '>', '|'
        };

        private static readonly HashSet<string> ReservedNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        private readonly PrintConfig _cfg;
        private readonly Document _doc;
        private readonly Lazy<IReadOnlyList<TokenInfo>> _tokens;

        public NameTemplateEngine(PrintConfig cfg, Document doc)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _tokens = new Lazy<IReadOnlyList<TokenInfo>>(() => new TokenDiscovery(_doc).Discover());
        }

        public IReadOnlyList<TokenInfo> AvailableTokens() => _tokens.Value;

        public ResolvedName Resolve(string template, ViewSheet sheet, int index, int total, string jobId)
        {
            if (string.IsNullOrEmpty(template))
                return new ResolvedName { FileName = "", MissingTokens = Array.Empty<string>() };

            var missing = new List<string>();
            string raw = SubstituteTokens(template, sheet, index, total, jobId, missing);
            string sanitized = Sanitize(raw);
            return new ResolvedName { FileName = sanitized, MissingTokens = missing };
        }

        public IReadOnlyList<ResolvedName> Preview(string template, IList<ViewSheet> sheets)
        {
            var jobId = "preview";
            var list = new List<ResolvedName>(sheets.Count);
            for (int i = 0; i < sheets.Count; i++)
                list.Add(Resolve(template, sheets[i], i + 1, sheets.Count, jobId));
            return list;
        }

        // ── core substitution ────────────────────────────────────────────────

        private string SubstituteTokens(
            string template, ViewSheet sheet, int index, int total, string jobId,
            List<string> missing)
        {
            string work = template
                .Replace("{{", OpenEscape)
                .Replace("}}", CloseEscape);

            string substituted = TokenRegex.Replace(work, m =>
            {
                var name = m.Groups["name"].Value.Trim();
                var fmt  = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : null;

                if (TryResolveToken(name, fmt, sheet, index, total, jobId, out var value))
                    return value ?? "";

                missing.Add(name);
                switch (_cfg.MissingParamPolicy)
                {
                    case MissingParamPolicy.LiteralToken:
                    case MissingParamPolicy.Error:
                        return m.Value;
                    case MissingParamPolicy.BlankOut:
                    default:
                        return "";
                }
            });

            return substituted.Replace(OpenEscape, "{").Replace(CloseEscape, "}");
        }

        private bool TryResolveToken(
            string name, string fmt,
            ViewSheet sheet, int index, int total, string jobId,
            out string value)
        {
            if (TryBuiltIn(name, fmt, sheet, index, total, jobId, out value)) return true;

            if (sheet != null)
            {
                var p = LookupByName(sheet, name);
                if (p != null && p.HasValue)
                {
                    value = ApplyFormat(ParameterAsObject(p), fmt);
                    return true;
                }
            }

            var pi = _doc.ProjectInformation;
            if (pi != null)
            {
                var p = LookupByName(pi, name);
                if (p != null && p.HasValue)
                {
                    value = ApplyFormat(ParameterAsObject(p), fmt);
                    return true;
                }
            }

            value = null;
            return false;
        }

        private bool TryBuiltIn(string name, string fmt,
            ViewSheet sheet, int index, int total, string jobId, out string value)
        {
            switch (name.ToLowerInvariant().Replace(" ", "").Replace("_", ""))
            {
                case "today":           value = ApplyFormat(DateTime.Today, fmt ?? "yyyy-MM-dd"); return true;
                case "now":             value = ApplyFormat(DateTime.Now,   fmt ?? "yyyy-MM-dd_HHmm"); return true;
                case "year":            value = ApplyFormat(DateTime.Now.Year, fmt); return true;
                case "username":        value = Environment.UserName; return true;
                case "projectfilename": value = ProjectFileName(); return true;
                case "sheetcount":      value = ApplyFormat(total, fmt); return true;
                case "index":           value = ApplyFormat(index, fmt ?? "0"); return true;
                case "jobid":           value = jobId ?? ""; return true;
                default: value = null; return false;
            }
        }

        private string ProjectFileName()
        {
            if (string.IsNullOrEmpty(_doc.PathName)) return _doc.Title ?? "";
            return Path.GetFileNameWithoutExtension(_doc.PathName);
        }

        private static Parameter LookupByName(Element e, string requested)
        {
            var direct = e.LookupParameter(requested);
            if (direct != null) return direct;

            string norm = Normalize(requested);
            foreach (Parameter p in e.Parameters)
            {
                if (p?.Definition == null) continue;
                if (Normalize(p.Definition.Name) == norm) return p;
            }
            return null;
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace(" ", "").Replace("_", "").ToLowerInvariant();
        }

        private static object ParameterAsObject(Parameter p)
        {
            switch (p.StorageType)
            {
                case StorageType.String:  return p.AsString() ?? "";
                case StorageType.Integer: return p.AsInteger();
                case StorageType.Double:
                    var s = p.AsValueString();
                    return string.IsNullOrEmpty(s) ? (object)p.AsDouble() : s;
                case StorageType.ElementId:
                    var id = p.AsElementId();
                    return id == null ? "" : id.AsLong().ToString();
                default: return p.AsValueString() ?? "";
            }
        }

        private static string ApplyFormat(object value, string fmt)
        {
            if (value == null) return "";
            if (string.IsNullOrEmpty(fmt)) return value.ToString();
            try
            {
                return string.Format(CultureInfo.InvariantCulture, "{0:" + fmt + "}", value);
            }
            catch
            {
                return value.ToString();
            }
        }

        // ── sanitizer ────────────────────────────────────────────────────────

        public string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw)
            {
                if (Array.IndexOf(InvalidChars, c) >= 0) sb.Append('_');
                else if (char.IsControl(c)) sb.Append('_');
                else if (_cfg.ReplaceWhitespaceWithUnderscore && char.IsWhiteSpace(c)) sb.Append('_');
                else sb.Append(c);
            }

            string s = Regex.Replace(sb.ToString(), "_+", "_");
            s = s.Trim('_', '.', ' ');

            int max = Math.Max(20, _cfg.MaxFilenameLength);
            if (s.Length > max) s = s.Substring(0, max).TrimEnd('_', '.', ' ');

            string nameOnly = Path.GetFileNameWithoutExtension(s);
            if (ReservedNames.Contains(nameOnly)) s = "_" + s;

            return s.Length == 0 ? "untitled" : s;
        }

        /// <summary>
        /// Disambiguates a list of desired filenames by suffixing _1, _2, ... when
        /// duplicates appear in the batch or already exist in <paramref name="folder"/>.
        /// </summary>
        public IReadOnlyList<string> Deduplicate(IList<string> names, string folder, string extension)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                foreach (var f in Directory.EnumerateFiles(folder, "*" + extension))
                    taken.Add(Path.GetFileNameWithoutExtension(f));
            }

            var result = new List<string>(names.Count);
            foreach (var raw in names)
            {
                var candidate = raw;
                int n = 1;
                while (taken.Contains(candidate))
                    candidate = raw + "_" + (n++);
                taken.Add(candidate);
                result.Add(candidate);
            }
            return result;
        }
    }
}
