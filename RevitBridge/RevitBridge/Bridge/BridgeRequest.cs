using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RevitBridge.Bridge
{
    /// <summary>
    /// One queued call. The pipe thread creates it and waits on <see cref="Completion"/>;
    /// the Revit main thread claims it (<see cref="TryStart"/>), runs it and completes it.
    /// A request that was never claimed can be cancelled by the pipe thread
    /// (<see cref="TryCancel"/>), so a REVIT_BUSY reply guarantees nothing ran.
    /// </summary>
    public sealed class BridgeRequest
    {
        private const int Pending = 0, Running = 1, Cancelled = 2;
        private int _state = Pending;

        public string Id { get; }
        public string Method { get; }
        public JObject Params { get; }
        public TaskCompletionSource<JObject> Completion { get; } =
            new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Started { get; } = new ManualResetEventSlim(false);

        public BridgeRequest(string id, string method, JObject @params)
        {
            Id = id;
            Method = method;
            Params = @params ?? new JObject();
        }

        /// <summary>Main thread: claim the request. False if the pipe side already gave up on it.</summary>
        public bool TryStart()
        {
            var ok = Interlocked.CompareExchange(ref _state, Running, Pending) == Pending;
            if (ok) Started.Set();
            return ok;
        }

        /// <summary>Pipe thread: withdraw a request that has not started. False if it is already running.</summary>
        public bool TryCancel() => Interlocked.CompareExchange(ref _state, Cancelled, Pending) == Pending;
    }
}
