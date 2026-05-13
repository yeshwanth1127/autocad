using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Agent;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;

namespace autocad_final.Workflows.Placement
{
    /// <summary>
    /// Shared interior lattice + offset pipeline for placing sprinklers inside any closed polygon
    /// (floor parcel or a single room outline).
    /// </summary>
    public static class SprinklerGridInPolygonWorkflow
    {
        private const long MaxLatticeCells = 350_000;

        /// <summary>
        /// Builds an inward offset ring and the best interior sprinkler grid for <paramref name="sourceBoundary"/>.
        /// Does not apply shaft/room exclusions or insert blocks.
        /// </summary>
        public static bool TryComputeInteriorGrid(
            Document doc,
            Polyline sourceBoundary,
            double offsetDu,
            double spacing,
            out List<Point2d> offsetRing,
            out List<Point2d> gridPoints,
            out string errorMessage)
        {
            offsetRing = null;
            gridPoints = null;
            errorMessage = null;

            if (doc == null || sourceBoundary == null)
            {
                errorMessage = "Invalid boundary input.";
                return false;
            }

            double extentHintDu = 0;
            try
            {
                var ext = sourceBoundary.GeometricExtents;
                extentHintDu = Math.Max(ext.MaxPoint.X - ext.MinPoint.X, ext.MaxPoint.Y - ext.MinPoint.Y);
            }
            catch
            {
                extentHintDu = 0;
            }

            if (!(spacing > 1e-9) || !(offsetDu > 1e-9))
            {
                errorMessage =
                    "Grid spacing or offset is effectively zero (check settings / drawing scale). " +
                    "INSUNITS=" + DrawingUnitsHelper.InsunitsLabel(doc.Database) + ".";
                return false;
            }

            if (extentHintDu > 0)
            {
                long estCells = (long)Math.Ceiling(extentHintDu / spacing);
                estCells = estCells * estCells;
                if (estCells > MaxLatticeCells)
                {
                    errorMessage =
                        "Grid would have too many cells for this boundary (scale may be wrong). " +
                        "INSUNITS=" + DrawingUnitsHelper.InsunitsLabel(doc.Database) +
                        ", spacing≈" + spacing.ToString("G6", CultureInfo.InvariantCulture) + " DU.";
                    return false;
                }
            }

            AgentLog.Write("SprinklerGridInPolygonWorkflow", "TryBuildInwardOffsetRing start");
            if (!OffsetService.TryBuildInwardOffsetRing(
                    sourceBoundary,
                    offsetDu,
                    out offsetRing,
                    out string offsetErr))
            {
                errorMessage = offsetErr;
                AgentLog.Write("SprinklerGridInPolygonWorkflow", "offset fail: " + offsetErr);
                return false;
            }

            PolygonUtils.GetBoundingBox(offsetRing, out double minBoundaryX, out double minBoundaryY, out _, out _);
            double baseOriginX = minBoundaryX;
            double baseOriginY = minBoundaryY;

            double half = spacing * 0.5;
            double[] ox = { baseOriginX, baseOriginX + half };
            double[] oy = { baseOriginY, baseOriginY + half };

            List<Point2d> bestFinal = null;
            double bestWorstEdgeDist = double.PositiveInfinity;
            bool latticeTooDense = false;

            for (int xi = 0; xi < ox.Length; xi++)
            {
                for (int yi = 0; yi < oy.Length; yi++)
                {
                    double gridOriginX = ox[xi];
                    double gridOriginY = oy[yi];

                    var gridCandidates = GridPlacementService.GenerateInteriorLatticeCandidates(offsetRing, spacing, gridOriginX, gridOriginY);
                    if (gridCandidates.Count == 0)
                    {
                        latticeTooDense = true;
                        continue;
                    }

                    var filteredGrid = PointFilter.FilterInsidePolygon(gridCandidates, offsetRing, spacing);
                    if (filteredGrid.Count == 0)
                        continue;

                    var finalPoints = BoundaryCoverageService.EnsureBoundaryCoverage(
                        filteredGrid,
                        offsetRing,
                        filteredGrid,
                        spacing,
                        offsetDu,
                        gridOriginX,
                        gridOriginY);

                    finalPoints = SnapToGrid(finalPoints, gridOriginX, gridOriginY, spacing);
                    finalPoints = PointFilter.FilterInsidePolygon(finalPoints, offsetRing, spacing);

                    double worst = WorstEdgeDistance(offsetRing, finalPoints);
                    if (worst < bestWorstEdgeDist)
                    {
                        bestWorstEdgeDist = worst;
                        bestFinal = finalPoints;
                    }
                }
            }

            if (bestFinal == null && latticeTooDense)
            {
                errorMessage =
                    "Lattice is too dense for this boundary (bbox vs grid spacing). " +
                    "Limit is about " + MaxLatticeCells.ToString(CultureInfo.InvariantCulture) + " cells.";
                return false;
            }

            gridPoints = bestFinal ?? new List<Point2d>();
            return true;
        }

        private static List<Point2d> SnapToGrid(List<Point2d> points, double originX, double originY, double spacing)
        {
            var result = new List<Point2d>();
            if (points == null || points.Count == 0 || !(spacing > 0))
                return result;

            var seen = new HashSet<Tuple<int, int>>();
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                int ix = (int)Math.Round((p.X - originX) / spacing);
                int iy = (int)Math.Round((p.Y - originY) / spacing);
                var key = Tuple.Create(ix, iy);
                if (!seen.Add(key))
                    continue;
                result.Add(new Point2d(originX + ix * spacing, originY + iy * spacing));
            }
            return result;
        }

        private static double WorstEdgeDistance(List<Point2d> ring, List<Point2d> points)
        {
            if (ring == null || ring.Count < 3 || points == null || points.Count == 0)
                return double.PositiveInfinity;

            double worst = 0.0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];

                double min = double.PositiveInfinity;
                for (int pi = 0; pi < points.Count; pi++)
                {
                    double d = PolygonUtils.DistancePointToSegment(points[pi], a, b);
                    if (d < min) min = d;
                }
                if (min > worst) worst = min;
            }
            return worst;
        }
    }
}
