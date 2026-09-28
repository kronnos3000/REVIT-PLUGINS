using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RevitBridge.Bridge
{
    public enum CommandKind
    {
        /// <summary>No transaction. Any model change throws.</summary>
        Read,
        /// <summary>Runs inside the per-call TransactionGroup "Claude: &lt;method&gt;" (one Ctrl+Z); honours dry_run.</summary>
        Write,
        /// <summary>Document-lifecycle calls (open/save/sync/close/activate) that cannot run inside a transaction.</summary>
        Session,
    }

    /// <summary>Marks a static method <c>object M(CommandContext ctx)</c> as a bridge command.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class BridgeCommandAttribute : Attribute
    {
        public string Name { get; }
        public CommandKind Kind { get; }
        /// <summary>"param=value": the call is treated as Write when that param has that value (e.g. "mode=write").</summary>
        public string WriteIf { get; set; }
        public BridgeCommandAttribute(string name, CommandKind kind = CommandKind.Read) { Name = name; Kind = kind; }
    }

    public sealed class CommandDef
    {
        public string Name;
        public CommandKind Kind;
        public string WriteIf;
        public Func<CommandContext, object> Run;

        public CommandKind KindFor(Newtonsoft.Json.Linq.JObject p)
        {
            if (string.IsNullOrEmpty(WriteIf)) return Kind;
            var parts = WriteIf.Split('=');
            return string.Equals((string)p[parts[0]], parts[1], StringComparison.OrdinalIgnoreCase) ? CommandKind.Write : Kind;
        }
    }

    /// <summary>Method name → command. Keys are identical to the MCP tool names.</summary>
    public static class CommandRouter
    {
        private static readonly Dictionary<string, CommandDef> Map = Build();

        private static Dictionary<string, CommandDef> Build()
        {
            var map = new Dictionary<string, CommandDef>(StringComparer.Ordinal);
            var methods = typeof(CommandRouter).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
            foreach (var m in methods)
            {
                var attr = m.GetCustomAttribute<BridgeCommandAttribute>();
                if (attr == null) continue;
                var fn = (Func<CommandContext, object>)Delegate.CreateDelegate(typeof(Func<CommandContext, object>), m);
                map[attr.Name] = new CommandDef { Name = attr.Name, Kind = attr.Kind, WriteIf = attr.WriteIf, Run = fn };
            }
            return map;
        }

        public static bool TryGet(string name, out CommandDef def) => Map.TryGetValue(name, out def);

        public static IEnumerable<CommandDef> All => Map.Values.OrderBy(d => d.Name);
    }
}
