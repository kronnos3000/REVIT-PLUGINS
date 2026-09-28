using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Newtonsoft.Json.Linq;

namespace RevitBridge.Scripting
{
    /// <summary>
    /// Compiles execute_csharp bodies with Roslyn (CSharpCompilation, not CSharpScript) into an
    /// in-memory assembly loaded into a collectible AssemblyLoadContext, so repeated scripts
    /// don't leak. Compiled scripts are cached by SHA-256 of the full source (LRU, unloaded on
    /// eviction). Compile errors are mapped back to the user's line/column.
    /// </summary>
    internal static class ScriptRunner
    {
        private const int CacheSize = 32;
        private const string FileName = "script.cs";

        private static readonly string[] DefaultUsings =
        {
            "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Text",
            "Autodesk.Revit.DB", "Autodesk.Revit.DB.Architecture", "Autodesk.Revit.DB.Structure",
            "Autodesk.Revit.DB.Structure.StructuralSections", "Autodesk.Revit.UI",
            "Newtonsoft.Json.Linq", "RevitBridge.Scripting",
        };

        private sealed class Entry
        {
            public string Hash;
            public ScriptLoadContext Alc;
            public MethodInfo Run;
            public int LineOffset;
        }

        private static readonly LinkedList<Entry> Lru = new LinkedList<Entry>();
        private static List<MetadataReference> _refs;
        private static int _compiles, _cacheHits, _unloaded;

        public static JObject Stats() => new JObject
        {
            ["cached"] = Lru.Count, ["compiles"] = _compiles, ["cache_hits"] = _cacheHits, ["unloaded"] = _unloaded,
        };

        public static MethodInfo Get(string body, IEnumerable<string> extraUsings, out int lineOffset, out bool cacheHit)
        {
            var usings = DefaultUsings.Concat(extraUsings ?? Enumerable.Empty<string>()).Distinct().ToList();
            var header = new StringBuilder();
            foreach (var u in usings) header.Append("using ").Append(u).Append(";\n");
            header.Append("public static class __Script {\npublic static object Run(ScriptContext ctx) {\n");
            var offset = header.ToString().Count(ch => ch == '\n');
            // Trailing return so bodies that only do work still compile (unreachable-code warning at most).
            var source = header + body + "\nreturn null;\n}\n}\n";
            var hash = Sha256(source);

            var hit = Lru.FirstOrDefault(e => e.Hash == hash);
            if (hit != null)
            {
                Lru.Remove(hit); Lru.AddFirst(hit);
                _cacheHits++;
                lineOffset = hit.LineOffset; cacheHit = true;
                return hit.Run;
            }

            var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: FileName, encoding: Encoding.UTF8);
            var compilation = CSharpCompilation.Create(
                "__RevitBridgeScript_" + hash.Substring(0, 12),
                new[] { tree },
                References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Debug,
                    nullableContextOptions: NullableContextOptions.Disable,
                    allowUnsafe: false));

            using (var pe = new MemoryStream())
            using (var pdb = new MemoryStream())
            {
                var result = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
                if (!result.Success)
                {
                    var errs = new JArray(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(50).Select(d =>
                    {
                        var span = d.Location.GetLineSpan();
                        return new JObject
                        {
                            ["line"] = span.StartLinePosition.Line + 1 - offset,
                            ["column"] = span.StartLinePosition.Character + 1,
                            ["id"] = d.Id,
                            ["message"] = d.GetMessage(),
                        };
                    }));
                    var first = (JObject)errs.FirstOrDefault();
                    throw new BridgeException("COMPILE_ERROR",
                        $"{errs.Count} compile error(s)" + (first != null ? $"; first at line {first["line"]}, col {first["column"]}: {first["message"]}" : "."),
                        "Lines/columns are relative to your code body (line 1 = first line you sent).",
                        new JObject { ["diagnostics"] = errs });
                }
                pe.Position = 0; pdb.Position = 0;
                var alc = new ScriptLoadContext();
                var asm = alc.LoadFromStream(pe, pdb);
                var run = asm.GetType("__Script").GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                _compiles++;
                Lru.AddFirst(new Entry { Hash = hash, Alc = alc, Run = run, LineOffset = offset });
                while (Lru.Count > CacheSize)
                {
                    var old = Lru.Last.Value;
                    Lru.RemoveLast();
                    old.Run = null;
                    old.Alc.Unload();
                    _unloaded++;
                }
                lineOffset = offset; cacheHit = false;
                return run;
            }
        }

        /// <summary>Maps an exception thrown inside the script to the user's line number, if known.</summary>
        public static int? ScriptLine(Exception ex, int lineOffset)
        {
            var st = new System.Diagnostics.StackTrace(ex, true);
            foreach (var f in st.GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>())
            {
                if (f.GetMethod()?.DeclaringType?.Name == "__Script" && f.GetFileLineNumber() > 0)
                    return f.GetFileLineNumber() - lineOffset;
            }
            return null;
        }

        private static List<MetadataReference> References()
        {
            if (_refs != null) return _refs;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // The shared framework (System.*, netstandard) Revit runs on.
            var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (!string.IsNullOrEmpty(tpa))
                foreach (var p in tpa.Split(Path.PathSeparator))
                {
                    var n = Path.GetFileName(p);
                    if (n.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || n.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase)
                        || n.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase)
                        || n.StartsWith("Microsoft.Win32.", StringComparison.OrdinalIgnoreCase))
                        paths.Add(p);
                }
            var rt = Path.GetDirectoryName(typeof(object).Assembly.Location);
            foreach (var n in new[] { "System.Runtime.dll", "netstandard.dll", "System.Collections.dll", "System.Linq.dll", "System.Private.CoreLib.dll" })
            {
                var p = Path.Combine(rt, n);
                if (File.Exists(p)) paths.Add(p);
            }
            // Revit API, Newtonsoft and the bridge itself (for ScriptContext).
            foreach (var a in new[] { typeof(Autodesk.Revit.DB.Document).Assembly, typeof(Autodesk.Revit.UI.UIApplication).Assembly,
                                      typeof(JObject).Assembly, typeof(ScriptContext).Assembly })
                if (!string.IsNullOrEmpty(a.Location)) paths.Add(a.Location);

            _refs = new List<MetadataReference>();
            foreach (var p in paths)
            {
                try { _refs.Add(MetadataReference.CreateFromFile(p)); } catch { /* native or unreadable */ }
            }
            return _refs;
        }

        private static string Sha256(string s)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Collectible context. Every dependency (RevitAPI, Newtonsoft, RevitBridge) resolves to the
        /// instance already loaded in the process, whichever load context Revit put it in, so types
        /// like ScriptContext are identical on both sides.
        /// </summary>
        private sealed class ScriptLoadContext : AssemblyLoadContext
        {
            public ScriptLoadContext() : base("RevitBridgeScript", isCollectible: true) { }

            protected override Assembly Load(AssemblyName name)
            {
                var own = typeof(ScriptContext).Assembly;
                if (string.Equals(name.Name, own.GetName().Name, StringComparison.OrdinalIgnoreCase)) return own;
                var loaded = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic && string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(a => a.GetName().Version)
                    .FirstOrDefault();
                return loaded; // null → default context
            }
        }
    }
}
