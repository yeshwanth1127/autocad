using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Geometry
{
    /// <summary>
    /// Grid-line / trunk intersection helpers for slanted main polylines (branch routing).
    /// </summary>
    public static class PolylineTrunkGridAttach2d
    {
        /// <summary>
        /// Same classification as trunk bbox aspect used for default branch orientation.
        /// </summary>
        public static bool TrunkPolylineRunsMostlyVertical(IReadOnlyList<Point2d> pts, double tol)
        {
            if (pts == null || pts.Count < 2)
                return true;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            double spanX = maxX - minX;
            double spanY = maxY - minY;
            double te = tol > 0 ? tol : 1e-6;
            if (spanX <= te && spanY <= te)
                return true;
            return spanY >= spanX;
        }

        /// <summary>
        /// Finds the best on-trunk attachment where a horizontal (branchHorizontal) or vertical grid line
        /// through <paramref name="gridKey"/> meets the polyline (same algorithm as branch routing).
        /// </summary>
        public static bool TryFindGridAxisAttach(
            List<Point2d> poly,
            List<Point2d> zoneRing,
            bool branchHorizontal,
            double gridKey,
            List<Point2d> sprinklers,
            double tol,
            out Point2d attach)
        {
            attach = default;
            double te = tol > 0 ? tol : 1e-6;
            if (poly == null || poly.Count < 2)
                return false;

            double target = 0;
            int count = 0;
            if (sprinklers != null)
            {
                for (int i = 0; i < sprinklers.Count; i++)
                {
                    target += branchHorizontal ? sprinklers[i].X : sprinklers[i].Y;
                    count++;
                }
            }
            if (count > 0)
                target /= count;

            bool found = false;
            double best = double.MaxValue;
            Point2d bestPoint = default;

            for (int i = 0; i + 1 < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[i + 1];
                Point2d p;
                if (branchHorizontal)
                {
                    double dyA = a.Y - gridKey;
                    double dyB = b.Y - gridKey;
                    if (System.Math.Abs(dyA) <= te && System.Math.Abs(dyB) <= te)
                    {
                        double lo = System.Math.Min(a.X, b.X);
                        double hi = System.Math.Max(a.X, b.X);
                        double x = System.Math.Max(lo, System.Math.Min(hi, target));
                        p = new Point2d(x, gridKey);
                    }
                    else if ((dyA < -te && dyB < -te) || (dyA > te && dyB > te))
                    {
                        continue;
                    }
                    else
                    {
                        double denom = b.Y - a.Y;
                        if (System.Math.Abs(denom) <= te)
                            continue;
                        double u = (gridKey - a.Y) / denom;
                        if (u < -1e-9 || u > 1.0 + 1e-9)
                            continue;
                        p = new Point2d(a.X + (b.X - a.X) * u, gridKey);
                    }

                    double d = System.Math.Abs(p.X - target);
                    if (zoneRing != null && zoneRing.Count >= 3 && !PolygonUtils.PointInPolygon(zoneRing, p))
                        d += te * 100.0;
                    if (d < best)
                    {
                        best = d;
                        bestPoint = p;
                        found = true;
                    }
                }
                else
                {
                    double dxA = a.X - gridKey;
                    double dxB = b.X - gridKey;
                    if (System.Math.Abs(dxA) <= te && System.Math.Abs(dxB) <= te)
                    {
                        double lo = System.Math.Min(a.Y, b.Y);
                        double hi = System.Math.Max(a.Y, b.Y);
                        double y = System.Math.Max(lo, System.Math.Min(hi, target));
                        p = new Point2d(gridKey, y);
                    }
                    else if ((dxA < -te && dxB < -te) || (dxA > te && dxB > te))
                    {
                        continue;
                    }
                    else
                    {
                        double denom = b.X - a.X;
                        if (System.Math.Abs(denom) <= te)
                            continue;
                        double u = (gridKey - a.X) / denom;
                        if (u < -1e-9 || u > 1.0 + 1e-9)
                            continue;
                        p = new Point2d(gridKey, a.Y + (b.Y - a.Y) * u);
                    }

                    double d = System.Math.Abs(p.Y - target);
                    if (zoneRing != null && zoneRing.Count >= 3 && !PolygonUtils.PointInPolygon(zoneRing, p))
                        d += te * 100.0;
                    if (d < best)
                    {
                        best = d;
                        bestPoint = p;
                        found = true;
                    }
                }
            }

            if (!found)
                return false;

            attach = bestPoint;
            return true;
        }
    }
}
