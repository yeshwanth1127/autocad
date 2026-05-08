using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Splits a closed floor polygon into N equal-area strips along WCS X (vertical cut lines) or WCS Y (horizontal cut lines),
    /// choosing the axis with the larger bounding-box extent (tie: vertical). Strips are returned in sweep order
    /// (left→right or bottom→top). Shafts are paired by sorting deduped sites on that axis and matching strip i to shaft i in that order.
    /// </summary>
    public static class EqualAreaAxisStripZonesInPolygon2d
    {
        /// <summary>Human-readable summary for the command line / palette.</summary>
        public static string FormatStripZoningSummary(double? floorM2, int shaftCount, bool splitVertical, double targetAreaDrawingUnits)
        {
            var sb = new StringBuilder();
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "Equal-area strips ({0} zones): cuts along {1}; each zone ≈ {2:F2} sq. drawing units. Shafts paired by sorted {3} (sweep order ↔ shaft order).",
                shaftCount,
                splitVertical ? "vertical lines (X)" : "horizontal lines (Y)",
                targetAreaDrawingUnits,
                splitVertical ? "X" : "Y");
            if (floorM2.HasValue)
                sb.AppendFormat(CultureInfo.InvariantCulture, " Floor: {0:F2} m².", floorM2.Value);
            return sb.ToString();
        }

        /// <summary>
        /// Computes ideal equal-area interior cut coordinates (one between each adjacent pair of zones), using the same
        /// ring sampling and bisection as <see cref="TryBuildZoneRings"/>. Used by zone implementation 2 (Region strips + boundary snap).
        /// </summary>
        /// <param name="interiorCuts">Length <c>zoneCount - 1</c>; sweep-order coordinate (X if vertical strips, else Y).</param>
        /// <param name="aTargetRing">Equal target area per zone in drawing units².</param>
        /// <param name="marginLeftArea">Left margin used to center floating-point remainder (same as strip builder).</param>
        public static bool TryGetIdealInteriorStripCuts(
            Polyline boundary,
            IList<Point2d> shaftSites,
            int zoneCount,
            double tol,
            out List<Point2d> ring,
            out double minX,
            out double maxX,
            out double minY,
            out double maxY,
            out double eps,
            out double aTargetRing,
            out double marginLeftArea,
            out double[] interiorCuts,
            out bool splitVertical,
            out string errorMessage)
        {
            ring = null;
            minX = maxX = minY = maxY = eps = aTargetRing = marginLeftArea = 0;
            interiorCuts = null;
            splitVertical = true;
            errorMessage = null;

            int nShaft = shaftSites?.Count ?? 0;
            if (zoneCount < 2)
            {
                errorMessage = "Need at least two zones (shafts).";
                return false;
            }
            if (nShaft != zoneCount)
            {
                errorMessage = "Shaft site count must match zone count.";
                return false;
            }

            ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(boundary);
            if (ring.Count < 3)
            {
                errorMessage = "Boundary must be a closed polygon with enough detail (at least 3 vertices).";
                return false;
            }

            minX = double.MaxValue;
            maxX = double.MinValue;
            minY = double.MaxValue;
            maxY = double.MinValue;
            foreach (var p in ring)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }

            double width = maxX - minX;
            double height = maxY - minY;
            double extent = Math.Max(width, height);
            eps = Math.Max(tol, 1e-9 * Math.Max(extent, 1.0));

            bool useVertical = width >= height;
            splitVertical = useVertical;

            if (useVertical && width <= eps)
            {
                errorMessage = "Boundary has no usable width in X for vertical strips.";
                return false;
            }
            if (!useVertical && height <= eps)
            {
                errorMessage = "Boundary has no usable height in Y for horizontal strips.";
                return false;
            }

            double ringArea = PolygonVerticalHalfPlaneClip2d.AbsArea(ring);
            if (ringArea <= eps * eps)
            {
                errorMessage = "Boundary area is too small to split.";
                return false;
            }

            aTargetRing = ringArea / zoneCount;
            marginLeftArea = Math.Max(0, (ringArea - zoneCount * aTargetRing) * 0.5);

            interiorCuts = new double[zoneCount - 1];
            for (int k = 0; k < zoneCount - 1; k++)
            {
                double targetCum = marginLeftArea + (k + 1) * aTargetRing;
                if (useVertical)
                {
                    double x = FindXForCumulativeLeftArea(ring, minX, maxX, targetCum, eps);
                    if (double.IsNaN(x))
                    {
                        errorMessage = "Could not compute vertical cut lines for this boundary.";
                        return false;
                    }
                    interiorCuts[k] = x;
                }
                else
                {
                    double y = FindYForCumulativeBelowArea(ring, minY, maxY, targetCum, eps);
                    if (double.IsNaN(y))
                    {
                        errorMessage = "Could not compute horizontal cut lines for this boundary.";
                        return false;
                    }
                    interiorCuts[k] = y;
                }
            }

            return true;
        }

        /// <summary>Cumulative polygon area to the left of <paramref name="x"/> (vertical strips) or below <paramref name="y"/> (horizontal).</summary>
        internal static double CumulativeAreaAtAxisCoordinate(
            IList<Point2d> ring,
            bool splitVertical,
            double coordinate,
            double eps)
        {
            return splitVertical
                ? AreaLeftOfX(ring, coordinate, eps)
                : AreaBelowY(ring, coordinate, eps);
        }

        /// <summary>
        /// Builds N closed rings in sweep order and parallel list mapping strip i → deduped shaft index <c>shaftOrder[i]</c>.
        /// </summary>
        public static bool TryBuildZoneRings(
            Polyline boundary,
            IList<Point2d> shaftSites,
            int zoneCount,
            double tol,
            out List<List<Point2d>> rings,
            out List<int> ringShaftIndex,
            out bool splitVertical,
            out string errorMessage)
        {
            rings = new List<List<Point2d>>();
            ringShaftIndex = new List<int>();
            splitVertical = true;
            errorMessage = null;

            int nShaft = shaftSites?.Count ?? 0;
            if (zoneCount < 2)
            {
                errorMessage = "Need at least two zones (shafts).";
                return false;
            }

            if (nShaft != zoneCount)
            {
                errorMessage = "Shaft site count must match zone count.";
                return false;
            }

            List<Point2d> ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(boundary);
            if (ring.Count < 3)
            {
                errorMessage = "Boundary must be a closed polygon with enough detail (at least 3 vertices).";
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

            double width = maxX - minX;
            double height = maxY - minY;
            double extent = Math.Max(width, height);
            double eps = Math.Max(tol, 1e-9 * Math.Max(extent, 1.0));

            bool useVertical = width >= height;
            splitVertical = useVertical;

            if (useVertical && width <= eps)
            {
                errorMessage = "Boundary has no usable width in X for vertical strips.";
                return false;
            }

            if (!useVertical && height <= eps)
            {
                errorMessage = "Boundary has no usable height in Y for horizontal strips.";
                return false;
            }

            double ringArea = PolygonVerticalHalfPlaneClip2d.AbsArea(ring);
            if (ringArea <= eps * eps)
            {
                errorMessage = "Boundary area is too small to split.";
                return false;
            }

            // Targets use the sampled ring area (same total as clips); margin centers any float remainder vs N equal parts.
            double aTargetRing = ringArea / zoneCount;
            double marginLeftArea = Math.Max(0, (ringArea - zoneCount * aTargetRing) * 0.5);

            var shaftOrder = new int[zoneCount];
            for (int i = 0; i < zoneCount; i++)
                shaftOrder[i] = i;
            Array.Sort(shaftOrder, (a, b) =>
            {
                double va = useVertical ? shaftSites[a].X : shaftSites[a].Y;
                double vb = useVertical ? shaftSites[b].X : shaftSites[b].Y;
                int c = va.CompareTo(vb);
                return c != 0 ? c : a.CompareTo(b);
            });

            for (int k = 0; k < zoneCount; k++)
            {
                double cumLeft = marginLeftArea + k * aTargetRing;
                double cumRight = marginLeftArea + (k + 1) * aTargetRing;
                List<Point2d> band;
                if (useVertical)
                {
                    double xLeft = FindXForCumulativeLeftArea(ring, minX, maxX, cumLeft, eps);
                    double xRight = FindXForCumulativeLeftArea(ring, minX, maxX, cumRight, eps);
                    if (double.IsNaN(xLeft) || double.IsNaN(xRight))
                    {
                        errorMessage = "Could not compute vertical cut lines for this boundary.";
                        return false;
                    }

                    band = PolygonVerticalHalfPlaneClip2d.ClipKeepXGreaterOrEqual(ring, xLeft, eps);
                    band = PolygonVerticalHalfPlaneClip2d.ClipKeepXLessOrEqual(band, xRight, eps);
                }
                else
                {
                    double yBottom = FindYForCumulativeBelowArea(ring, minY, maxY, cumLeft, eps);
                    double yTop = FindYForCumulativeBelowArea(ring, minY, maxY, cumRight, eps);
                    if (double.IsNaN(yBottom) || double.IsNaN(yTop))
                    {
                        errorMessage = "Could not compute horizontal cut lines for this boundary.";
                        return false;
                    }

                    band = PolygonHorizontalHalfPlaneClip2d.ClipKeepYGreaterOrEqual(ring, yBottom, eps);
                    band = PolygonHorizontalHalfPlaneClip2d.ClipKeepYLessOrEqual(band, yTop, eps);
                }

                if (band.Count < 3)
                {
                    errorMessage = "A zone strip collapsed to a degenerate polygon (boundary may be too narrow between cuts).";
                    return false;
                }

                rings.Add(band);
                ringShaftIndex.Add(shaftOrder[k]);
            }

            return true;
        }

        internal static double AreaLeftOfX(IList<Point2d> ring, double x, double eps)
        {
            var clipped = PolygonVerticalHalfPlaneClip2d.ClipKeepXLessOrEqual(ring, x, eps);
            return PolygonVerticalHalfPlaneClip2d.AbsArea(clipped);
        }

        internal static double AreaBelowY(IList<Point2d> ring, double y, double eps)
        {
            var clipped = PolygonHorizontalHalfPlaneClip2d.ClipKeepYLessOrEqual(ring, y, eps);
            return PolygonVerticalHalfPlaneClip2d.AbsArea(clipped);
        }

        internal static double FindXForCumulativeLeftArea(IList<Point2d> ring, double minX, double maxX, double targetArea, double eps)
        {
            double lo = minX;
            double hi = maxX;
            double aLo = AreaLeftOfX(ring, lo, eps);
            double aHi = AreaLeftOfX(ring, hi, eps);
            if (targetArea <= aLo + 1e-12)
                return lo;
            if (targetArea >= aHi - 1e-12)
                return hi;
            if (targetArea < aLo || targetArea > aHi)
                return double.NaN;

            double areaTol = Math.Max(eps * eps * 100, 1e-9 * (aHi - aLo));
            for (int iter = 0; iter < 80; iter++)
            {
                double mid = 0.5 * (lo + hi);
                double am = AreaLeftOfX(ring, mid, eps);
                if (Math.Abs(am - targetArea) <= areaTol)
                    return mid;
                if (am < targetArea)
                    lo = mid;
                else
                    hi = mid;
                if (hi - lo <= eps * 0.01)
                    return mid;
            }

            return 0.5 * (lo + hi);
        }

        internal static double FindYForCumulativeBelowArea(IList<Point2d> ring, double minY, double maxY, double targetArea, double eps)
        {
            double lo = minY;
            double hi = maxY;
            double aLo = AreaBelowY(ring, lo, eps);
            double aHi = AreaBelowY(ring, hi, eps);
            if (targetArea <= aLo + 1e-12)
                return lo;
            if (targetArea >= aHi - 1e-12)
                return hi;
            if (targetArea < aLo || targetArea > aHi)
                return double.NaN;

            double areaTol = Math.Max(eps * eps * 100, 1e-9 * (aHi - aLo));
            for (int iter = 0; iter < 80; iter++)
            {
                double mid = 0.5 * (lo + hi);
                double am = AreaBelowY(ring, mid, eps);
                if (Math.Abs(am - targetArea) <= areaTol)
                    return mid;
                if (am < targetArea)
                    lo = mid;
                else
                    hi = mid;
                if (hi - lo <= eps * 0.01)
                    return mid;
            }

            return 0.5 * (lo + hi);
        }
    }
}
