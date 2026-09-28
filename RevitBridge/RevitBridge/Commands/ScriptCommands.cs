using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using RevitBridge.Bridge;
using RevitBridge.Scripting;

namespace RevitBridge.Commands
{
    /// <summary>execute_csharp — the escape hatch. Full Revit API access from a C# method body.</summary>
    internal static class ScriptCommands
    {
        /// <summary>
        /// code: C# statements forming the body of <c>object Run(ScriptContext ctx)</c>.
        /// mode: "read" (default; no transaction, any model change throws) or "write" (runs inside
        /// the per-call TransactionGroup AND an open Transaction; one Ctrl+Z; honours dry_run).
        /// usings: extra namespaces. The pipe-side timeout_s cannot interrupt a running script.
        /// </summary>
        [BridgeCommand("execute_csharp", CommandKind.Read, WriteIf = "mode=write")]
        public static object ExecuteCSharp(CommandContext ctx)
        {
            var code = ctx.StrReq("code");
            var mode = ctx.Str("mode", "read").ToLowerInvariant();
            if (mode != "read" && mode != "write") throw new BridgeException("BAD_PARAMS", "mode must be 'read' or 'write'.");
            var usings = ctx.StrList("usings");

            var swCompile = Stopwatch.StartNew();
            var run = ScriptRunner.Get(code, usings, out var lineOffset, out var cacheHit);
            swCompile.Stop();

            var doc = ctx.DocOrNull;
            var sctx = new ScriptContext(ctx.UIApp, doc, doc != null && ctx.IsActiveDoc ? ctx.UIApp.ActiveUIDocument : null, mode);
            var swRun = Stopwatch.StartNew();
            object value;
            try
            {
                if (mode == "write")
                {
                    if (doc == null) throw new BridgeException("NO_DOCUMENT", "write mode needs a document.");
                    value = ctx.Tx("Claude: execute_csharp", () => run.Invoke(null, new object[] { sctx }));
                }
                else value = run.Invoke(null, new object[] { sctx });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                var inner = tie.InnerException;
                if (inner is BridgeException) throw inner;
                var line = ScriptRunner.ScriptLine(inner, lineOffset);
                var isReadOnlyViolation = inner is Autodesk.Revit.Exceptions.ModificationOutsideTransactionException;
                throw new BridgeException("SCRIPT_ERROR",
                    $"{inner.GetType().Name}: {inner.Message}" + (line.HasValue ? $" (script line {line})" : ""),
                    isReadOnlyViolation ? "The script modified the model in mode='read'. Use mode='write'." : null,
                    new JObject { ["log"] = new JArray(sctx.LogLines), ["line"] = line });
            }
            swRun.Stop();

            JToken serialized;
            try { serialized = Json.ToToken(value); }
            catch (Exception ex) { serialized = $"<unserializable {value?.GetType().Name}: {ex.Message}> {value}"; }

            var o = new JObject
            {
                ["value"] = serialized,
                ["log"] = new JArray(sctx.LogLines),
                ["mode"] = mode,
                ["compile_ms"] = swCompile.ElapsedMilliseconds,
                ["run_ms"] = swRun.ElapsedMilliseconds,
                ["cache_hit"] = cacheHit,
            };
            if (sctx.Result.Extra.Count > 0) o["outputs"] = sctx.Result.Extra;
            return o;
        }

        [BridgeCommand("script_stats")]
        public static object ScriptStats(CommandContext ctx)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var stats = ScriptRunner.Stats();
            stats["managed_mb"] = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1);
            stats["private_mb"] = Math.Round(Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0, 1);
            stats["script_assemblies_loaded"] = AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name.StartsWith("__RevitBridgeScript_"));
            return stats;
        }
    }
}
