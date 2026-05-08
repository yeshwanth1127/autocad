using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Geometry
{
    public static class ConvexHull2d
    {
        public static List<Point2d> Compute(IReadOnlyList<Point2d> points)
        {
            var pts = new List<Point2d>();
            if (points == null) return pts;
            for (int i = 0; i < points.Count; i++)
                pts.Add(points[i]);

            pts.Sort((a, b) =>
            {
                int cx = a.X.CompareTo(b.X);
                return cx != 0 ? cx : a.Y.CompareTo(b.Y);
            });

            // De-dupe exact duplicates
            var unique = new List<Point2d>();
            for (int i = 0; i < pts.Count; i++)
            {
                if (i == 0 || pts[i].GetDistanceTo(pts[i - 1]) > 1e-9)
                    unique.Add(pts[i]);
            }
            if (unique.Count < 3) return unique;

            var lower = new List<Point2d>();
            for (int i = 0; i < unique.Count; i++)
            {
                var p = unique[i];
                while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], p) <= 0)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(p);
            }

            var upper = new List<Point2d>();
            for (int i = unique.Count - 1; i >= 0; i--)
            {
                var p = unique[i];
                while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], p) <= 0)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(p);
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        private static double Cross(Point2d o, Point2d a, Point2d b)
            => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
    }
}

