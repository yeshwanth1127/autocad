using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Geometry
{
    /// <summary>Fallback and auxiliary point generation for placement grids.</summary>
    public static class PointGenerator
    {
        public static bool TryFindAnyInteriorPoint(
            List<Point2d> ring,
            double minX,
            double minY,
            double maxX,
            double maxY,
            double spacingDu,
            out Point2d point)
        {
            point = default(Point2d);
            if (ring == null || ring.Count < 3) return false;
            int attempts = 8;
            for (int ay = 0; ay <= attempts; ay++)
            {
                double y = minY + (maxY - minY) * ay / attempts;
                for (int ax = 0; ax <= attempts; ax++)
                {
                    double x = minX + (maxX - minX) * ax / attempts;
                    var p = new Point2d(
                        Math.Round(x / spacingDu) * spacingDu,
                        Math.Round(y / spacingDu) * spacingDu);
                    if (PolygonUtils.PointInPolygon(ring, p))
                    {
                        point = p;
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
