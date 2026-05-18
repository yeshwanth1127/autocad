using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Clustered-shaft zoning: exactly <c>N</c> parallel equal-area strips clipped to the floor boundary via
    /// axis-aligned slabs and Region intersection. Strip cut direction follows the shaft cluster footprint
    /// (cuts perpendicular to cluster elongation), not the floor bbox aspect ratio.
    /// </summary>
    public static class ClusteredEqualAreaStripZonesInPolygon2d
    {
        /// <summary>
        /// Builds <paramref name="zoneCount"/> zone rings using only <c>zoneCount - 1</c> parallel cuts.
        /// </summary>
        /// <param name="shaftSites">Deduped shaft insertion points used to pick vertical vs horizontal strips.</param>
        public static bool TryBuildZoneRings(
            Polyline boundary,
            IList<Point2d> shaftSites,
            int zoneCount,
            double tolerance,
            out List<List<Point2d>> rings,
            out bool splitVertical,
            out string errorMessage)
        {
            rings = new List<List<Point2d>>();
            splitVertical = true;
            errorMessage = null;

            if (boundary == null)
            {
                errorMessage = "Boundary is null.";
                return false;
            }

            if (zoneCount < 2)
            {
                errorMessage = "Need at least two zones.";
                return false;
            }

            List<Point2d> ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(boundary);
            if (ring == null || ring.Count < 3)
            {
                errorMessage = "Boundary must be a closed polygon with at least 3 vertices.";
                return false;
            }

            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var p in ring)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }

            double floorW = maxX - minX;
            double floorH = maxY - minY;
            double extent = Math.Max(floorW, floorH);
            double eps = Math.Max(tolerance, 1e-9 * Math.Max(extent, 1.0));

            splitVertical = ChooseStripOrientation(shaftSites, floorW, floorH, eps);

            if (splitVertical && floorW <= eps)
            {
                errorMessage = "Boundary has no usable width in X for vertical strips.";
                return false;
            }

            if (!splitVertical && floorH <= eps)
            {
                errorMessage = "Boundary has no usable height in Y for horizontal strips.";
                return false;
            }

            double floorPolygonArea = PolygonVerticalHalfPlaneClip2d.AbsArea(ring);
            if (floorPolygonArea <= eps * eps)
            {
                errorMessage = "Boundary area is too small to split.";
                return false;
            }

            double targetArea = floorPolygonArea / zoneCount;
            double marginLeftArea = Math.Max(0, (floorPolygonArea - zoneCount * targetArea) * 0.5);

            if (!TryComputeInteriorCuts(
                    ring, zoneCount, splitVertical, minX, maxX, minY, maxY, eps,
                    marginLeftArea, targetArea,
                    out double[] interiorCuts, out errorMessage))
                return false;

            double margin = extent * 2.0 + 10.0 * eps;
            if (!(margin > 0)) margin = 1000.0;

            if (!RegionBooleanIntersection2d.TryCreateBoundaryRegion(boundary, tolerance, out var boundaryRegion, out string regErr))
            {
                errorMessage = "Region creation failed: " + regErr;
                return false;
            }

            try
            {
                for (int strip = 0; strip < zoneCount; strip++)
                {
                    double a0 = strip == 0
                        ? (splitVertical ? minX - margin : minY - margin)
                        : interiorCuts[strip - 1];
                    double a1 = strip == zoneCount - 1
                        ? (splitVertical ? maxX + margin : maxY + margin)
                        : interiorCuts[strip];

                    Polyline slab = null;
                    try
                    {
                        if (splitVertical)
                            slab = RegionBooleanIntersection2d.MakeRectangleSlabOnBoundaryPlane(
                                boundary, a0, minY - margin, a1, maxY + margin);
                        else
                            slab = RegionBooleanIntersection2d.MakeRectangleSlabOnBoundaryPlane(
                                boundary, minX - margin, a0, maxX + margin, a1);

                        if (!RegionBooleanIntersection2d.TryIntersectBoundaryRegionWithSlabToRings(
                                boundaryRegion,
                                slab,
                                tolerance,
                                out var outRings,
                                out string clipErr))
                        {
                            errorMessage = "Strip " + (strip + 1).ToString(CultureInfo.InvariantCulture) +
                                           " region clip failed: " + (clipErr ?? "empty intersection");
                            return false;
                        }

                        if (outRings == null || outRings.Count == 0)
                        {
                            errorMessage = "Strip " + (strip + 1).ToString(CultureInfo.InvariantCulture) +
                                           " produced no outline.";
                            return false;
                        }

                        var best = SelectLargestAreaRing(outRings);
                        if (best == null || best.Count < 3)
                        {
                            errorMessage = "Strip " + (strip + 1).ToString(CultureInfo.InvariantCulture) +
                                           " outline is degenerate.";
                            return false;
                        }

                        rings.Add(best);
                    }
                    finally
                    {
                        try { slab?.Dispose(); } catch { /* ignore */ }
                    }
                }
            }
            finally
            {
                try { boundaryRegion.Dispose(); } catch { /* ignore */ }
            }

            if (rings.Count != zoneCount)
            {
                errorMessage = "Expected " + zoneCount.ToString(CultureInfo.InvariantCulture) +
                               " strip zones, got " + rings.Count.ToString(CultureInfo.InvariantCulture) + ".";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Vertical cuts (strips sweep in X) when the shaft cluster is wider than tall; horizontal strips when taller.
        /// Cuts run perpendicular to cluster elongation. Falls back to floor bbox only if shaft spread is degenerate.
        /// </summary>
        internal static bool ChooseStripOrientation(IList<Point2d> shaftSites, double floorW, double floorH, double eps)
        {
            if (shaftSites == null || shaftSites.Count < 2)
                return floorW >= floorH;

            double sMinX = double.MaxValue, sMaxX = double.MinValue, sMinY = double.MaxValue, sMaxY = double.MinValue;
            for (int i = 0; i < shaftSites.Count; i++)
            {
                var p = shaftSites[i];
                if (p.X < sMinX) sMinX = p.X;
                if (p.X > sMaxX) sMaxX = p.X;
                if (p.Y < sMinY) sMinY = p.Y;
                if (p.Y > sMaxY) sMaxY = p.Y;
            }

            double shaftSpreadX = sMaxX - sMinX;
            double shaftSpreadY = sMaxY - sMinY;

            if (shaftSpreadX <= eps && shaftSpreadY <= eps)
                return floorW >= floorH;

            return shaftSpreadX >= shaftSpreadY;
        }

        /// <summary>
        /// <c>zoneCount - 1</c> parallel cut coordinates (X if vertical strips, else Y) for equal cumulative area.
        /// </summary>
        private static bool TryComputeInteriorCuts(
            IList<Point2d> ring,
            int zoneCount,
            bool splitVertical,
            double minX,
            double maxX,
            double minY,
            double maxY,
            double eps,
            double marginLeftArea,
            double targetAreaPerZone,
            out double[] interiorCuts,
            out string errorMessage)
        {
            interiorCuts = new double[zoneCount - 1];
            errorMessage = null;

            for (int k = 0; k < zoneCount - 1; k++)
            {
                double targetCum = marginLeftArea + (k + 1) * targetAreaPerZone;
                if (splitVertical)
                {
                    double x = EqualAreaAxisStripZonesInPolygon2d.FindXForCumulativeLeftArea(
                        ring, minX, maxX, targetCum, eps);
                    if (double.IsNaN(x))
                    {
                        errorMessage = "Could not compute vertical strip cut " +
                                       (k + 1).ToString(CultureInfo.InvariantCulture) + ".";
                        return false;
                    }

                    interiorCuts[k] = x;
                }
                else
                {
                    double y = EqualAreaAxisStripZonesInPolygon2d.FindYForCumulativeBelowArea(
                        ring, minY, maxY, targetCum, eps);
                    if (double.IsNaN(y))
                    {
                        errorMessage = "Could not compute horizontal strip cut " +
                                       (k + 1).ToString(CultureInfo.InvariantCulture) + ".";
                        return false;
                    }

                    interiorCuts[k] = y;
                }
            }

            return true;
        }

        private static List<Point2d> SelectLargestAreaRing(IList<List<Point2d>> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return null;

            List<Point2d> best = null;
            double bestArea = -1.0;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c == null || c.Count < 3)
                    continue;
                double a = PolygonVerticalHalfPlaneClip2d.AbsArea(c);
                if (a > bestArea)
                {
                    bestArea = a;
                    best = c;
                }
            }

            return best;
        }
    }
}
