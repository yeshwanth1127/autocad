using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Geometry
{
    /// <summary>
    /// Builds closed lightweight polylines from vertex lists and chains line/polyline segments into closed loops.
    /// </summary>
    public static class ClosedPolylineFromPointsAndSegments
    {
        private static bool PointsEqual(Point3d a, Point3d b, double tol)
        {
            return a.DistanceTo(b) <= tol;
        }

        /// <summary>
        /// Creates a closed 2D polyline (world XY) from ordered vertices (no duplicate closing vertex).
        /// </summary>
        public static Polyline CreateClosedPolylineFromPoints(IList<Point3d> vertices, double bulge = 0)
        {
            if (vertices == null || vertices.Count < 3)
                throw new ArgumentException("Need at least 3 vertices for a closed boundary.", nameof(vertices));

            var pl = new Polyline();
            for (int i = 0; i < vertices.Count; i++)
            {
                var p = vertices[i];
                pl.AddVertexAt(i, new Point2d(p.X, p.Y), bulge, 0, 0);
            }
            pl.Closed = true;
            return pl;
        }

        /// <summary>
        /// Extracts undirected edges from Lines and Polylines (each edge once).
        /// </summary>
        public static List<(Point3d Start, Point3d End)> CollectSegments(IEnumerable<Entity> entities)
        {
            var segments = new List<(Point3d, Point3d)>();
            foreach (var ent in entities)
            {
                if (ent is Line line)
                {
                    if (!PointsEqual(line.StartPoint, line.EndPoint, 1e-12))
                        segments.Add((line.StartPoint, line.EndPoint));
                    continue;
                }
                if (ent is Polyline pl)
                {
                    int n = pl.NumberOfVertices;
                    if (pl.Closed)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            int j = (i + 1) % n;
                            var a = pl.GetPoint3dAt(i);
                            var b = pl.GetPoint3dAt(j);
                            if (!PointsEqual(a, b, 1e-12))
                                segments.Add((a, b));
                        }
                    }
                    else
                    {
                        for (int i = 0; i < n - 1; i++)
                        {
                            var a = pl.GetPoint3dAt(i);
                            var b = pl.GetPoint3dAt(i + 1);
                            if (!PointsEqual(a, b, 1e-12))
                                segments.Add((a, b));
                        }
                    }
                }
            }
            return segments;
        }

        /// <summary>
        /// Chains segments into one closed vertex ring (no duplicate closing point).
        /// </summary>
        public static List<Point3d> ChainSegmentsToClosedLoop(IList<(Point3d Start, Point3d End)> segments, double tolerance)
        {
            if (segments == null || segments.Count < 3)
                throw new InvalidOperationException("Need at least 3 segments to form an area.");

            var remaining = segments.ToList();
            var (s0, e0) = remaining[0];
            remaining.RemoveAt(0);
            var ring = new List<Point3d> { s0, e0 };
            Point3d current = e0;

            int safety = remaining.Count + 5;
            while (remaining.Count > 0 && safety-- > 0)
            {
                int idx = FindNextSegmentIndex(remaining, current, tolerance, out bool reverse);
                if (idx < 0)
                    throw new InvalidOperationException("Could not chain segments — check gaps or order.");

                var (a, b) = remaining[idx];
                remaining.RemoveAt(idx);
                Point3d next;
                if (!reverse)
                {
                    if (!PointsEqual(current, a, tolerance))
                        throw new InvalidOperationException("Segment mismatch at chain.");
                    next = b;
                }
                else
                {
                    if (!PointsEqual(current, b, tolerance))
                        throw new InvalidOperationException("Segment mismatch at chain.");
                    next = a;
                }
                current = next;
                ring.Add(current);
            }

            if (remaining.Count > 0)
                throw new InvalidOperationException("Extra segments — not a single closed loop.");

            if (!PointsEqual(ring[0], ring[ring.Count - 1], tolerance))
                throw new InvalidOperationException("Segments do not close to the starting point.");

            ring.RemoveAt(ring.Count - 1);
            if (ring.Count < 3)
                throw new InvalidOperationException("Boundary needs at least 3 corners.");

            return ring;
        }

        private static int FindNextSegmentIndex(List<(Point3d Start, Point3d End)> remaining, Point3d at, double tol, out bool reverse)
        {
            reverse = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                var (s, e) = remaining[i];
                if (PointsEqual(at, s, tol))
                {
                    reverse = false;
                    return i;
                }
                if (PointsEqual(at, e, tol))
                {
                    reverse = true;
                    return i;
                }
            }
            return -1;
        }
    }
}
