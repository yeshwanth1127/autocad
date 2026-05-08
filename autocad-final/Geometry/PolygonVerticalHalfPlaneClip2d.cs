using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Geometry
{
    /// <summary>
    /// Sutherland–Hodgman clipping of a 2D polygon (vertex ring, last not repeated) against vertical half-planes.
    /// </summary>
    public static class PolygonVerticalHalfPlaneClip2d
    {
        /// <summary>Keep points with x &lt;= xLine (closed half-plane to the left of the vertical line).</summary>
        public static List<Point2d> ClipKeepXLessOrEqual(IList<Point2d> vertices, double xLine, double eps)
        {
            return Clip(vertices, (x, _) => x <= xLine + eps, xLine);
        }

        /// <summary>Keep points with x &gt;= xLine (closed half-plane to the right of the vertical line).</summary>
        public static List<Point2d> ClipKeepXGreaterOrEqual(IList<Point2d> vertices, double xLine, double eps)
        {
            return Clip(vertices, (x, _) => x >= xLine - eps, xLine);
        }

        private static List<Point2d> Clip(
            IList<Point2d> vertices,
            Func<double, double, bool> inside,
            double xLine)
        {
            int n = vertices.Count;
            if (n < 3)
                return new List<Point2d>();

            var output = new List<Point2d>();
            for (int k = 0; k < n; k++)
            {
                Point2d s = vertices[k];
                Point2d e = vertices[(k + 1) % n];
                bool sIn = inside(s.X, s.Y);
                bool eIn = inside(e.X, e.Y);

                if (sIn && eIn)
                {
                    output.Add(e);
                }
                else if (sIn && !eIn)
                {
                    if (TryIntersectVertical(s, e, xLine, out Point2d hit))
                        output.Add(hit);
                }
                else if (!sIn && eIn)
                {
                    if (TryIntersectVertical(s, e, xLine, out Point2d hit))
                        output.Add(hit);
                    output.Add(e);
                }
            }

            return output;
        }

        private static bool TryIntersectVertical(Point2d a, Point2d b, double xLine, out Point2d hit)
        {
            hit = default;
            double dx = b.X - a.X;
            if (Math.Abs(dx) <= 1e-18 * (Math.Abs(a.X) + Math.Abs(b.X) + 1.0))
                return false;
            double t = (xLine - a.X) / dx;
            if (t < -1e-9 || t > 1.0 + 1e-9)
                return false;
            t = Math.Max(0.0, Math.Min(1.0, t));
            hit = new Point2d(a.X + t * dx, a.Y + t * (b.Y - a.Y));
            return true;
        }

        /// <summary>Simple absolute shoelace area (positive value).</summary>
        public static double AbsArea(IList<Point2d> v)
        {
            int n = v.Count;
            if (n < 3)
                return 0;
            double a = 0;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                a += v[i].X * v[j].Y - v[j].X * v[i].Y;
            }

            return Math.Abs(a) * 0.5;
        }
    }
}
