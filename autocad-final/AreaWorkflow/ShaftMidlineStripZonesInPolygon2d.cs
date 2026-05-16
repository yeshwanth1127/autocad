using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Zone implementation 2 (<c>SPRINKLERZONEAREA2</c>): equal-area axis-aligned strips with optional snapping to boundary
    /// corners and edge midpoints, clipped via Region boolean intersection (concave + multi-component).
    /// Snap search: if a corner or wall line lies within <see cref="SnapSearchMeters"/> of the ideal equal-area cut (in real-world distance), snap to it.
    /// </summary>
    public static class ShaftMidlineStripZonesInPolygon2d
    {
        /// <summary>Ideal cut may snap to a boundary corner or wall axis when within this distance (meters).</summary>
        public const double SnapSearchMeters = 7.0;

        /// <param name="snapSearchMeters">
        /// Max distance from ideal cut to snap to a boundary corner/wall (meters). Use 0 or negative to apply <see cref="SnapSearchMeters"/>.
        /// </param>
        public static bool TryBuildZoneRingsMulti(
            Database db,
            Polyline boundary,
            IList<FindShaftsInsideBoundary.ShaftBlockInfo> shafts,
            double tolerance,
            double snapSearchMeters,
            out List<List<Point2d>> rings,
            out List<int> ringShaftIdx,
            out bool splitVertical,
            out string errorMessage)
        {
            rings = new List<List<Point2d>>();
            ringShaftIdx = new List<int>();
            splitVertical = true;
            errorMessage = null;

            int n = shafts?.Count ?? 0;
            if (n < 2)
            {
                errorMessage = "Need at least two shafts for zones.";
                return false;
            }
            if (boundary == null)
            {
                errorMessage = "Boundary is null.";
                return false;
            }

            Extents3d ext;
            try
            {
                ext = boundary.GeometricExtents;
            }
            catch
            {
                errorMessage = "Could not read boundary extents.";
                return false;
            }

            double minX = ext.MinPoint.X, maxX = ext.MaxPoint.X;
            double minY = ext.MinPoint.Y, maxY = ext.MaxPoint.Y;
            double dx = maxX - minX, dy = maxY - minY;
            double extent = Math.Max(Math.Abs(dx), Math.Abs(dy));
            double eps = Math.Max(tolerance, 1e-9 * Math.Max(extent, 1.0));

            splitVertical = Math.Abs(dx) >= Math.Abs(dy);

            var sites = new List<Point2d>(n);
            for (int i = 0; i < n; i++)
                sites.Add(new Point2d(shafts[i].Position.X, shafts[i].Position.Y));

            var shaftOrder = new int[n];
            for (int i = 0; i < n; i++)
                shaftOrder[i] = i;

            if (!EqualAreaAxisStripZonesInPolygon2d.TryGetIdealInteriorStripCuts(
                    boundary,
                    sites,
                    n,
                    tolerance,
                    out var ring,
                    out double rMinX,
                    out double rMaxX,
                    out double rMinY,
                    out double rMaxY,
                    out double ringEps,
                    out double aTargetRing,
                    out double marginLeftArea,
                    out var idealCuts,
                    out bool eaVertical,
                    out string eaErr))
            {
                errorMessage = eaErr ?? "Equal-area cut computation failed.";
                return false;
            }

            splitVertical = eaVertical;
            bool orderVertical = splitVertical;
            Array.Sort(shaftOrder, (a, b) =>
            {
                double va = orderVertical ? sites[a].X : sites[a].Y;
                double vb = orderVertical ? sites[b].X : sites[b].Y;
                int c = va.CompareTo(vb);
                return c != 0 ? c : a.CompareTo(b);
            });

            double axisEps = Math.Max(ringEps, 1e-6 * Math.Max(extent, 1.0));
            var candidates = CollectTaggedBoundaryAxisSnapCandidates(ring, splitVertical, axisEps, ringEps);

            double snapM = snapSearchMeters > 0 ? snapSearchMeters : SnapSearchMeters;
            double snapRadiusDu = ComputeSnapRadiusDrawingUnits(db, ringEps, snapM);

            double[] cuts = SnapInteriorCutsToBoundary(
                idealCuts,
                ring,
                splitVertical,
                ringEps,
                marginLeftArea,
                aTargetRing,
                candidates,
                snapRadiusDu,
                rMinX,
                rMaxX,
                rMinY,
                rMaxY);

            double margin = extent * 2.0 + 10.0 * eps;
            if (!(margin > 0)) margin = 1000.0;

            if (!RegionBooleanIntersection2d.TryCreateBoundaryRegion(boundary, tolerance, out var boundaryRegion, out string regErr))
            {
                errorMessage = "Region creation failed: " + regErr;
                return false;
            }

            try
            {
                for (int strip = 0; strip < n; strip++)
                {
                    double a0 = strip == 0
                        ? (splitVertical ? (minX - margin) : (minY - margin))
                        : cuts[strip - 1];
                    double a1 = strip == n - 1
                        ? (splitVertical ? (maxX + margin) : (maxY + margin))
                        : cuts[strip];

                    Polyline slab = null;
                    try
                    {
                        if (splitVertical)
                            slab = RegionBooleanIntersection2d.MakeRectangleSlabOnBoundaryPlane(boundary, a0, minY - margin, a1, maxY + margin);
                        else
                            slab = RegionBooleanIntersection2d.MakeRectangleSlabOnBoundaryPlane(boundary, minX - margin, a0, maxX + margin, a1);

                        if (!RegionBooleanIntersection2d.TryIntersectBoundaryRegionWithSlabToRings(
                                boundaryRegion,
                                slab,
                                tolerance,
                                out var outRings,
                                out string clipErr))
                        {
                            errorMessage = "Strip " + (strip + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                           " region clip failed: " + (clipErr ?? "empty intersection");
                            return false;
                        }

                        int shaftIndex = shaftOrder[strip];
                        var selected = SelectRingForShaft(outRings, sites[shaftIndex], tolerance);
                        if (selected == null || selected.Count < 3)
                        {
                            errorMessage = "A snapped strip did not contain the expected shaft, so zone count would not match shaft count.";
                            return false;
                        }

                        rings.Add(selected);
                        ringShaftIdx.Add(shaftIndex);
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

            if (rings.Count == 0)
            {
                errorMessage = "No zones produced (intersection returned empty).";
                return false;
            }

            if (rings.Count != n)
            {
                errorMessage = "Zone count mismatch after strip snapping: expected " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                               " zones for " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                               " shafts, actual zone count: " + rings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
                return false;
            }

            errorMessage = (splitVertical ? "Strips: vertical." : "Strips: horizontal.") +
                           " Snap within " + snapM.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                           " m of ideal cut to corners/wall lines when possible (Region clipping).";
            return true;
        }

        private static List<Point2d> SelectRingForShaft(IList<List<Point2d>> rings, Point2d shaft, double tol)
        {
            if (rings == null || rings.Count == 0)
                return null;

            List<Point2d> best = null;
            double bestArea = double.NegativeInfinity;
            double eps = Math.Max(tol, 1e-9);
            foreach (var ring in rings)
            {
                if (ring == null || ring.Count < 3)
                    continue;

                double area = PolygonVerticalHalfPlaneClip2d.AbsArea(ring);
                if (PointInPolygonOrOnEdge(ring, shaft, eps))
                    return ring;

                if (area > bestArea)
                {
                    bestArea = area;
                    best = ring;
                }
            }

            return best;
        }

        private static bool PointInPolygonOrOnEdge(IList<Point2d> poly, Point2d p, double tol)
        {
            if (poly == null || poly.Count < 3)
                return false;

            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                if (DistancePointToSegment(p, poly[j], poly[i]) <= tol)
                    return true;
            }

            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i];
                var b = poly[j];
                bool crosses = ((a.Y > p.Y) != (b.Y > p.Y));
                if (!crosses)
                    continue;

                double denom = b.Y - a.Y;
                if (Math.Abs(denom) <= 1e-12)
                    continue;

                double xInt = (b.X - a.X) * (p.Y - a.Y) / denom + a.X;
                if (p.X < xInt)
                    inside = !inside;
            }

            return inside;
        }

        private static double DistancePointToSegment(Point2d p, Point2d a, Point2d b)
        {
            double vx = b.X - a.X, vy = b.Y - a.Y;
            double wx = p.X - a.X, wy = p.Y - a.Y;
            double c1 = vx * wx + vy * wy;
            if (c1 <= 0)
                return Math.Sqrt(wx * wx + wy * wy);

            double c2 = vx * vx + vy * vy;
            if (c2 <= 0)
                return Math.Sqrt(wx * wx + wy * wy);

            double t = c1 / c2;
            if (t >= 1)
            {
                double dx = p.X - b.X, dy = p.Y - b.Y;
                return Math.Sqrt(dx * dx + dy * dy);
            }

            double projX = a.X + t * vx;
            double projY = a.Y + t * vy;
            double px = p.X - projX, py = p.Y - projY;
            return Math.Sqrt(px * px + py * py);
        }

        /// <summary>Backward-compatible overload using <see cref="SnapSearchMeters"/>.</summary>
        public static bool TryBuildZoneRingsMulti(
            Database db,
            Polyline boundary,
            IList<FindShaftsInsideBoundary.ShaftBlockInfo> shafts,
            double tolerance,
            out List<List<Point2d>> rings,
            out List<int> ringShaftIdx,
            out bool splitVertical,
            out string errorMessage)
            => TryBuildZoneRingsMulti(db, boundary, shafts, tolerance, 0, out rings, out ringShaftIdx, out splitVertical, out errorMessage);

        /// <summary>
        /// Distance threshold from ideal cut to a candidate (meters), converted via INSUNITS when supported.
        /// </summary>
        private static double ComputeSnapRadiusDrawingUnits(Database db, double ringEps, double snapSearchMeters)
        {
            try
            {
                if (db != null &&
                    DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, snapSearchMeters, out double du) &&
                    du > ringEps)
                    return du;
            }
            catch
            {
                // ignore
            }

            return Math.Max(8.0 * ringEps, 1.0);
        }

        /// <summary>Vertex / wall-axis vs span midpoint — structural snaps align to corners and wall lines.</summary>
        private enum SnapFeatureKind
        {
            WallSpanMid = 0,
            WallAxis = 1,
            Vertex = 2
        }

        private readonly struct AxisSnapCandidate
        {
            public readonly double Value;
            public readonly SnapFeatureKind Kind;

            public AxisSnapCandidate(double value, SnapFeatureKind kind)
            {
                Value = value;
                Kind = kind;
            }
        }

        /// <summary>
        /// Polygon vertices (corners), axis-aligned wall lines, and span midpoints — tagged for priority.
        /// </summary>
        private static List<AxisSnapCandidate> CollectTaggedBoundaryAxisSnapCandidates(
            IList<Point2d> ring,
            bool splitVertical,
            double axisEps,
            double tol)
        {
            var raw = new List<AxisSnapCandidate>();
            int nv = ring.Count;
            for (int i = 0; i < nv; i++)
            {
                var p0 = ring[i];
                raw.Add(new AxisSnapCandidate(splitVertical ? p0.X : p0.Y, SnapFeatureKind.Vertex));
            }

            for (int i = 0; i < nv; i++)
            {
                var p0 = ring[i];
                var p1 = ring[(i + 1) % nv];
                double dx = p1.X - p0.X;
                double dy = p1.Y - p0.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len <= tol * 0.01)
                    continue;

                if (splitVertical)
                {
                    if (Math.Abs(dx) <= axisEps && Math.Abs(dy) > axisEps)
                        raw.Add(new AxisSnapCandidate(p0.X, SnapFeatureKind.WallAxis));
                    else if (Math.Abs(dy) <= axisEps && Math.Abs(dx) > axisEps)
                        raw.Add(new AxisSnapCandidate(0.5 * (p0.X + p1.X), SnapFeatureKind.WallSpanMid));
                }
                else
                {
                    if (Math.Abs(dy) <= axisEps && Math.Abs(dx) > axisEps)
                        raw.Add(new AxisSnapCandidate(p0.Y, SnapFeatureKind.WallAxis));
                    else if (Math.Abs(dx) <= axisEps && Math.Abs(dy) > axisEps)
                        raw.Add(new AxisSnapCandidate(0.5 * (p0.Y + p1.Y), SnapFeatureKind.WallSpanMid));
                }
            }

            raw.Sort((a, b) => a.Value.CompareTo(b.Value));

            var uniq = new List<AxisSnapCandidate>();
            foreach (var item in raw)
            {
                if (uniq.Count == 0)
                {
                    uniq.Add(item);
                    continue;
                }

                var last = uniq[uniq.Count - 1];
                if (Math.Abs(item.Value - last.Value) <= tol * 0.5)
                {
                    if ((int)item.Kind > (int)last.Kind)
                        uniq[uniq.Count - 1] = item;
                }
                else
                {
                    uniq.Add(item);
                }
            }

            return uniq;
        }

        private static bool IsStructuralKind(SnapFeatureKind k) =>
            k == SnapFeatureKind.Vertex || k == SnapFeatureKind.WallAxis;

        /// <summary>
        /// Picks snapped interior cuts. When corners or wall axes fall in range, those are preferred over
        /// span midpoints so dividers align with architecture (e.g. inner leg of a U-shape). Otherwise minimizes
        /// cumulative area error vs equal-area target, then distance to ideal cut.
        /// </summary>
        private static double[] SnapInteriorCutsToBoundary(
            double[] idealCuts,
            IList<Point2d> ring,
            bool splitVertical,
            double eps,
            double marginLeftArea,
            double aTargetRing,
            List<AxisSnapCandidate> sortedCandidates,
            double snapRadius,
            double rMinX,
            double rMaxX,
            double rMinY,
            double rMaxY)
        {
            int m = idealCuts.Length;
            var result = new double[m];
            double minA = splitVertical ? rMinX : rMinY;
            double maxA = splitVertical ? rMaxX : rMaxY;
            double span = Math.Max(maxA - minA, eps);
            double minSep = Math.Max(eps * 8.0, 1e-8 * span);
            double edgePad = Math.Min(eps * 10.0, minSep * 0.25);

            for (int k = 0; k < m; k++)
            {
                double targetArea = marginLeftArea + (k + 1) * aTargetRing;
                double lo = k == 0 ? minA + edgePad : result[k - 1] + minSep;
                double hi = maxA - eps - minSep * (m - 1 - k);
                if (hi <= lo + eps)
                {
                    double mid = 0.5 * (lo + hi);
                    result[k] = mid;
                    continue;
                }

                // Same 7 m (drawing-units) band for generic candidates and structural (corners / wall axes).
                double winLo = Math.Max(idealCuts[k] - snapRadius, lo);
                double winHi = Math.Min(idealCuts[k] + snapRadius, hi);
                if (winHi < winLo)
                    winLo = lo;

                double structWinLo = Math.Max(idealCuts[k] - snapRadius, lo);
                double structWinHi = Math.Min(idealCuts[k] + snapRadius, hi);

                var tryList = new List<double>();
                var structuralInRange = new List<double>();
                foreach (var cand in sortedCandidates)
                {
                    double c = cand.Value;
                    if (c < lo - 1e-12 || c > hi + 1e-12)
                        continue;
                    if (c >= winLo - 1e-12 && c <= winHi + 1e-12)
                        tryList.Add(c);
                    if (IsStructuralKind(cand.Kind) && c >= structWinLo - 1e-12 && c <= structWinHi + 1e-12)
                        structuralInRange.Add(c);
                }

                double id = idealCuts[k];
                if (id >= lo - 1e-12 && id <= hi + 1e-12)
                    tryList.Add(id);

                structuralInRange.Sort();
                var mergedStructural = new List<double>();
                double prevS = double.NaN;
                foreach (double c in structuralInRange)
                {
                    if (double.IsNaN(prevS) || Math.Abs(c - prevS) > eps * 0.5)
                    {
                        mergedStructural.Add(c);
                        prevS = c;
                    }
                }

                if (tryList.Count == 0 && mergedStructural.Count == 0)
                {
                    double clamped = id;
                    if (clamped < lo) clamped = lo;
                    if (clamped > hi) clamped = hi;
                    result[k] = clamped;
                    continue;
                }

                tryList.Sort();

                // Prefer corners and axis-aligned wall lines over span midpoints when any structural point is near the ideal cut.
                List<double> evaluate;
                if (mergedStructural.Count > 0)
                    evaluate = mergedStructural;
                else
                    evaluate = tryList;

                double bestC = evaluate[0];
                double bestErr = double.MaxValue;
                double bestDist = double.MaxValue;
                foreach (double c in evaluate)
                {
                    double a = EqualAreaAxisStripZonesInPolygon2d.CumulativeAreaAtAxisCoordinate(ring, splitVertical, c, eps);
                    double err = Math.Abs(a - targetArea);
                    double dist = Math.Abs(c - idealCuts[k]);
                    if (err < bestErr - 1e-15 || (Math.Abs(err - bestErr) <= 1e-15 && dist < bestDist))
                    {
                        bestErr = err;
                        bestDist = dist;
                        bestC = c;
                    }
                }

                result[k] = bestC;
            }

            return result;
        }
    }
}
