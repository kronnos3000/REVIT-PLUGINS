using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitBridge.Services;

namespace RevitBridge.Bridge
{
    /// <summary>
    /// The only place bridge requests touch the Revit API. Runs on Revit's main thread via
    /// ExternalEvent, drains the queue, and wraps each call:
    ///   Write   → TransactionGroup "Claude: &lt;method&gt;" → Assimilate (one Ctrl+Z), or RollBack
    ///             on error / dry_run / size guard.
    ///   Read    → no transaction.
    ///   Session → no transaction (open/save/sync/close manage themselves).
    /// </summary>
    public sealed class BridgeHandler : IExternalEventHandler
    {
        private const int ChangedListCap = 1000;
        private readonly ConcurrentQueue<BridgeRequest> _queue;

        public BridgeHandler(ConcurrentQueue<BridgeRequest> queue) { _queue = queue; }

        public string GetName() => "CCorp RevitBridge";

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var req))
            {
                if (!req.TryStart()) continue; // pipe side already answered REVIT_BUSY
                JObject reply;
                try { reply = Run(app, req); }
                catch (Exception ex)
                {
                    reply = Envelope.Failure(req.Id, req.Method, "INTERNAL", ex.Message);
                    Logger.Error($"{req.Method}: unhandled {ex}");
                }
                req.Completion.TrySetResult(reply);
            }
        }

        private JObject Run(UIApplication app, BridgeRequest req)
        {
            var sw = Stopwatch.StartNew();
            var ctx = new CommandContext(app, req.Method, req.Params);
            JObject env;
            string txName = null;
            var kind = CommandKind.Read;
            var changed = new ChangeTracker();

            if (!CommandRouter.TryGet(req.Method, out var def))
            {
                env = Envelope.Failure(req.Id, req.Method, "UNKNOWN_METHOD", $"Unknown method '{req.Method}'.",
                    "Known: " + string.Join(", ", CommandRouter.All.Select(d => d.Name)));
                return Finish(env, ctx, changed, null, sw, req);
            }

            try
            {
                // Pre-flight size guard on any id list in the params.
                foreach (var prop in req.Params.Properties())
                    if (prop.Value is JArray a && (prop.Name == "ids" || prop.Name.EndsWith("_ids")))
                        ctx.GuardSize(a.Count, req.Method);

                object result;
                kind = def.KindFor(req.Params);
                if (kind == CommandKind.Write)
                {
                    var doc = ctx.Doc;
                    if (doc.IsReadOnly)
                        throw new BridgeException("READ_ONLY", $"'{doc.Title}' is read-only.");
                    txName = "Claude: " + req.Method;
                    result = RunInGroup(app, doc, txName, def, ctx, changed);
                }
                else
                {
                    result = def.Run(ctx);
                }

                env = Envelope.Success(req.Id, req.Method, Json.ToToken(result));
                if (kind == CommandKind.Write && ctx.DryRun) env["dry_run"] = true;
            }
            catch (BridgeException bex)
            {
                env = Envelope.Failure(req.Id, req.Method, bex.Code, bex.Message, bex.Hint,
                    bex.Data2 != null ? Json.ToToken(bex.Data2) : null);
                if (bex.Code != "CONFIRM_REQUIRED") changed.Clear(); // rolled back: nothing changed
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException rex)
            {
                env = Envelope.Failure(req.Id, req.Method, "REVIT_ERROR", rex.Message + TopFrame(rex));
                changed.Clear();
                Logger.Error($"{req.Method}: {rex}");
            }
            catch (Exception ex)
            {
                env = Envelope.Failure(req.Id, req.Method, "ERROR", ex.GetType().Name + ": " + ex.Message + TopFrame(ex));
                changed.Clear();
                Logger.Error($"{req.Method}: {ex}");
            }

            env["tx_name"] = txName;
            return Finish(env, ctx, changed, def, sw, req);
        }

        private static object RunInGroup(UIApplication app, Document doc, string txName, CommandDef def,
                                         CommandContext ctx, ChangeTracker changed)
        {
            EventHandler<DocumentChangedEventArgs> onChanged = (s, e) =>
            {
                if (e.GetDocument().Equals(doc)) changed.Add(e);
            };
            app.Application.DocumentChanged += onChanged;
            var tg = new TransactionGroup(doc, txName);
            try
            {
                tg.Start();
                var result = def.Run(ctx);
                app.Application.DocumentChanged -= onChanged;

                if (ctx.Failures.Errors.Count > 0)
                {
                    tg.RollBack();
                    throw new BridgeException("REVIT_ERROR", "Revit reported errors; the call was rolled back.");
                }
                var total = changed.Total;
                if (total > CommandContext.SizeGuardLimit && !ctx.Confirm && !ctx.DryRun)
                {
                    tg.RollBack();
                    throw new BridgeException("SIZE_GUARD",
                        $"The call touched {total} elements (limit {CommandContext.SizeGuardLimit} without confirm=true). It was rolled back.",
                        "Re-run with dry_run=true to inspect, then confirm=true.");
                }
                if (ctx.DryRun) tg.RollBack();
                else tg.Assimilate();
                return result;
            }
            catch
            {
                app.Application.DocumentChanged -= onChanged;
                if (tg.HasStarted() && !tg.HasEnded()) tg.RollBack();
                throw;
            }
            finally
            {
                tg.Dispose();
            }
        }

        private static JObject Finish(JObject env, CommandContext ctx, ChangeTracker changed, CommandDef def,
                                      Stopwatch sw, BridgeRequest req)
        {
            env["warnings"] = new JArray(ctx.Failures.Warnings.Concat(ctx.Notes.Select(n => (JToken)new JObject { ["message"] = n })));
            env["errors"] = new JArray(ctx.Failures.Errors);
            env["changed"] = changed.ToJson(ChangedListCap);
            Document doc = null;
            try { doc = ctx.DocOrNull; } catch { }
            env["doc"] = CommandContext.DocInfo(doc);
            sw.Stop();
            env["elapsed_ms"] = sw.ElapsedMilliseconds;

            var err = env["error"] as JObject;
            Logger.Call(req.Method, doc?.Title, ParamsHash(req.Params), (bool)env["ok"], (string)err?["code"],
                changed.Created.Count, changed.Modified.Count, changed.Deleted.Count, sw.ElapsedMilliseconds, ctx.DryRun);
            return env;
        }

        /// <summary>" (at Type.Method)" for the innermost bridge frame, so errors are diagnosable from the envelope.</summary>
        private static string TopFrame(Exception ex)
        {
            var frames = new System.Diagnostics.StackTrace(ex, false).GetFrames();
            var f = frames?.FirstOrDefault(fr => fr.GetMethod()?.DeclaringType?.Namespace?.StartsWith("RevitBridge") == true);
            var m = f?.GetMethod();
            return m == null ? "" : $" (at {m.DeclaringType?.Name}.{m.Name})";
        }

        private static string ParamsHash(JObject p)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(p.ToString(Formatting.None)));
                return BitConverter.ToString(bytes, 0, 6).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>Accumulates DocumentChanged ids across all transactions of one call.</summary>
        private sealed class ChangeTracker
        {
            public readonly HashSet<long> Created = new HashSet<long>();
            public readonly HashSet<long> Modified = new HashSet<long>();
            public readonly HashSet<long> Deleted = new HashSet<long>();

            public int Total => Created.Count + Modified.Count + Deleted.Count;

            public void Add(DocumentChangedEventArgs e)
            {
                foreach (var id in e.GetAddedElementIds()) Created.Add(id.AsLong());
                foreach (var id in e.GetModifiedElementIds()) { var v = id.AsLong(); if (!Created.Contains(v)) Modified.Add(v); }
                foreach (var id in e.GetDeletedElementIds())
                {
                    var v = id.AsLong();
                    if (!Created.Remove(v)) Deleted.Add(v); // created then deleted in the same call = net nothing
                    Modified.Remove(v);
                }
            }

            public void Clear() { Created.Clear(); Modified.Clear(); Deleted.Clear(); }

            public JObject ToJson(int cap)
            {
                JArray Take(HashSet<long> s) => new JArray(s.OrderBy(x => x).Take(cap));
                var o = new JObject
                {
                    ["created"] = Take(Created), ["modified"] = Take(Modified), ["deleted"] = Take(Deleted),
                    ["counts"] = new JObject { ["created"] = Created.Count, ["modified"] = Modified.Count, ["deleted"] = Deleted.Count },
                };
                if (Created.Count > cap || Modified.Count > cap || Deleted.Count > cap) o["truncated"] = true;
                return o;
            }
        }
    }
}
