using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitBridge.Services;

namespace RevitBridge.Bridge
{
    /// <summary>
    /// Named-pipe front door: \\.\pipe\ccorp-revitbridge-&lt;pid&gt;, current user only.
    /// Each connection carries newline-delimited JSON requests {id, method, params} and gets
    /// one newline-delimited JSON reply per request. Requests are queued for the Revit main
    /// thread via ExternalEvent; this class never touches the Revit API.
    /// </summary>
    public sealed class PipeServer : IDisposable
    {
        public static readonly TimeSpan PickupTimeout = TimeSpan.FromSeconds(5);
        public const double DefaultTimeoutS = 60;

        private readonly string _pipeName;
        private readonly ConcurrentQueue<BridgeRequest> _queue;
        private readonly ExternalEvent _event;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _acceptLoop;

        public string PipeName => _pipeName;
        public bool Running => _acceptLoop != null && !_cts.IsCancellationRequested;

        public PipeServer(string pipeName, ConcurrentQueue<BridgeRequest> queue, ExternalEvent evt)
        {
            _pipeName = pipeName;
            _queue = queue;
            _event = evt;
        }

        public void Start()
        {
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            Logger.Info($"Pipe server listening on \\\\.\\pipe\\{_pipeName}");
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var connected = server;
                    server = null;
                    _ = Task.Run(() => HandleConnectionAsync(connected, ct));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.Error("Pipe accept failed: " + ex.Message);
                    try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { break; }
                }
                finally
                {
                    server?.Dispose();
                }
            }
        }

        private async Task HandleConnectionAsync(NamedPipeServerStream stream, CancellationToken ct)
        {
            using (stream)
            {
                var reader = new StreamReader(stream, new UTF8Encoding(false), false, 65536, leaveOpen: true);
                var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
                try
                {
                    while (!ct.IsCancellationRequested && stream.IsConnected)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var reply = await ProcessLineAsync(line).ConfigureAwait(false);
                        await writer.WriteLineAsync(reply.ToString(Formatting.None)).ConfigureAwait(false);
                    }
                }
                catch (IOException) { /* client went away */ }
                catch (ObjectDisposedException) { }
                catch (Exception ex) { Logger.Error("Pipe connection error: " + ex); }
            }
        }

        private async Task<JObject> ProcessLineAsync(string line)
        {
            JObject msg;
            try { msg = JObject.Parse(line); }
            catch (Exception ex)
            {
                return Envelope.Failure(null, null, "BAD_REQUEST", "Invalid JSON: " + ex.Message);
            }

            var id = msg.Value<string>("id") ?? Guid.NewGuid().ToString("N");
            var method = msg.Value<string>("method");
            var prms = msg["params"] as JObject ?? new JObject();
            if (string.IsNullOrWhiteSpace(method))
                return Envelope.Failure(id, null, "BAD_REQUEST", "Missing 'method'.");

            // "ping" is answered on the pipe thread so liveness checks work even while Revit is busy.
            if (method == "ping")
            {
                var p = Envelope.Success(id, method, new JObject
                {
                    ["pid"] = Process.GetCurrentProcess().Id,
                    ["pipe"] = _pipeName,
                    ["queued"] = _queue.Count,
                });
                return p;
            }

            double timeoutS = prms.Value<double?>("timeout_s") ?? msg.Value<double?>("timeout_s") ?? DefaultTimeoutS;
            if (timeoutS <= 0) timeoutS = DefaultTimeoutS;

            var req = new BridgeRequest(id, method, prms);
            _queue.Enqueue(req);
            RaiseEvent();

            // Wait for the main thread to pick it up. Nudge Revit's message loop while we wait:
            // ExternalEvents are only serviced when Revit's UI thread pumps messages.
            var pickupDeadline = DateTime.UtcNow + PickupTimeout;
            while (!req.Started.Wait(250))
            {
                if (DateTime.UtcNow >= pickupDeadline)
                {
                    if (req.TryCancel())
                    {
                        return Envelope.Failure(id, method, "REVIT_BUSY",
                            "Revit did not pick up the request within 5 s. Nothing was executed.",
                            "A modal dialog, an edit mode (sketch, in-place family) or a long operation is " +
                            "blocking Revit's main thread. Finish or cancel it in Revit, then call again.");
                    }
                    break; // started at the last moment
                }
                RaiseEvent();
                WakeRevit();
            }

            var done = await Task.WhenAny(req.Completion.Task, Task.Delay(TimeSpan.FromSeconds(timeoutS))).ConfigureAwait(false);
            if (done != req.Completion.Task)
            {
                return Envelope.Failure(id, method, "TIMEOUT",
                    $"No reply within {timeoutS:0} s. The call is STILL RUNNING inside Revit and may yet complete.",
                    "Do not retry a write blindly: check the model (query_elements / get_warnings) first. " +
                    "Pass timeout_s for long operations.");
            }
            return req.Completion.Task.Result;
        }

        private void RaiseEvent()
        {
            try { _event.Raise(); } catch (Exception ex) { Logger.Error("ExternalEvent.Raise failed: " + ex.Message); }
        }

        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private const uint WM_NULL = 0x0000;
        private static IntPtr _mainWindow;

        /// <summary>Set on the main thread at startup; lets the pipe thread nudge Revit's message loop.</summary>
        public static void SetMainWindow(IntPtr hwnd) => _mainWindow = hwnd;

        private static void WakeRevit()
        {
            if (_mainWindow != IntPtr.Zero) PostMessage(_mainWindow, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }

        public void Dispose()
        {
            _cts.Cancel();
            // Fail anything still queued so waiting pipe threads return promptly.
            while (_queue.TryDequeue(out var r))
            {
                if (r.TryCancel())
                    r.Completion.TrySetResult(Envelope.Failure(r.Id, r.Method, "BRIDGE_OFF", "The bridge was switched off."));
            }
            Logger.Info("Pipe server stopped.");
        }
    }
}
