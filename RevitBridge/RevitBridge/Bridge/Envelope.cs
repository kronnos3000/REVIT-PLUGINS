using Newtonsoft.Json.Linq;

namespace RevitBridge.Bridge
{
    /// <summary>
    /// The common response shape every call returns:
    /// {id, ok, result, warnings[], errors[], changed:{created[],modified[],deleted[]},
    ///  tx_name, elapsed_ms, doc, error?:{code,message,hint}}
    /// </summary>
    public static class Envelope
    {
        public static JObject Success(string id, string method, JToken result) => Build(id, method, true, result);

        public static JObject Failure(string id, string method, string code, string message, string hint = null, JToken result = null)
        {
            var env = Build(id, method, false, result);
            env["error"] = new JObject { ["code"] = code, ["message"] = message, ["hint"] = hint };
            return env;
        }

        private static JObject Build(string id, string method, bool ok, JToken result) => new JObject
        {
            ["id"] = id,
            ["method"] = method,
            ["ok"] = ok,
            ["result"] = result ?? JValue.CreateNull(),
            ["warnings"] = new JArray(),
            ["errors"] = new JArray(),
            ["changed"] = new JObject { ["created"] = new JArray(), ["modified"] = new JArray(), ["deleted"] = new JArray() },
            ["tx_name"] = null,
            ["elapsed_ms"] = 0,
            ["doc"] = null,
        };
    }
}
