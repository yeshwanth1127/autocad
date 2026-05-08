using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Geometry
{
    /// <summary>
    /// Sutherland–Hodgman clipping of a 2D polygon (vertex ring, last not repeated) against horizontal half-planes.
    /// </summary>
    public static class PolygonHorizontalHalfPlaneClip2d
    {
        /// <summary>Keep points with y &lt;= yLine (closed half-plane below the horizontal line).</summary>
        public static List<Point2d> ClipKeepYLessOrEqual(IList<Point2d> vertices, double yLine, double eps)
        {
            return Clip(vertices, (_, y) => y <= yLine + eps, yLine);
        }

        /// <summary>Keep points with y &gt;= yLine (closed half-plane above the horizontal line).</summary>
        public static List<Point2d> ClipKeepYGreaterOrEqual(IList<Point2d> vertices, double yLine, double eps)
        {
            return Clip(vertices, (_, y) => y >= yLine - eps, yLine);
        }

        private static List<Point2d> Clip(
            IList<Point2d> vertices,
            Func<double, double, bool> inside,
            double yLine)
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
                    if (TryIntersectHorizontal(s, e, yLine, out Point2d hit))
                        output.Add(hit);
                }
                else if (!sIn && eIn)
                {
                    if (TryIntersectHorizontal(s, e, yLine, out Point2d hit))
                        output.Add(hit);
                    output.Add(e);
                }
            }

            return output;
        }

        private static bool TryIntersectHorizontal(Point2d a, Point2d b, double yLine, out Point2d hit)
        {
            hit = default;
            double dy = b.Y - a.Y;
            if (Math.Abs(dy) <= 1e-18 * (Math.Abs(a.Y) + Math.Abs(b.Y) + 1.0))
                return false;
            double t = (yLine - a.Y) / dy;
            if (t < -1e-9 || t > 1.0 + 1e-9)
                return false;
            t = Math.Max(0.0, Math.Min(1.0, t));
            double dx = b.X - a.X;
            hit = new Point2d(a.X + t * dx, a.Y + t * dy);
            return true;
        }
    }
}
