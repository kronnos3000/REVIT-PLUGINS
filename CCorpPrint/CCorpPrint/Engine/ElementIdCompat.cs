using Autodesk.Revit.DB;

namespace CCorpPrint.Engine
{
    /// <summary>
    /// Revit 2024+ kept ElementId.IntegerValue (int); Revit 2027 replaced it with
    /// ElementId.Value (long) and deprecated the int property. This helper hides
    /// the delta so call-sites stay year-agnostic.
    /// </summary>
    internal static class ElementIdCompat
    {
        public static long AsLong(this ElementId id)
        {
#if REVIT2027
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }
    }
}
