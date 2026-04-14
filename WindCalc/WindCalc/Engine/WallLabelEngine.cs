using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace WindCalc.Engine
{
    /// <summary>
    /// Interactive wall labeling tool for mechanical energy calculations.
    ///
    /// Workflow:
    ///   1. User clicks walls one at a time in numbering order; ESC finishes picking.
    ///   2. For each wall the outward-facing normal is rotated into true-north coordinates
    ///      using the project location's Angle offset.
    ///   3. A TextNote is placed at the wall midpoint (offset outward) with the label
    ///      "{CardinalDirection}-{SequenceNumber}" (e.g. "N-1", "SSW-3").
    ///
    /// Cardinal resolution uses a 16-point compass (N NNE NE ENE … NNW).
    /// Text type "Chief blueprint 5-32in" is created in the document if absent.
    /// </summary>
    public class WallLabelEngine
    {
        // ── Text note appearance ──────────────────────────────────────────────
        private const string FontName        = "Chief blueprint";
        private const double TextSizeInch    = 5.0 / 32.0;
        private const double TextSizeFt      = TextSizeInch / 12.0;
        private const string TextTypeName    = "Chief blueprint 5-32in";

        // ── Label placement ───────────────────────────────────────────────────
        private const double OffsetFt        = 2.0;   // distance from wall midpoint
        private const bool   SkipDuplicates  = true;

        // ── Compass ───────────────────────────────────────────────────────────
        private const int CompassPoints = 16;

        private static readonly string[] Labels16 =
        {
            "N","NNE","NE","ENE","E","ESE","SE","SSE",
            "S","SSW","SW","WSW","W","WNW","NW","NNW"
        };

        // ── Public entry point ────────────────────────────────────────────────

        /// <summary>
        /// Interactively pick walls, then place cardinal-direction + sequence labels.
        /// Must be called from a valid Revit external command context (not inside a transaction).
        /// </summary>
        public static void Run(UIDocument uidoc)
        {
            var doc  = uidoc.Document;
            var view = doc.ActiveView;

            if (view == null)
            {
                TaskDialog.Show("Label Walls", "No active view.");
                return;
            }

            // ── Step 1: pick walls interactively (no transaction open) ─────────
            var wallIds = PickWallsInOrder(uidoc);
            if (wallIds.Count == 0) return;

            // ── Step 2: read project → true-north rotation angle ──────────────
            double angleRad = GetTrueNorthAngleRad(doc);

            // ── Step 3: place labels in a single transaction ──────────────────
            using var tx = new Transaction(doc, "Place Wall Direction Labels");
            tx.Start();
            try
            {
                var textType = EnsureTextNoteType(doc);

                for (int i = 0; i < wallIds.Count; i++)
                {
                    var wall = doc.GetElement(wallIds[i]) as Wall;
                    if (wall == null) continue;

                    // Outward-facing normal in project coordinates
                    XYZ projNormal = wall.Orientation;

                    // Rotate into true-north coordinates
                    XYZ trueNormal = RotateByAngle(projNormal, angleRad);

                    // Compute compass label
                    double bearing = BearingDegFromTrueNorth(trueNormal);
                    string compass = CompassLabel(bearing, CompassPoints);

                    string tag = $"{compass}-{i + 1}";

                    // Placement point: wall midpoint + offset outward
                    XYZ mid = WallMidpoint(wall, view);
                    XYZ n   = NormalizeXY(projNormal);
                    var pt  = new XYZ(mid.X + n.X * OffsetFt,
                                      mid.Y + n.Y * OffsetFt,
                                      mid.Z);

                    TextNote.Create(doc, view.Id, pt, tag, textType.Id);
                }

                tx.Commit();
            }
            catch
            {
                tx.RollBack();
                throw;
            }
        }

        // ── Interactive wall picking ──────────────────────────────────────────

        private static List<ElementId> PickWallsInOrder(UIDocument uidoc)
        {
            var ids  = new List<ElementId>();
            var seen = new HashSet<int>();
            var filt = new WallFilter();

            while (true)
            {
                try
                {
                    var r = uidoc.Selection.PickObject(
                        ObjectType.Element, filt,
                        $"Pick wall #{ids.Count + 1} (ESC when done)");

                    if (SkipDuplicates)
                    {
                        int intId = r.ElementId.IntegerValue;
                        if (seen.Contains(intId)) continue;
                        seen.Add(intId);
                    }
                    ids.Add(r.ElementId);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    break; // user pressed ESC
                }
            }
            return ids;
        }

        // ── True north angle ──────────────────────────────────────────────────

        /// <summary>
        /// Returns the angle (radians) from project north to true north,
        /// as stored in the active project location.
        /// A positive angle means project north is rotated counter-clockwise
        /// relative to true north.
        /// </summary>
        public static double GetTrueNorthAngleRad(Document doc)
        {
            var ploc = doc.ActiveProjectLocation;
            var pp   = ploc.GetProjectPosition(XYZ.Zero);
            return pp.Angle;
        }

        // ── Vector math ───────────────────────────────────────────────────────

        /// <summary>
        /// Rotates a project-north vector into true-north coordinates.
        /// </summary>
        private static XYZ RotateByAngle(XYZ vec, double angleRad)
        {
            // Revit's Angle is from project north to true north, counter-clockwise.
            // To convert a vector FROM project TO true coords, rotate by +angle.
            var rot = Transform.CreateRotation(XYZ.BasisZ, angleRad);
            return rot.OfVector(vec);
        }

        /// <summary>
        /// Returns a bearing in [0, 360) degrees measured clockwise from true north.
        /// </summary>
        public static double BearingDegFromTrueNorth(XYZ trueVec)
        {
            // atan2(East, North) → clockwise from North
            double ang = Math.Atan2(trueVec.X, trueVec.Y) * (180.0 / Math.PI);
            if (ang < 0) ang += 360.0;
            return ang;
        }

        /// <summary>
        /// Maps a bearing (degrees, CW from N) to a compass label.
        /// </summary>
        public static string CompassLabel(double bearingDeg, int points = 16)
        {
            string[] labels;
            double   step;
            if (points == 4)
            {
                labels = new[] { "N", "E", "S", "W" };
                step   = 90.0;
            }
            else if (points == 8)
            {
                labels = new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
                step   = 45.0;
            }
            else
            {
                labels = Labels16;
                step   = 22.5;
            }
            int idx = (int)((bearingDeg + step / 2.0) / step) % labels.Length;
            return labels[idx];
        }

        private static XYZ NormalizeXY(XYZ v)
        {
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            if (len < 1e-9) return XYZ.BasisX;
            return new XYZ(v.X / len, v.Y / len, 0.0);
        }

        // ── Wall geometry helpers ─────────────────────────────────────────────

        private static XYZ WallMidpoint(Wall wall, View view)
        {
            var loc = wall.Location as LocationCurve;
            if (loc?.Curve != null)
            {
                var p0 = loc.Curve.GetEndPoint(0);
                var p1 = loc.Curve.GetEndPoint(1);
                return new XYZ((p0.X + p1.X) / 2.0,
                               (p0.Y + p1.Y) / 2.0,
                               (p0.Z + p1.Z) / 2.0);
            }
            // Fallback: bounding box center
            var bb = wall.get_BoundingBox(view) ?? wall.get_BoundingBox(null);
            if (bb != null)
                return new XYZ((bb.Min.X + bb.Max.X) / 2.0,
                               (bb.Min.Y + bb.Max.Y) / 2.0,
                               (bb.Min.Z + bb.Max.Z) / 2.0);
            return XYZ.Zero;
        }

        // ── TextNote type management ──────────────────────────────────────────

        private static TextNoteType EnsureTextNoteType(Document doc)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .ToElements();

            if (types.Count == 0)
                throw new InvalidOperationException("No TextNoteType exists in this document.");

            // Look for existing type by name
            TextNoteType found = null;
            foreach (TextNoteType t in types)
            {
                if (GetTypeName(t) == TextTypeName) { found = t; break; }
            }

            // Create by duplicating the first available type if not found
            if (found == null)
            {
                var baseType = (TextNoteType)types[0];
                found = baseType.Duplicate(TextTypeName) as TextNoteType;
            }

            if (found == null)
                throw new InvalidOperationException("Could not create TextNoteType.");

            // Apply font and size
            var pSize = found.get_Parameter(BuiltInParameter.TEXT_SIZE);
            if (pSize != null && !pSize.IsReadOnly) pSize.Set(TextSizeFt);

            var pFont = found.get_Parameter(BuiltInParameter.TEXT_FONT);
            if (pFont != null && !pFont.IsReadOnly) pFont.Set(FontName);

            return found;
        }

        private static string GetTypeName(TextNoteType t)
        {
            foreach (BuiltInParameter bip in new[]
                { BuiltInParameter.SYMBOL_NAME_PARAM, BuiltInParameter.ALL_MODEL_TYPE_NAME })
            {
                try
                {
                    var p = t.get_Parameter(bip);
                    if (p != null) { string s = p.AsString(); if (!string.IsNullOrEmpty(s)) return s; }
                }
                catch { }
            }
            return t.Name;
        }

        // ── Selection filter ──────────────────────────────────────────────────

        private class WallFilter : ISelectionFilter
        {
            public bool AllowElement(Element e)   => e is Wall;
            public bool AllowReference(Reference r, XYZ pt) => true;
        }
    }
}
