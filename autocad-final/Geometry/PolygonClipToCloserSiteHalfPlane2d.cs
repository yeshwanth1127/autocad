using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Geometry
{
    /// <summary>
    /// Clips a 2D polygon to the half-plane of points closer to site <paramref name="i"/> than to <paramref name="j"/>.
    /// Used to build Voronoi cells clipped to a floor polygon.
    /// </summary>
    public static class PolygonClipToCloserSiteHalfPlane2d
    {
        private const double Eps = 1e-9;

        /// <summary>
        /// |p-i|^2 &lt;= |p-j|^2  ⟺  2(j-i)·p &lt;= |j|^2 - |i|^2
        /// </summary>
        public static bool InsideCloserToI(Point2d p, Point2d i, Point2d j)
        {
            double lhs = 2.0 * ((j.X - i.X) * p.X + (j.Y - i.Y) * p.Y);
            double rhs = j.X * j.X + j.Y * j.Y - i.X * i.X - i.Y * i.Y;
            // Relaxed tolerance for large drawing coordinates (reduces spurious clipping gaps).
            double tol = Eps + 1e-12 * (Math.Abs(lhs) + Math.Abs(rhs) + 1.0);
            return lhs <= rhs + tol;
        }

        /// <summary>
        /// Intersection of segment AB with line 2(j-i)·p = |j|^2 - |i|^2 (bisector of i,j).
        /// </summary>
        public static bool TryIntersectBisector(Point2d a, Point2d b, Point2d i, Point2d j, out Point2d hit)
        {
            hit = default;
            double aCoeff = 2.0 * (j.X - i.X);
            double bCoeff = 2.0 * (j.Y - i.Y);
            double c = j.X * j.X + j.Y * j.Y - i.X * i.X - i.Y * i.Y;
            double denom = aCoeff * (b.X - a.X) + bCoeff * (b.Y - a.Y);
            if (Math.Abs(denom) < Eps * (Math.Abs(aCoeff) + Math.Abs(bCoeff) + 1.0))
                return false;
            double t = (c - aCoeff * a.X - bCoeff * a.Y) / denom;
            if (t < -1e-6 || t > 1.0 + 1e-6)
                return false;
            t = Math.Max(0.0, Math.Min(1.0, t));
            hit = new Point2d(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));
            return true;
        }

        /// <summary>
        /// Sutherland–Hodgman clip: <paramref name="vertices"/> closed (last not repeated).
        /// </summary>
        public static List<Point2d> ClipToCloserThan(List<Point2d> vertices, Point2d i, Point2d j)
        {
            int n = vertices.Count;
            if (n < 3)
                return new List<Point2d>();

            var output = new List<Point2d>();
            for (int k = 0; k < n; k++)
            {
                Point2d s = vertices[k];
                Point2d e = vertices[(k + 1) % n];
                bool sIn = InsideCloserToI(s, i, j);
                bool eIn = InsideCloserToI(e, i, j);

                if (sIn && eIn)
                {
                    output.Add(e);
                }
                else if (sIn && !eIn)
                {
                    if (TryIntersectBisector(s, e, i, j, out Point2d hit))
                        output.Add(hit);
                }
                else if (!sIn && eIn)
                {
                    if (TryIntersectBisector(s, e, i, j, out Point2d hit))
                        output.Add(hit);
                    output.Add(e);
                }
            }

            return output;
        }
    }
}
