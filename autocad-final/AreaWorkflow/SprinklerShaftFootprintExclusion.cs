using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Drops sprinkler grid points that lie inside shaft block footprints (2D extents of the insert).
    /// </summary>
    public static class SprinklerShaftFootprintExclusion
    {
        private const double Eps = 1e-9;

        /// <summary>
        /// True when <paramref name="p"/> lies in the closed axis-aligned rectangle of the shaft block
        /// (<see cref="BlockReference.GeometricExtents"/>). Shaft hints without extents are ignored.
        /// </summary>
        public static bool IsPointInsideShaftFootprint(Point2d p, FindShaftsInsideBoundary.ShaftBlockInfo shaft)
        {
            if (!shaft.HasExtents)
                return false;

            var min = shaft.Extents2d.MinPoint;
            var max = shaft.Extents2d.MaxPoint;
            double minX = System.Math.Min(min.X, max.X);
            double maxX = System.Math.Max(min.X, max.X);
            double minY = System.Math.Min(min.Y, max.Y);
            double maxY = System.Math.Max(min.Y, max.Y);
            if (maxX - minX < Eps && maxY - minY < Eps)
                return false;

            return p.X >= minX - Eps && p.X <= maxX + Eps && p.Y >= minY - Eps && p.Y <= maxY + Eps;
        }

        /// <summary>
        /// Returns a copy of <paramref name="points"/> without any point inside a shaft footprint
        /// for shafts collected inside <paramref name="boundary"/> (same rules as
        /// <see cref="FindShaftsInsideBoundary.GetShaftBlocksInsideBoundary"/>).
        /// </summary>
        public static List<Point2d> RemovePointsInsideShaftFootprints(
            Database db,
            Polyline boundary,
            IEnumerable<Point2d> points)
        {
            if (points == null)
                return new List<Point2d>();
            if (db == null || boundary == null)
                return new List<Point2d>(points);

            var shafts = FindShaftsInsideBoundary.GetShaftBlocksInsideBoundary(db, boundary);
            if (shafts == null || shafts.Count == 0)
                return new List<Point2d>(points);

            var kept = new List<Point2d>();
            foreach (var p in points)
            {
                bool inside = false;
                for (int i = 0; i < shafts.Count; i++)
                {
                    if (IsPointInsideShaftFootprint(p, shafts[i]))
                    {
                        inside = true;
                        break;
                    }
                }
                if (!inside)
                    kept.Add(p);
            }
            return kept;
        }
    }
}
