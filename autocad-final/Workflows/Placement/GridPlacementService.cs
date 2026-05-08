using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Agent;
using autocad_final.Geometry;

namespace autocad_final.Workflows.Placement
{
    /// <summary>Interior orthogonal lattice aligned to the offset ring anchor (same phase as boundary heads).</summary>
    public static class GridPlacementService
    {
        /// <summary>Hard cap so bbox × tight spacing cannot allocate millions of candidate points.</summary>
        private const long MaxLatticeCells = 350_000;

        public static List<Point2d> GenerateInteriorLatticeCandidates(List<Point2d> ring, double gridSpacing)
        {
            var candidates = new List<Point2d>();
            if (ring == null || ring.Count < 3 || gridSpacing <= 0)
                return candidates;

            int anchorIndex = PolygonUtils.FindBottomLeftVertexIndex(ring);
            double gridOriginX = ring[anchorIndex].X;
            double gridOriginY = ring[anchorIndex].Y;
            return GenerateInteriorLatticeCandidates(ring, gridSpacing, gridOriginX, gridOriginY);
        }

        public static List<Point2d> GenerateInteriorLatticeCandidates(List<Point2d> ring, double gridSpacing, double gridOriginX, double gridOriginY)
        {
            var candidates = new List<Point2d>();
            if (ring == null || ring.Count < 3 || gridSpacing <= 0)
                return candidates;

            double eps = Math.Max(1.0, gridSpacing * 1e-4);

            PolygonUtils.GetBoundingBox(ring, out double minBoundaryX, out double minBoundaryY, out double maxBoundaryX, out double maxBoundaryY);
            int gridColumnStart = (int)Math.Floor((minBoundaryX - gridOriginX) / gridSpacing);
            int gridColumnEnd = (int)Math.Ceiling((maxBoundaryX - gridOriginX) / gridSpacing);
            int gridRowStart = (int)Math.Floor((minBoundaryY - gridOriginY) / gridSpacing);
            int gridRowEnd = (int)Math.Ceiling((maxBoundaryY - gridOriginY) / gridSpacing);
            if (gridColumnEnd < gridColumnStart || gridRowEnd < gridRowStart)
                return candidates;

            long cellCount = (long)(gridColumnEnd - gridColumnStart + 1) * (gridRowEnd - gridRowStart + 1);
            AgentLog.Write("GridPlacement",
                "bbox cols " + gridColumnStart.ToString(CultureInfo.InvariantCulture) + ".." + gridColumnEnd.ToString(CultureInfo.InvariantCulture) +
                " rows " + gridRowStart.ToString(CultureInfo.InvariantCulture) + ".." + gridRowEnd.ToString(CultureInfo.InvariantCulture) +
                " cellCount=" + cellCount.ToString(CultureInfo.InvariantCulture));
            if (cellCount > MaxLatticeCells)
            {
                AgentLog.Write("GridPlacement", "skip: cellCount > MaxLatticeCells");
                return candidates;
            }

            for (int gridRow = gridRowStart; gridRow <= gridRowEnd; gridRow++)
            {
                double y = gridOriginY + gridRow * gridSpacing;
                for (int gridColumn = gridColumnStart; gridColumn <= gridColumnEnd; gridColumn++)
                {
                    double x = gridOriginX + gridColumn * gridSpacing;
                    var p = new Point2d(x, y);
                    if (x < minBoundaryX - eps || x > maxBoundaryX + eps || y < minBoundaryY - eps || y > maxBoundaryY + eps)
                        continue;
                    candidates.Add(p);
                }
            }

            AgentLog.Write("GridPlacement", "candidates built count=" + candidates.Count.ToString(CultureInfo.InvariantCulture));
            return candidates;
        }
    }
}
