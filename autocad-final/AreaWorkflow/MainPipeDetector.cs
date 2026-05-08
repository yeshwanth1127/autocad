using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Scans the drawing for main-pipe trunk entities tagged to a zone and returns
    /// them as angle-aware segments. Used by <see cref="AngleTrunkRouting2d"/> so
    /// that re-routing after a manual pipe adjustment honours the actual geometry
    /// instead of recomputing a fresh axis-aligned trunk.
    /// </summary>
    public static class MainPipeDetector
    {
        public readonly struct MainPipeSegment
        {
            public readonly Point2d Start;
            public readonly Point2d End;
            /// <summary>Angle from positive X-axis, radians.</summary>
            public readonly double AngleRad;
            public readonly double Length;

            public MainPipeSegment(Point2d start, Point2d end)
            {
                Start = start;
                End = end;
                double dx = end.X - start.X;
                double dy = end.Y - start.Y;
                Length = Math.Sqrt(dx * dx + dy * dy);
                AngleRad = Math.Atan2(dy, dx);
            }

            public Point2d Direction => Length > 1e-12
                ? new Point2d((End.X - Start.X) / Length, (End.Y - Start.Y) / Length)
                : new Point2d(1, 0);

            /// <summary>Unit normal perpendicular to this segment (rotated 90° CCW).</summary>
            public Point2d Normal
            {
                get
                {
                    var d = Direction;
                    return new Point2d(-d.Y, d.X);
                }
            }
        }

        /// <summary>
        /// Returns all trunk segments tagged to <paramref name="zoneBoundaryHandle"/> in the drawing.
        /// Returns an empty list if no main pipe has been routed yet for that zone.
        /// Supports <c>Line</c> and <c>Polyline</c> entities.
        /// </summary>
        public static List<MainPipeSegment> FindMainPipes(Database db, string zoneBoundaryHandle)
        {
            var result = new List<MainPipeSegment>();
            if (db == null || string.IsNullOrEmpty(zoneBoundaryHandle))
                return result;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    Entity ent;
                    try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { continue; }
                    if (ent == null) continue;

                    // Must be tagged as trunk and belong to this zone.
                    if (!SprinklerXData.IsTaggedTrunk(ent)) continue;
                    if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out var h) ||
                        !string.Equals(h, zoneBoundaryHandle, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (ent is Line line)
                    {
                        var seg = new MainPipeSegment(
                            new Point2d(line.StartPoint.X, line.StartPoint.Y),
                            new Point2d(line.EndPoint.X, line.EndPoint.Y));
                        if (seg.Length > 1e-6)
                            result.Add(seg);
                    }
                    else if (ent is Polyline pl)
                    {
                        for (int i = 0; i < pl.NumberOfVertices - 1; i++)
                        {
                            var s = pl.GetPoint2dAt(i);
                            var e = pl.GetPoint2dAt(i + 1);
                            var seg = new MainPipeSegment(s, e);
                            if (seg.Length > 1e-6)
                                result.Add(seg);
                        }
                        // If closed polyline, add the closing segment too.
                        if (pl.Closed && pl.NumberOfVertices > 1)
                        {
                            var s = pl.GetPoint2dAt(pl.NumberOfVertices - 1);
                            var e = pl.GetPoint2dAt(0);
                            var seg = new MainPipeSegment(s, e);
                            if (seg.Length > 1e-6)
                                result.Add(seg);
                        }
                    }
                }

                tr.Commit();
            }

            return result;
        }

        /// <summary>
        /// Returns the dominant angle of a set of trunk segments, weighted by length.
        /// Angles are normalised to [0°, 180°) so opposite directions are treated as equal.
        /// Returns NaN if the list is empty.
        /// </summary>
        public static double DominantAngleDeg(List<MainPipeSegment> segments)
        {
            if (segments == null || segments.Count == 0)
                return double.NaN;

            // Length-weighted average of (angle mod 180°).
            // Use circular mean on doubled angle to handle the wrap-around.
            double sinSum = 0, cosSum = 0;
            foreach (var s in segments)
            {
                double a = s.AngleRad % Math.PI; // normalise to [0, π)
                if (a < 0) a += Math.PI;
                double a2 = 2 * a;               // double angle trick
                sinSum += Math.Sin(a2) * s.Length;
                cosSum += Math.Cos(a2) * s.Length;
            }

            double meanAngle2 = Math.Atan2(sinSum, cosSum);
            double meanAngle = meanAngle2 / 2.0;
            if (meanAngle < 0) meanAngle += Math.PI;
            return meanAngle * 180.0 / Math.PI;
        }
    }
}
