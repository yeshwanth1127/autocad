using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Geometry
{
    /// <summary>
    /// Converts boundary-candidate entities (LWPolyline, classic 2D polyline, etc.) into a closed lightweight polyline for area workflows.
    /// </summary>
    public static class BoundaryEntityToClosedLwPolyline
    {
        /// <summary>
        /// If the polyline is not flagged closed but the first and last vertices coincide within tolerance, sets Closed = true on the clone.
        /// </summary>
        public static Polyline TryCloseCoincidentVertices(Polyline source, double tolerance)
        {
            if (source == null) return null;
            var pl = (Polyline)source.Clone();
            if (pl.Closed)
                return pl;
            if (pl.NumberOfVertices < 3)
                return pl;
            var a = pl.GetPoint2dAt(0);
            var b = pl.GetPoint2dAt(pl.NumberOfVertices - 1);
            if (a.GetDistanceTo(b) <= tolerance)
                pl.Closed = true;
            return pl;
        }

        /// <summary>
        /// Converts a legacy 2D polyline (AcDb2dPolyline) to a lightweight polyline by exploding to segments.
        /// Straight segments only; arc bulges become Arc entities — user should use CONVERTPOLY / redraw as LWPOLYLINE if this fails.
        /// </summary>
        public static Polyline FromPolyline2d(Polyline2d p2d, Database db)
        {
            if (p2d == null) throw new ArgumentNullException(nameof(p2d));
            if (db == null) throw new ArgumentNullException(nameof(db));

            var exploded = new DBObjectCollection();
            p2d.Explode(exploded);
            var lines = new List<Line>();
            try
            {
                foreach (DBObject obj in exploded)
                {
                    if (obj is Line ln)
                        lines.Add(ln);
                    else if (obj is Arc)
                        throw new InvalidOperationException(
                            "This classic 2D polyline contains arc segments. In AutoCAD, run CONVERTPOLY to make it a LWPOLYLINE, or redraw the boundary as a closed lightweight polyline.");
                }

                if (lines.Count < 3)
                    throw new InvalidOperationException("Not enough straight segments in the 2D polyline.");

                var segments = ClosedPolylineFromPointsAndSegments.CollectSegments(lines);
                double tol = CoincidentTolerance(db);
                var ring = ClosedPolylineFromPointsAndSegments.ChainSegmentsToClosedLoop(segments, tol);
                return ClosedPolylineFromPointsAndSegments.CreateClosedPolylineFromPoints(ring, 0);
            }
            finally
            {
                foreach (DBObject obj in exploded)
                {
                    try { obj.Dispose(); } catch { /* ignore */ }
                }
            }
        }

        public static double CoincidentTolerance(Database db)
        {
            return db.Insunits == UnitsValue.Millimeters ? 1.0 : 1e-4;
        }

        /// <summary>
        /// Approximates a circle as a closed lightweight polyline with <paramref name="segments"/> straight edges.
        /// </summary>
        public static Polyline FromCircle(Circle circle, int segments = 72)
        {
            if (circle == null) throw new ArgumentNullException(nameof(circle));
            if (segments < 8) segments = 8;

            var pl = new Polyline(segments);
            pl.SetDatabaseDefaults();
            pl.Normal = circle.Normal;
            pl.Elevation = circle.Center.Z;
            double cx = circle.Center.X;
            double cy = circle.Center.Y;
            double r = circle.Radius;
            for (int i = 0; i < segments; i++)
            {
                double t = (2.0 * Math.PI * i) / segments;
                var p = new Point2d(cx + r * Math.Cos(t), cy + r * Math.Sin(t));
                pl.AddVertexAt(i, p, 0, 0, 0);
            }
            pl.Closed = true;
            return pl;
        }
    }
}
