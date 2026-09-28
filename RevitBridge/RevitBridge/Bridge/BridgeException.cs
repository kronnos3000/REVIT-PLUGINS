using System;

namespace RevitBridge
{
    /// <summary>
    /// A command failure with a stable machine-readable code (BAD_PARAMS, NOT_FOUND,
    /// NO_DOCUMENT, CONFIRM_REQUIRED, SIZE_GUARD, REVIT_ERROR, ...). Caught by the
    /// handler and turned into an envelope with ok=false.
    /// </summary>
    public class BridgeException : Exception
    {
        public string Code { get; }
        public string Hint { get; }
        public object Data2 { get; }

        public BridgeException(string code, string message, string hint = null, object data = null)
            : base(message)
        {
            Code = code;
            Hint = hint;
            Data2 = data;
        }
    }
}
