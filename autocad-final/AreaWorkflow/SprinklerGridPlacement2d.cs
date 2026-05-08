using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Agent;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    public static class SprinklerGridPlacement2d
    {
        public sealed class PlacementResult
        {
            public List<Point2d> Sprinklers;
            /// <summary>Heads on the +perpendicular side of the trunk (e.g. above a horizontal main).</summary>
            public List<Point2d> AboveSprinklers;
            /// <summary>Heads on the −perpendicular side of the trunk (e.g. below a horizontal main).</summary>
            public List<Point2d> BelowSprinklers;
            public bool UsedOffsetPolygon;
            public bool CoverageOk;
            public string Summary;
        }

        /// <summary>
        /// Nudges existing sprinkler points that sit on/near the boundary inward along their current grid axis.
        /// </summary>
        public static bool TrySnapExistingSprinklersInsideBoundary(
            List<Point2d> zoneRing,
            Database db,
            List<Point2d> sprinklers,
            double spacingMeters,
            out List<Point2d> snapped,
            out int movedCount,
            out string errorMessage)
        {
            snapped = sprinklers == null ? new List<Point2d>() : new List<Point2d>(sprinklers);
            movedCount = 0;
            errorMessage = null;

            if (zoneRing == null || zoneRing.Count < 3)
            {
                errorMessage = "Zone polygon is invalid.";
                return false;
            }
            if (db == null)
            {
                errorMessage = "Database is null.";
                return false;
            }
            if (sprinklers == null || sprinklers.Count == 0)
                return true;

            GetExtents(zoneRing, out double minX, out double minY, out double maxX, out double maxY);
            double extent = Math.Max(maxX - minX, maxY - minY);
            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            double eps = Math.Max(tol, 1e-9 * Math.Max(extent, 1.0));

            if (!DrawingUnitsHelper.TryAutoGetDrawingScale(db.Insunits, spacingMeters, extent, out double duPerMeter) || duPerMeter <= 0)
            {
                errorMessage = "Could not determine drawing scale (check INSUNITS).";
                return false;
            }

            double spacingDu = spacingMeters * duPerMeter;
            double minPlacementClearanceDu = Math.Max(eps * 5.0, 0.05 * duPerMeter);
            double strictInteriorInsetDu = ComputeStrictInteriorInsetDu(minPlacementClearanceDu, extent, eps);

            var nudged = NudgeBoundarySprinklersTowardGrid(
                zoneRing,
                snapped,
                spacingDu,
                strictInteriorInsetDu,
                eps,
                keepouts: null);

            movedCount = CountMovedPoints(snapped, nudged, Math.Max(eps * 8.0, spacingDu * 1e-6));
            snapped = nudged;
            return true;
        }

        /// <summary>
        /// Like <see cref="TryPlaceForZoneRing"/> but anchors the grid phase to a straight axis-aligned trunk.
        /// For a horizontal trunk at Y=<paramref name="trunkAxis"/>, sprinkler rows are placed at Y = trunkAxis ± coverageRadius + k·spacing.
        /// For a vertical trunk, columns are placed at X = trunkAxis ± coverageRadius + k·spacing.
        /// </summary>
        public static bool TryPlaceForZoneRingAnchoredToTrunk(
            List<Point2d> zoneRing,
            Database db,
            double spacingMeters,
            double coverageRadiusMeters,
            double maxBoundarySprinklerGapMeters,
            bool trunkHorizontal,
            double trunkAxis,
            out PlacementResult result,
            out string errorMessage,
            double gridAnchorOffsetXMeters = 0,
            double gridAnchorOffsetYMeters = 0)
        {
            result = null;
            errorMessage = null;

            if (zoneRing == null || zoneRing.Count < 3)
            {
                errorMessage = "Zone polygon is invalid.";
                return false;
            }

            if (db == null)
            {
                errorMessage = "Database is null.";
                return false;
            }

            GetExtents(zoneRing, out double minX, out double minY, out double maxX, out double maxY);
            double extent = Math.Max(maxX - minX, maxY - minY);
            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            double eps = Math.Max(tol, 1e-9 * Math.Max(extent, 1.0));

            if (!DrawingUnitsHelper.TryAutoGetDrawingScale(db.Insunits, spacingMeters, extent, out double duPerMeter) || duPerMeter <= 0)
            {
                errorMessage = "Could not determine drawing scale (check INSUNITS).";
                return false;
            }
            double spacingDu = spacingMeters * duPerMeter;
            double radiusDu  = coverageRadiusMeters * duPerMeter;
            double maxGapDu  = maxBoundarySprinklerGapMeters * duPerMeter;
            double maxGapHardLimitDu = RuntimeSettings.Load().SprinklerToBoundaryDistanceM * duPerMeter;
            double effectiveMaxGapDu = Math.Min(maxGapDu, maxGapHardLimitDu);

            if (spacingDu > 0)
            {
                double cellsX = (maxX - minX) / spacingDu;
                double cellsY = (maxY - minY) / spacingDu;
                double expectedCells = cellsX * cellsY;
                if (expectedCells > 200_000 || cellsX > 2000 || cellsY > 2000)
                {
                    errorMessage =
                        "Zone extent vs spacing would generate ~" +
                        expectedCells.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
                        " grid cells (cap 200,000). Check INSUNITS or increase spacing_m.";
                    AgentLog.Write("TryPlaceForZoneRingAnchoredToTrunk", "REJECT grid-cap: expectedCells=" + expectedCells.ToString("F0"));
                    return false;
                }
            }

            double minPlacementClearanceDu = eps * 5;
            double mpc = 0.05 * duPerMeter;
            if (mpc > 0) minPlacementClearanceDu = Math.Max(minPlacementClearanceDu, mpc);
            double strictInteriorInsetDu = ComputeStrictInteriorInsetDu(minPlacementClearanceDu, extent, eps);
            strictInteriorInsetDu = Math.Max(strictInteriorInsetDu, GetSprinklerVisualInsetDu(db, eps));

            double keepoutPadDu = Math.Max(eps * 10.0, spacingDu * 0.05) / 1.5;
            var keepouts = CollectShaftKeepouts(db, zoneRing, keepoutPadDu);

            double offsetXDu = gridAnchorOffsetXMeters * duPerMeter;
            double offsetYDu = gridAnchorOffsetYMeters * duPerMeter;

            var best = PlaceTrunkAnchoredGridInRing(
                zoneRing,
                spacingDu,
                eps,
                strictInteriorInsetDu,
                keepouts,
                trunkHorizontal,
                trunkAxis,
                radiusDu,
                extraAlongTrunkNeg: 0,
                extraAlongTrunkPos: 0,
                gridAnchorOffsetXDu: offsetXDu,
                gridAnchorOffsetYDu: offsetYDu);

            bool bestOk = CoverageOk(zoneRing, best, radiusDu, eps, keepouts);
            if (!bestOk)
            {
                var candidates = new List<(int n, int p)>
                {
                    (1,0),(0,1),
                    (1,1),
                    (2,0),(0,2)
                };
                foreach (var c in candidates)
                {
                    var pts = PlaceTrunkAnchoredGridInRing(
                        zoneRing,
                        spacingDu,
                        eps,
                        strictInteriorInsetDu,
                        keepouts,
                        trunkHorizontal,
                        trunkAxis,
                        radiusDu,
                        extraAlongTrunkNeg: c.n,
                        extraAlongTrunkPos: c.p,
                        gridAnchorOffsetXDu: offsetXDu,
                        gridAnchorOffsetYDu: offsetYDu);
                    if (pts.Count == 0) continue;
                    bool ok = CoverageOk(zoneRing, pts, radiusDu, eps, keepouts);
                    if (ok || pts.Count > best.Count)
                    {
                        best = pts;
                        bestOk = ok;
                        if (ok) break;
                    }
                }
            }

            if (best.Count == 0)
            {
                if (!TryFindPointWithClearance(zoneRing, strictInteriorInsetDu, eps, keepouts, out var p))
                {
                    errorMessage = "Zone is too narrow to place any sprinklers.";
                    return false;
                }
                best.Add(p);
            }

            var beforeBoundaryNudge = new List<Point2d>(best);
            best = NudgeBoundarySprinklersTowardGrid(zoneRing, best, spacingDu, strictInteriorInsetDu, eps, keepouts);
            int boundaryNudgedCount = CountMovedPoints(beforeBoundaryNudge, best, Math.Max(eps * 8.0, spacingDu * 1e-6));

            best = FilterToStrictInterior(zoneRing, best, strictInteriorInsetDu, eps);
            best = EnforceGridLatticePhase(zoneRing, best, spacingDu, strictInteriorInsetDu, eps, keepouts);
            if (best.Count == 0)
            {
                errorMessage = "Sprinkler placement produced no valid points.";
                return false;
            }

            bestOk = CoverageOk(zoneRing, best, radiusDu, eps, keepouts);

            result = new PlacementResult
            {
                Sprinklers = best,
                AboveSprinklers = null,
                BelowSprinklers = null,
                UsedOffsetPolygon = false,
                CoverageOk = bestOk,
                Summary = "Placed trunk-anchored grid. Boundary snap moved " +
                          boundaryNudgedCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " heads inward."
            };
            return true;
        }

        /// <summary>
        /// Places sprinklers inside a zone ring using a centered WCS grid. After placement, adds sprinklers so that
        /// every sampled boundary point has a head within <paramref name="maxBoundarySprinklerGapMeters"/> (extra columns/rows near walls when needed).
        /// </summary>
        public static bool TryPlaceForZoneRing(
            List<Point2d> zoneRing,
            Database db,
            double spacingMeters,
            double coverageRadiusMeters,
            double maxBoundarySprinklerGapMeters,
            out PlacementResult result,
            out string errorMessage,
            double gridAnchorOffsetXMeters = 0,
            double gridAnchorOffsetYMeters = 0,
            bool alignGridToBoundary = false)
        {
            result = null;
            errorMessage = null;

            if (zoneRing == null || zoneRing.Count < 3)
            {
                errorMessage = "Zone polygon is invalid.";
                return false;
            }

            if (db == null)
            {
                errorMessage = "Database is null.";
                return false;
            }

            // Compute extents first so the zone size can guide unit auto-detection.
            GetExtents(zoneRing, out double minX, out double minY, out double maxX, out double maxY);
            double extent = Math.Max(maxX - minX, maxY - minY);
            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            double eps = Math.Max(tol, 1e-9 * Math.Max(extent, 1.0));

            // Auto-detect drawing scale from zone extent so the grid is correct even when
            // INSUNITS doesn't match the actual coordinate scale of the drawing.
            if (!DrawingUnitsHelper.TryAutoGetDrawingScale(db.Insunits, spacingMeters, extent, out double duPerMeter) || duPerMeter <= 0)
            {
                errorMessage = "Could not determine drawing scale (check INSUNITS).";
                return false;
            }
            double spacingDu = spacingMeters * duPerMeter;
            double radiusDu  = coverageRadiusMeters * duPerMeter;
            double maxGapDu  = maxBoundarySprinklerGapMeters * duPerMeter;
            double maxGapHardLimitDu = RuntimeSettings.Load().SprinklerToBoundaryDistanceM * duPerMeter;
            double effectiveMaxGapDu = Math.Min(maxGapDu, maxGapHardLimitDu);

            // Sanity cap — a mismatched INSUNITS or an accidentally huge zone can produce
            // millions of grid cells that freeze the UI thread. Reject early with a message
            // the LLM can act on rather than hang in PlaceCenteredGridInRing.
            if (spacingDu > 0)
            {
                double cellsX = (maxX - minX) / spacingDu;
                double cellsY = (maxY - minY) / spacingDu;
                double expectedCells = cellsX * cellsY;
                const double MaxExpectedCells = 200_000.0;
                if (expectedCells > MaxExpectedCells || cellsX > 2000 || cellsY > 2000)
                {
                    errorMessage =
                        "Zone extent vs spacing would generate ~" +
                        expectedCells.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
                        " grid cells (cap " + MaxExpectedCells.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
                        "). Check INSUNITS — duPerMeter=" + duPerMeter.ToString("G4", System.Globalization.CultureInfo.InvariantCulture) +
                        ", extent=" + extent.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
                        " du, spacingDu=" + spacingDu.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                        ". Likely causes: wrong drawing units, or spacing_m too small for this zone size.";
                    AgentLog.Write("TryPlaceForZoneRing", "REJECT grid-cap: expectedCells=" + expectedCells.ToString("F0") + " cellsX=" + cellsX.ToString("F0") + " cellsY=" + cellsY.ToString("F0"));
                    return false;
                }
            }

            // Minimum inset from boundary polyline (then tightened by <see cref="ComputeStrictInteriorInsetDu"/>).
            double minPlacementClearanceDu = eps * 5;
            double mpc = 0.05 * duPerMeter; // 5 cm clearance in drawing units
            if (mpc > 0) minPlacementClearanceDu = Math.Max(minPlacementClearanceDu, mpc);

            double strictInteriorInsetDu = ComputeStrictInteriorInsetDu(minPlacementClearanceDu, extent, eps);
            strictInteriorInsetDu = Math.Max(strictInteriorInsetDu, GetSprinklerVisualInsetDu(db, eps));

            // Keep-out zones: avoid sprinklers on top of shaft blocks (and don't require coverage inside them).
            double keepoutPadDu = Math.Max(eps * 10.0, spacingDu * 0.05) / 1.5;
            var keepouts = CollectShaftKeepouts(db, zoneRing, keepoutPadDu);

            double offsetXDu2 = gridAnchorOffsetXMeters * duPerMeter;
            double offsetYDu2 = gridAnchorOffsetYMeters * duPerMeter;

            AgentLog.Write("TryPlaceForZoneRing", "duPerMeter=" + duPerMeter.ToString("G4") + " spacingDu=" + spacingDu.ToString("F0") + " radiusDu=" + radiusDu.ToString("F0") + " extent=" + extent.ToString("F0"));

            var best = PlaceCenteredGridInRing(zoneRing, spacingDu, eps, strictInteriorInsetDu, keepouts, 0, 0, 0, 0, offsetXDu2, offsetYDu2, alignGridToBoundary);
            AgentLog.Write("TryPlaceForZoneRing", "PlaceCenteredGrid done count=" + best.Count);

            // Hard sanity check — even after the upfront cap, a degenerate spacing_du (e.g., set to a fraction
            // of the zone by another code path) could still produce too many points. Refuse to continue to the
            // O(N²) stages rather than freeze the UI.
            if (best.Count > 50_000)
            {
                errorMessage =
                    "Grid produced " + best.Count + " heads (cap 50,000). " +
                    "Check INSUNITS and spacing_m. duPerMeter=" + duPerMeter.ToString("G4", System.Globalization.CultureInfo.InvariantCulture) +
                    ", spacingDu=" + spacingDu.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ".";
                AgentLog.Write("TryPlaceForZoneRing", "REJECT head-count cap: count=" + best.Count);
                return false;
            }

            bool bestOk = CoverageOk(zoneRing, best, radiusDu, eps, keepouts);
            AgentLog.Write("TryPlaceForZoneRing", "CoverageOk=" + bestOk);

            if (!bestOk)
            {
                var candidates = new List<(int l, int r, int b, int t)>
                {
                    (1,0,0,0),(0,1,0,0),(0,0,1,0),(0,0,0,1),
                    (1,1,0,0),(0,0,1,1),
                    (1,0,1,0),(0,1,0,1),
                    (1,1,1,0),(1,1,0,1),(1,0,1,1),(0,1,1,1),
                    (1,1,1,1),
                    (2,0,0,0),(0,2,0,0),(0,0,2,0),(0,0,0,2)
                };

                foreach (var c in candidates)
                {
                    var pts = PlaceCenteredGridInRing(zoneRing, spacingDu, eps, strictInteriorInsetDu, keepouts, c.l, c.r, c.b, c.t, offsetXDu2, offsetYDu2, alignGridToBoundary);
                    if (pts.Count == 0)
                        continue;
                    bool ok = CoverageOk(zoneRing, pts, radiusDu, eps, keepouts);
                    if (ok || pts.Count > best.Count)
                    {
                        best = pts;
                        bestOk = ok;
                        if (ok)
                            break;
                    }
                }
            }

            if (best.Count == 0)
            {
                if (!TryFindPointWithClearance(zoneRing, strictInteriorInsetDu, eps, keepouts, out var p))
                {
                    errorMessage =
                        "Zone is too narrow to place any sprinklers (minimum inset from boundary " +
                        "0.05 m could not be satisfied).";
                    return false;
                }

                best.Add(p);
                bestOk = CoverageOk(zoneRing, best, radiusDu, eps, keepouts);
            }

            var beforeBoundaryNudge2 = new List<Point2d>(best);
            best = NudgeBoundarySprinklersTowardGrid(zoneRing, best, spacingDu, strictInteriorInsetDu, eps, keepouts);
            int boundaryNudgedCount2 = CountMovedPoints(beforeBoundaryNudge2, best, Math.Max(eps * 8.0, spacingDu * 1e-6));

            best = FilterToStrictInterior(zoneRing, best, strictInteriorInsetDu, eps);
            best = EnforceGridLatticePhase(zoneRing, best, spacingDu, strictInteriorInsetDu, eps, keepouts);

            if (best.Count == 0)
            {
                errorMessage = "Sprinkler placement produced no valid points.";
                return false;
            }

            bestOk = CoverageOk(zoneRing, best, radiusDu, eps, keepouts);

            result = new PlacementResult
            {
                Sprinklers = best,
                AboveSprinklers = null,
                BelowSprinklers = null,
                UsedOffsetPolygon = false,
                CoverageOk = bestOk,
                Summary = "Placed grid. Boundary snap moved " +
                          boundaryNudgedCount2.ToString(System.Globalization.CultureInfo.InvariantCulture) + " heads inward."
            };
            return true;
        }

        /// <summary>Minimum perpendicular distance from boundary so heads stay clearly inside the zone (not on the dashed polyline).</summary>
        private static double ComputeStrictInteriorInsetDu(double minPlacementClearanceDu, double extent, double eps)
        {
            return Math.Max(
                minPlacementClearanceDu + eps * 6.0,
                Math.Max(1e-7 * Math.Max(extent, 1.0), eps * 12.0));
        }

        /// <summary>True if the point lies in the polygon interior and is not on/near the boundary segments.</summary>
        private static bool IsStrictlyInsideZone(IList<Point2d> ring, Point2d p, double strictInteriorInsetDu, double eps)
        {
            if (ring == null || ring.Count < 3)
                return false;
            if (!PointInPolygon(ring, p.X, p.Y))
                return false;
            double need = strictInteriorInsetDu + eps * 2.0;
            double d2 = MinDistSquaredToRingEdges(ring, p);
            return d2 > need * need;
        }

        private static List<Point2d> FilterToStrictInterior(List<Point2d> ring, List<Point2d> sprinklers, double strictInteriorInsetDu, double eps)
        {
            if (sprinklers == null || sprinklers.Count == 0)
                return sprinklers ?? new List<Point2d>();
            var list = new List<Point2d>(sprinklers.Count);
            for (int i = 0; i < sprinklers.Count; i++)
            {
                if (IsStrictlyInsideZone(ring, sprinklers[i], strictInteriorInsetDu, eps))
                    list.Add(sprinklers[i]);
            }
            return list;
        }

        private readonly struct AxisBand
        {
            public readonly bool Vertical; // true => x = constant (column), false => y = constant (row)
            public readonly double AxisValue;

            public AxisBand(bool vertical, double axisValue)
            {
                Vertical = vertical;
                AxisValue = axisValue;
            }
        }

        private static double SignedArea2(IList<Point2d> ring)
        {
            double a2 = 0.0;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                a2 += ring[i].X * ring[j].Y - ring[j].X * ring[i].Y;
            }
            return a2;
        }

        private static List<double> UniqueAxisCoords(List<Point2d> pts, bool useX, double tol)
        {
            var vals = new List<double>();
            if (pts == null) return vals;
            for (int i = 0; i < pts.Count; i++)
                vals.Add(useX ? pts[i].X : pts[i].Y);
            vals.Sort();
            var uniq = new List<double>();
            for (int i = 0; i < vals.Count; i++)
            {
                if (uniq.Count == 0 || Math.Abs(vals[i] - uniq[uniq.Count - 1]) > tol)
                    uniq.Add(vals[i]);
            }
            return uniq;
        }

        private static bool HasAxisCoordWithin(List<double> uniq, double v, double tol)
        {
            // linear is fine (grid sizes are modest)
            for (int i = 0; i < uniq.Count; i++)
            {
                if (Math.Abs(uniq[i] - v) <= tol)
                    return true;
            }
            return false;
        }

        private static double PositiveModulo(double value, double mod)
        {
            if (!(mod > 0)) return value;
            double r = value % mod;
            if (r < 0) r += mod;
            return r;
        }

        private static double CircularDistance(double a, double b, double period)
        {
            double d = Math.Abs(a - b);
            return Math.Min(d, Math.Abs(period - d));
        }

        private static double DominantPhase(List<Point2d> pts, double spacingDu, bool useX)
        {
            if (pts == null || pts.Count == 0 || !(spacingDu > 0))
                return 0.0;

            double bestPhase = PositiveModulo(useX ? pts[0].X : pts[0].Y, spacingDu);
            int bestCount = -1;
            double tol = Math.Max(spacingDu * 0.08, 1e-9 * spacingDu);

            for (int i = 0; i < pts.Count; i++)
            {
                double phaseI = PositiveModulo(useX ? pts[i].X : pts[i].Y, spacingDu);
                int count = 0;
                for (int j = 0; j < pts.Count; j++)
                {
                    double phaseJ = PositiveModulo(useX ? pts[j].X : pts[j].Y, spacingDu);
                    if (CircularDistance(phaseI, phaseJ, spacingDu) <= tol)
                        count++;
                }

                if (count > bestCount)
                {
                    bestCount = count;
                    bestPhase = phaseI;
                }
            }

            return bestPhase;
        }

        private static List<Point2d> EnforceGridLatticePhase(
            List<Point2d> ring,
            List<Point2d> sprinklers,
            double spacingDu,
            double strictInteriorInsetDu,
            double eps,
            List<Extents2d> keepouts)
        {
            if (sprinklers == null || sprinklers.Count == 0 || !(spacingDu > eps))
                return sprinklers ?? new List<Point2d>();

            double xPhase = DominantPhase(sprinklers, spacingDu, useX: true);
            double yPhase = DominantPhase(sprinklers, spacingDu, useX: false);
            double moveTol = spacingDu * 0.45;
            double dedupeTol = Math.Max(eps * 60.0, spacingDu * 0.08);

            var outPts = new List<Point2d>(sprinklers.Count);
            for (int i = 0; i < sprinklers.Count; i++)
            {
                var p = sprinklers[i];
                var snapped = new Point2d(
                    SnapToGridPhase(p.X, spacingDu, xPhase, eps),
                    SnapToGridPhase(p.Y, spacingDu, yPhase, eps));

                double dx = snapped.X - p.X;
                double dy = snapped.Y - p.Y;
                var cand = (dx * dx + dy * dy <= moveTol * moveTol) ? snapped : p;

                if (!IsStrictlyInsideZone(ring, cand, strictInteriorInsetDu, eps))
                    cand = p;
                if (keepouts != null && keepouts.Count > 0 && PointInsideAnyKeepout(cand, keepouts))
                    cand = p;

                bool dup = false;
                for (int k = 0; k < outPts.Count; k++)
                {
                    double ddx = outPts[k].X - cand.X;
                    double ddy = outPts[k].Y - cand.Y;
                    if (ddx * ddx + ddy * ddy <= dedupeTol * dedupeTol)
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup)
                    outPts.Add(cand);
            }

            return outPts;
        }

        private static double SnapToGridPhase(double value, double spacingDu, double phaseValue, double eps)
        {
            if (!(spacingDu > eps))
                return value;
            return phaseValue + Math.Round((value - phaseValue) / spacingDu) * spacingDu;
        }

        /// <summary>
        /// Minimum centre-to-centre distance for heads placed on wall-offset bands / gap-fix passes.
        /// Kept below the main grid spacing rule so a mandatory row at <paramref name="maxGapDu"/> from the wall is not
        /// blocked by a trunk-locked row slightly farther in (common with horizontal trunk under a top wall).
        /// </summary>
        private static double BoundaryBandMinSepDu(double spacingDu, double maxGapDu, double eps)
        {
            double fullGrid = spacingDu * 0.42;
            double wallBand = maxGapDu * 0.28;
            return Math.Max(eps * 400.0, Math.Min(fullGrid, wallBand));
        }

        /// <summary>
        /// Offset line parallel to an axis-aligned boundary segment, at distance <paramref name="offsetDu"/> into the polygon.
        /// </summary>
        private static bool TryAxisAlignedInwardOffsetLine(
            Point2d p0,
            Point2d p1,
            bool vertical,
            double offsetDu,
            IList<Point2d> ring,
            double axisEps,
            out double bandCoord)
        {
            bandCoord = 0;
            if (offsetDu <= 0 || ring == null || ring.Count < 3)
                return false;

            double probe = Math.Max(axisEps * 500, offsetDu * 0.02);
            if (!(probe > 0))
                probe = 1e-4;

            if (vertical)
            {
                double x0 = p0.X;
                double my = 0.5 * (p0.Y + p1.Y);
                bool leftIn = PointInPolygon(ring, x0 - probe, my);
                bool rightIn = PointInPolygon(ring, x0 + probe, my);
                if (leftIn == rightIn)
                    return false;
                bandCoord = leftIn ? x0 - offsetDu : x0 + offsetDu;
            }
            else
            {
                double y0 = p0.Y;
                double mx = 0.5 * (p0.X + p1.X);
                bool belowIn = PointInPolygon(ring, mx, y0 - probe);
                bool aboveIn = PointInPolygon(ring, mx, y0 + probe);
                if (belowIn == aboveIn)
                    return false;
                bandCoord = belowIn ? y0 - offsetDu : y0 + offsetDu;
            }

            return true;
        }

        /// <summary>
        /// If any axis-aligned wall segment still has perpendicular distance &gt; <paramref name="maxGapDu"/> to the nearest
        /// sprinkler (within that segment's span), adds a full row or column at <paramref name="maxGapDu"/> inside that wall.
        /// </summary>
        private static List<Point2d> EnsureMaxPerpendicularGapToAxisAlignedWalls(
            List<Point2d> zoneRing,
            List<Point2d> sprinklers,
            double maxGapDu,
            double spacingDu,
            double strictInteriorInsetDu,
            double eps,
            List<Extents2d> keepouts,
            double axisEps,
            double coordTol)
        {
            if (zoneRing == null || zoneRing.Count < 3 || sprinklers == null || sprinklers.Count == 0)
                return sprinklers ?? new List<Point2d>();

            double boundaryMinSepDu = BoundaryBandMinSepDu(spacingDu, maxGapDu, eps);
            double boundaryMinSepSq = boundaryMinSepDu * boundaryMinSepDu;

            var pts = new List<Point2d>(sprinklers);
            GetExtents(zoneRing, out double minX, out double minY, out double maxX, out double maxY);
            double extent = Math.Max(maxX - minX, maxY - minY);
            double gapTol = Math.Max(eps * 4, 1e-9 * Math.Max(extent, 1.0));

            int n = zoneRing.Count;
            for (int i = 0; i < n; i++)
            {
                var xGrid = UniqueAxisCoords(pts, useX: true, tol: coordTol);
                var yGrid = UniqueAxisCoords(pts, useX: false, tol: coordTol);
                if (xGrid.Count == 0 || yGrid.Count == 0)
                    return pts;

                var p0 = zoneRing[i];
                var p1 = zoneRing[(i + 1) % n];
                double dx = p1.X - p0.X;
                double dy = p1.Y - p0.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len <= axisEps)
                    continue;

                bool vertical = Math.Abs(dx) <= axisEps && Math.Abs(dy) > axisEps;
                bool horizontal = Math.Abs(dy) <= axisEps && Math.Abs(dx) > axisEps;
                if (!vertical && !horizontal)
                {
                    // Slanted wall: build an inward parallel band at maxGapDu, then project existing
                    // grid points onto that band and snap back to grid to preserve alignment.
                    if (len <= axisEps)
                        continue;

                    double ux = dx / len;
                    double uy = dy / len;
                    double nx = -uy;
                    double ny = ux;

                    double probe = Math.Max(eps * 100.0, axisEps * 10.0);
                    double testX = (p0.X + p1.X) * 0.5 + nx * probe;
                    double testY = (p0.Y + p1.Y) * 0.5 + ny * probe;
                    if (!PointInPolygon(zoneRing, testX, testY))
                    {
                        nx = -nx;
                        ny = -ny;
                    }

                    double nearestGap = double.PositiveInfinity;
                    for (int k = 0; k < pts.Count; k++)
                    {
                        var s = pts[k];
                        double tOnEdge = (s.X - p0.X) * ux + (s.Y - p0.Y) * uy;
                        if (tOnEdge < -coordTol || tOnEdge > len + coordTol)
                            continue;

                        double perp = (s.X - p0.X) * nx + (s.Y - p0.Y) * ny;
                        if (perp <= gapTol)
                            continue;

                        if (perp < nearestGap)
                            nearestGap = perp;
                    }

                    if (!double.IsPositiveInfinity(nearestGap) && !(nearestGap > maxGapDu + gapTol))
                        continue;

                    double bx0 = p0.X + nx * maxGapDu;
                    double by0 = p0.Y + ny * maxGapDu;

                    int sourceCount = pts.Count;
                    for (int k = 0; k < sourceCount; k++)
                    {
                        var g = pts[k];
                        double t = ((g.X - bx0) * ux + (g.Y - by0) * uy);
                        if (t < -coordTol || t > len + coordTol)
                            continue;

                        double perp = (g.X - p0.X) * nx + (g.Y - p0.Y) * ny;
                        if (Math.Abs(perp - maxGapDu) > spacingDu * 0.5)
                            continue;

                        var cand = g;
                        double tCand = ((cand.X - p0.X) * ux + (cand.Y - p0.Y) * uy);
                        if (tCand < -coordTol || tCand > len + coordTol)
                            continue;
                        if (!IsStrictlyInsideZone(zoneRing, cand, strictInteriorInsetDu, eps))
                            continue;
                        if (keepouts != null && keepouts.Count > 0 && PointInsideAnyKeepout(cand, keepouts))
                            continue;

                        bool ok = true;
                        for (int m = 0; m < pts.Count; m++)
                        {
                            double dx2 = pts[m].X - cand.X;
                            double dy2 = pts[m].Y - cand.Y;
                            if (dx2 * dx2 + dy2 * dy2 < boundaryMinSepSq)
                            {
                                ok = false;
                                break;
                            }
                        }

                        if (ok)
                            pts.Add(cand);
                    }

                    continue;
                }

                if (!TryAxisAlignedInwardOffsetLine(p0, p1, vertical, maxGapDu, zoneRing, axisEps, out double bandLine))
                    continue;

                double xe0 = Math.Min(p0.X, p1.X);
                double xe1 = Math.Max(p0.X, p1.X);
                double ye0 = Math.Min(p0.Y, p1.Y);
                double ye1 = Math.Max(p0.Y, p1.Y);

                if (horizontal)
                {
                    double yWall = p0.Y;
                    double probe = Math.Max(axisEps * 500, maxGapDu * 0.02);
                    double mx = 0.5 * (xe0 + xe1);
                    bool belowIn = PointInPolygon(zoneRing, mx, yWall - probe);
                    bool aboveIn = PointInPolygon(zoneRing, mx, yWall + probe);

                    double worstGap = 0;
                    if (belowIn && !aboveIn)
                    {
                        double bestY = double.NegativeInfinity;
                        for (int k = 0; k < pts.Count; k++)
                        {
                            var s = pts[k];
                            if (s.X < xe0 - coordTol || s.X > xe1 + coordTol)
                                continue;
                            if (s.Y >= yWall - gapTol)
                                continue;
                            if (!PointInPolygon(zoneRing, s.X, s.Y))
                                continue;
                            if (s.Y > bestY)
                                bestY = s.Y;
                        }

                        worstGap = double.IsNegativeInfinity(bestY) ? double.PositiveInfinity : yWall - bestY;
                    }
                    else if (aboveIn && !belowIn)
                    {
                        double bestY = double.PositiveInfinity;
                        for (int k = 0; k < pts.Count; k++)
                        {
                            var s = pts[k];
                            if (s.X < xe0 - coordTol || s.X > xe1 + coordTol)
                                continue;
                            if (s.Y <= yWall + gapTol)
                                continue;
                            if (!PointInPolygon(zoneRing, s.X, s.Y))
                                continue;
                            if (s.Y < bestY)
                                bestY = s.Y;
                        }

                        worstGap = double.IsPositiveInfinity(bestY) ? double.PositiveInfinity : bestY - yWall;
                    }
                    else
                        continue;

                    if (!(worstGap > maxGapDu + gapTol))
                        continue;

                    double yBand = bandLine;
                    if (spacingDu > eps && yGrid.Count > 0)
                        yBand = SnapToGridPhase(yBand, spacingDu, yGrid[0], eps);

                    void TryAddBandPointHorizontal(Point2d p)
                    {
                        if (p.X < xe0 - coordTol || p.X > xe1 + coordTol)
                            return;
                        if (!IsStrictlyInsideZone(zoneRing, p, strictInteriorInsetDu, eps))
                            return;
                        if (keepouts != null && keepouts.Count > 0 && PointInsideAnyKeepout(p, keepouts))
                            return;

                        for (int k = 0; k < pts.Count; k++)
                        {
                            double ddx = pts[k].X - p.X;
                            double ddy = pts[k].Y - p.Y;
                            if (ddx * ddx + ddy * ddy < boundaryMinSepSq)
                                return;
                        }

                        pts.Add(p);
                    }

                    for (int ix = 0; ix < xGrid.Count; ix++)
                        TryAddBandPointHorizontal(new Point2d(xGrid[ix], yBand));

                    double xSampleStep = Math.Max(axisEps * 2500.0, Math.Min(spacingDu * 0.5, maxGapDu * 0.65));
                    if (xSampleStep > 1e-12)
                    {
                        double xPhase = xGrid[0];
                        double yPhase = yGrid[0];
                        for (double vx = xe0; vx <= xe1 + gapTol; vx += xSampleStep)
                        {
                            double x = vx;
                            double y = yBand;
                            if (spacingDu > eps)
                            {
                                x = SnapToGridPhase(x, spacingDu, xPhase, eps);
                                y = SnapToGridPhase(y, spacingDu, yPhase, eps);
                            }
                            TryAddBandPointHorizontal(new Point2d(x, y));
                        }
                    }

                    if (!HasAxisCoordWithin(yGrid, yBand, coordTol))
                    {
                        yGrid.Add(yBand);
                        yGrid.Sort();
                    }
                }
                else
                {
                    double xWall = p0.X;
                    double vprobe = Math.Max(axisEps * 500, maxGapDu * 0.02);
                    double myv = 0.5 * (ye0 + ye1);
                    bool rightIn = PointInPolygon(zoneRing, xWall + vprobe, myv);
                    bool leftIn = PointInPolygon(zoneRing, xWall - vprobe, myv);

                    double worstGap = 0;
                    if (rightIn && !leftIn)
                    {
                        double bestX = double.NegativeInfinity;
                        for (int k = 0; k < pts.Count; k++)
                        {
                            var s = pts[k];
                            if (s.Y < ye0 - coordTol || s.Y > ye1 + coordTol)
                                continue;
                            if (s.X <= xWall + gapTol)
                                continue;
                            if (!PointInPolygon(zoneRing, s.X, s.Y))
                                continue;
                            if (s.X > bestX)
                                bestX = s.X;
                        }

                        worstGap = double.IsNegativeInfinity(bestX) ? double.PositiveInfinity : bestX - xWall;
                    }
                    else if (leftIn && !rightIn)
                    {
                        double bestX = double.PositiveInfinity;
                        for (int k = 0; k < pts.Count; k++)
                        {
                            var s = pts[k];
                            if (s.Y < ye0 - coordTol || s.Y > ye1 + coordTol)
                                continue;
                            if (s.X >= xWall - gapTol)
                                continue;
                            if (!PointInPolygon(zoneRing, s.X, s.Y))
                                continue;
                            if (s.X < bestX)
                                bestX = s.X;
                        }

                        worstGap = double.IsPositiveInfinity(bestX) ? double.PositiveInfinity : xWall - bestX;
                    }
                    else
                        continue;

                    if (!(worstGap > maxGapDu + gapTol))
                        continue;

                    double xBand = bandLine;
                    if (spacingDu > eps && xGrid.Count > 0)
                        xBand = SnapToGridPhase(xBand, spacingDu, xGrid[0], eps);

                    void TryAddBandPointVertical(Point2d p)
                    {
                        if (p.Y < ye0 - coordTol || p.Y > ye1 + coordTol)
                            return;
                        if (!IsStrictlyInsideZone(zoneRing, p, strictInteriorInsetDu, eps))
                            return;
                        if (keepouts != null && keepouts.Count > 0 && PointInsideAnyKeepout(p, keepouts))
                            return;

                        for (int k = 0; k < pts.Count; k++)
                        {
                            double ddx = pts[k].X - p.X;
                            double ddy = pts[k].Y - p.Y;
                            if (ddx * ddx + ddy * ddy < boundaryMinSepSq)
                                return;
                        }

                        pts.Add(p);
                    }

                    for (int iy = 0; iy < yGrid.Count; iy++)
                        TryAddBandPointVertical(new Point2d(xBand, yGrid[iy]));

                    double ySampleStep = Math.Max(axisEps * 2500.0, Math.Min(spacingDu * 0.5, maxGapDu * 0.65));
                    if (ySampleStep > 1e-12)
                    {
                        double xPhase = xGrid[0];
                        double yPhase = yGrid[0];
                        for (double vy = ye0; vy <= ye1 + gapTol; vy += ySampleStep)
                        {
                            double x = xBand;
                            double y = vy;
                            if (spacingDu > eps)
                            {
                                x = SnapToGridPhase(x, spacingDu, xPhase, eps);
                                y = SnapToGridPhase(y, spacingDu, yPhase, eps);
                            }
                            TryAddBandPointVertical(new Point2d(x, y));
                        }
                    }

                    if (!HasAxisCoordWithin(xGrid, xBand, coordTol))
                    {
                        xGrid.Add(xBand);
                        xGrid.Sort();
                    }
                }
            }

            return pts;
        }

        /// <summary>
        /// Adds a full row/column of heads at <paramref name="wallOffsetDu"/> inside each axis-aligned wall, aligned to the existing grid rows/cols.
        /// This produces the clean \"second column\" at 1.5m from walls (coverage radius) without sprinklers sitting on the boundary.
        /// </summary>
        private static List<Point2d> AddWallOffsetBands(
            List<Point2d> zoneRing,
            List<Point2d> sprinklers,
            double wallOffsetDu,
            double spacingDu,
            double strictInteriorInsetDu,
            double eps,
            List<Extents2d> keepouts)
        {
            if (zoneRing == null || zoneRing.Count < 3 || sprinklers == null || sprinklers.Count == 0)
                return sprinklers ?? new List<Point2d>();

            var pts = new List<Point2d>(sprinklers);

            GetExtents(zoneRing, out double minX, out double minY, out double maxX, out double maxY);
            double extent = Math.Max(maxX - minX, maxY - minY);
            double axisEps = Math.Max(eps * 25.0, 1e-8 * Math.Max(extent, 1.0));

            // Existing grid coordinates (for alignment).
            double coordTol = Math.Max(eps * 20.0, spacingDu * 0.03);
            var xGrid = UniqueAxisCoords(pts, useX: true, tol: coordTol);
            var yGrid = UniqueAxisCoords(pts, useX: false, tol: coordTol);
            if (xGrid.Count == 0 || yGrid.Count == 0)
                return pts;

            // Collect unique bands from axis-aligned edges. Inward offset uses a midpoint test so top vs bottom
            // (and left vs right) walls get the correct perpendicular direction — the old CCW+sign heuristic
            // could place bands outside the zone, leaving large gaps (e.g. > 1.5 m) to the boundary.
            var bands = new List<AxisBand>();
            int n = zoneRing.Count;
            for (int i = 0; i < n; i++)
            {
                var p0 = zoneRing[i];
                var p1 = zoneRing[(i + 1) % n];
                double dx = p1.X - p0.X;
                double dy = p1.Y - p0.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len <= axisEps)
                    continue;

                bool vertical = Math.Abs(dx) <= axisEps && Math.Abs(dy) > axisEps;
                bool horizontal = Math.Abs(dy) <= axisEps && Math.Abs(dx) > axisEps;
                if (!vertical && !horizontal)
                    continue;

                if (vertical)
                {
                    if (TryAxisAlignedInwardOffsetLine(p0, p1, vertical: true, wallOffsetDu, zoneRing, axisEps, out double xBand))
                        bands.Add(new AxisBand(vertical: true, axisValue: xBand));
                }
                else
                {
                    if (TryAxisAlignedInwardOffsetLine(p0, p1, vertical: false, wallOffsetDu, zoneRing, axisEps, out double yBand))
                        bands.Add(new AxisBand(vertical: false, axisValue: yBand));
                }
            }

            // De-dup bands that are essentially the same.
            bands.Sort((a, b) => a.AxisValue.CompareTo(b.AxisValue));
            var uniqBands = new List<AxisBand>();
            for (int i = 0; i < bands.Count; i++)
            {
                if (uniqBands.Count == 0)
                {
                    uniqBands.Add(bands[i]);
                    continue;
                }
                var last = uniqBands[uniqBands.Count - 1];
                if (bands[i].Vertical == last.Vertical && Math.Abs(bands[i].AxisValue - last.AxisValue) <= coordTol)
                    continue;
                uniqBands.Add(bands[i]);
            }

            // IMPORTANT: Don't add wall bands unconditionally. Only add points when a wall segment would otherwise
            // exceed the allowed max gap (handled by EnsureMaxPerpendicularGapToAxisAlignedWalls below). This prevents
            // creating an extra row when the nearest interior row already sits at exactly wallOffsetDu (e.g. 1.5 m).

            const int maxGapEnsurePasses = 14;
            for (int pass = 0; pass < maxGapEnsurePasses; pass++)
            {
                int before = pts.Count;
                pts = EnsureMaxPerpendicularGapToAxisAlignedWalls(
                    zoneRing,
                    pts,
                    wallOffsetDu,
                    spacingDu,
                    strictInteriorInsetDu,
                    eps,
                    keepouts,
                    axisEps,
                    coordTol);
                if (pts.Count == before)
                    break;
            }

            return pts;
        }

        private static List<Point2d> PlaceTrunkAnchoredGridInRing(
            List<Point2d> ring,
            double spacingDu,
            double eps,
            double strictInteriorInsetDu,
            List<Extents2d> keepouts,
            bool trunkHorizontal,
            double trunkAxis,
            double offsetFromTrunkDu,
            int extraAlongTrunkNeg,
            int extraAlongTrunkPos,
            double gridAnchorOffsetXDu = 0,
            double gridAnchorOffsetYDu = 0)
        {
            GetExtents(ring, out double minX, out double minY, out double maxX, out double maxY);

            // Along-trunk axis is centered like the original grid; perpendicular is locked to trunkAxis ± offset + k·spacing.
            double offsetDu = spacingDu * 0.5;
            double width = (maxX - minX) - 2.0 * offsetDu;
            double height = (maxY - minY) - 2.0 * offsetDu;
            if (width <= eps || height <= eps)
                return new List<Point2d>();

            int nx = (int)Math.Floor(width / spacingDu) + 1;
            int ny = (int)Math.Floor(height / spacingDu) + 1;
            if (nx < 1) nx = 1;
            if (ny < 1) ny = 1;

            double xSpan = (nx - 1) * spacingDu;
            double ySpan = (ny - 1) * spacingDu;
            double x0 = minX + offsetDu + (width - xSpan) * 0.5 + gridAnchorOffsetXDu;
            double y0 = minY + offsetDu + (height - ySpan) * 0.5 + gridAnchorOffsetYDu;

            int nxUse = nx;
            int nyUse = ny;
            double xStart = x0;
            double yStart = y0;

            if (trunkHorizontal)
            {
                nxUse = nx + extraAlongTrunkNeg + extraAlongTrunkPos;
                xStart = x0 - extraAlongTrunkNeg * spacingDu;
            }
            else
            {
                nyUse = ny + extraAlongTrunkNeg + extraAlongTrunkPos;
                yStart = y0 - extraAlongTrunkNeg * spacingDu;
            }

            // Perpendicular anchored sequence.
            double anchor = trunkAxis + offsetFromTrunkDu;

            int kMin, kMax;
            if (trunkHorizontal)
            {
                kMin = (int)Math.Floor((minY - anchor) / spacingDu) - 2;
                kMax = (int)Math.Ceiling((maxY - anchor) / spacingDu) + 2;
            }
            else
            {
                kMin = (int)Math.Floor((minX - anchor) / spacingDu) - 2;
                kMax = (int)Math.Ceiling((maxX - anchor) / spacingDu) + 2;
            }

            var pts = new List<Point2d>(Math.Max(16, nxUse * nyUse));

            if (trunkHorizontal)
            {
                for (int iy = kMin; iy <= kMax; iy++)
                {
                    double y = anchor + iy * spacingDu;
                    for (int ix = 0; ix < nxUse; ix++)
                    {
                        double x = xStart + ix * spacingDu;
                        var p = new Point2d(x, y);
                        if (!IsStrictlyInsideZone(ring, p, strictInteriorInsetDu, eps))
                            continue;
                        if (keepouts != null && keepouts.Count > 0 && PointInsideAnyKeepout(p, keepouts))
                            continue;
                        pts.Add(p);
                    }
                }
            }
            else
            {
                for (int ix = kMin; ix <= kMax; ix++)
                {
                    double x = anchor + ix * spacingDu;
                    for (int iy = 0; iy < nyUse; iy++)
                    {
                        double y = yStart + iy * spacingDu;
                        var p = new Point2d(x, y);
                        if (!IsStrictlyInsideZone(ring, p, strictInteriorInsetDu, eps))
                            continue;
                        if (keepouts != null && keepouts.Count > 0 && PointInsideAnyKeepout(p, keepouts))
                            continue;
                        pts.Add(p);
                    }
                }
            }

            return pts;
        }

        private static List<Point2d> PlaceCenteredGridInRing(
            List<Point2d> ring,
            double spacingDu,
            double eps,
            double strictInteriorInsetDu,
            List<Extents2d> keepouts,
            int extraColsLeft,
            int extraColsRight,
            int extraRowsBottom,
            int extraRowsTop,
            double gridAnchorOffsetXDu = 0,
            double gridAnchorOffsetYDu = 0,
            bool alignGridToBoundary = false)
        {
            GetExtents(ring, out double minX, out double minY, out double maxX, out double maxY);
            double offsetDu = alignGridToBoundary ? 0.0 : spacingDu * 0.5;

            double width = (maxX - minX) - 2.0 * offsetDu;
            double height = (maxY - minY) - 2.0 * offsetDu;
            if (width <= eps || height <= eps)
                return new List<Point2d>();

            int nx = (int)Math.Floor(width / spacingDu) + 1;
            int ny = (int)Math.Floor(height / spacingDu) + 1;
            if (nx < 1) nx = 1;
            if (ny < 1) ny = 1;

            double xSpan = (nx - 1) * spacingDu;
            double ySpan = (ny - 1) * spacingDu;

            double x0 = alignGridToBoundary
                ? minX + offsetDu + gridAnchorOffsetXDu
                : minX + offsetDu + (width - xSpan) * 0.5 + gridAnchorOffsetXDu;
            double y0 = alignGridToBoundary
                ? minY + offsetDu + gridAnchorOffsetYDu
                : minY + offsetDu + (height - ySpan) * 0.5 + gridAnchorOffsetYDu;

            int nxUse = nx + extraColsLeft + extraColsRight;
            int nyUse = ny + extraRowsBottom + extraRowsTop;

            // Defense-in-depth against runaway grids even if upstream caps are bypassed.
            if ((long)nxUse * nyUse > 500_000 || nxUse > 2000 || nyUse > 2000)
            {
                AgentLog.Write("PlaceCenteredGridInRing", "REJECT oversize grid nxUse=" + nxUse + " nyUse=" + nyUse);
                return new List<Point2d>();
            }

            double xStart = x0 - extraColsLeft * spacingDu;
            double yStart = y0 - extraRowsBottom * spacingDu;

            var pts = new List<Point2d>(Math.Max(16, nxUse * nyUse));
            for (int iy = 0; iy < nyUse; iy++)
            {
                double y = yStart + iy * spacingDu;
                for (int ix = 0; ix < nxUse; ix++)
                {
                    double x = xStart + ix * spacingDu;
                    var p = new Point2d(x, y);
                    if (!IsStrictlyInsideZone(ring, p, strictInteriorInsetDu, eps))
                        continue;

                    if (keepouts != null && keepouts.Count > 0 && PointInsideAnyKeepout(p, keepouts))
                        continue;

                    pts.Add(p);
                }
            }

            return pts;
        }

        private static bool CoverageOk(List<Point2d> ring, List<Point2d> sprinklers, double radiusDu, double eps, List<Extents2d> keepouts)
        {
            if (sprinklers == null || sprinklers.Count == 0)
                return false;

            GetExtents(ring, out double minX, out double minY, out double maxX, out double maxY);
            double step = Math.Max(radiusDu * 0.5, eps * 10);
            if (step <= eps)
                step = radiusDu;

            // Safety cap: if the grid × sprinkler product is too large, skip and assume coverage OK.
            // This prevents a hang when INSUNITS mismatch slips past the placement guard.
            long xSteps = (long)Math.Ceiling((maxX - minX) / step) + 1;
            long ySteps = (long)Math.Ceiling((maxY - minY) / step) + 1;
            if (xSteps * ySteps * sprinklers.Count > 50_000_000L)
                return true;

            for (double y = minY; y <= maxY + eps; y += step)
            {
                for (double x = minX; x <= maxX + eps; x += step)
                {
                    if (!PointInPolygon(ring, x, y))
                        continue;
                    if (keepouts != null && keepouts.Count > 0 && PointInsideAnyKeepout(new Point2d(x, y), keepouts))
                        continue;
                    if (MinDistToSprinklers(sprinklers, x, y) > radiusDu + eps)
                        return false;
                }
            }

            return true;
        }

        private static double MinDistToSprinklers(List<Point2d> sprinklers, double x, double y)
        {
            double best2 = double.MaxValue;
            for (int i = 0; i < sprinklers.Count; i++)
            {
                double dx = sprinklers[i].X - x;
                double dy = sprinklers[i].Y - y;
                double d2 = dx * dx + dy * dy;
                if (d2 < best2) best2 = d2;
            }
            return Math.Sqrt(best2);
        }

        private static Point2d CentroidApprox(List<Point2d> ring)
        {
            // Polygon centroid (shoelace). Fallback to average if degenerate.
            double a = 0;
            double cx = 0;
            double cy = 0;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                double cross = ring[i].X * ring[j].Y - ring[j].X * ring[i].Y;
                a += cross;
                cx += (ring[i].X + ring[j].X) * cross;
                cy += (ring[i].Y + ring[j].Y) * cross;
            }

            if (Math.Abs(a) <= 1e-18)
            {
                double sx = 0, sy = 0;
                for (int i = 0; i < n; i++) { sx += ring[i].X; sy += ring[i].Y; }
                double inv = n > 0 ? 1.0 / n : 0;
                return new Point2d(sx * inv, sy * inv);
            }

            a *= 0.5;
            double invA = 1.0 / (6.0 * a);
            return new Point2d(cx * invA, cy * invA);
        }

        private static void GetExtents(List<Point2d> ring, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = double.MaxValue; minY = double.MaxValue;
            maxX = double.MinValue; maxY = double.MinValue;
            foreach (var p in ring)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
        }

        private static bool PointInPolygon(IList<Point2d> poly, double x, double y)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = poly[i].X, yi = poly[i].Y;
                double xj = poly[j].X, yj = poly[j].Y;
                if (((yi > y) != (yj > y)) && (x < (xj - xi) * (y - yi) / (yj - yi + 1e-30) + xi))
                    inside = !inside;
            }
            return inside;
        }

        private static bool TryFindPointWithClearance(
            List<Point2d> ring,
            double strictInteriorInsetDu,
            double eps,
            List<Extents2d> keepouts,
            out Point2d p)
        {
            p = default;
            if (ring == null || ring.Count < 3)
                return false;

            var c = CentroidApprox(ring);
            if (IsStrictlyInsideZone(ring, c, strictInteriorInsetDu, eps) &&
                !(keepouts != null && keepouts.Count > 0 && PointInsideAnyKeepout(c, keepouts)))
            {
                p = c;
                return true;
            }

            GetExtents(ring, out double minX, out double minY, out double maxX, out double maxY);
            double step = Math.Max(strictInteriorInsetDu * 0.25, eps * 20);
            if (!(step > 0)) step = 1.0;

            bool foundAny = false;
            double bestD2 = double.NegativeInfinity;
            Point2d bestP = default;

            for (double y = minY; y <= maxY + eps; y += step)
            {
                for (double x = minX; x <= maxX + eps; x += step)
                {
                    var q = new Point2d(x, y);
                    if (!IsStrictlyInsideZone(ring, q, strictInteriorInsetDu, eps))
                        continue;
                    if (keepouts != null && keepouts.Count > 0 && PointInsideAnyKeepout(q, keepouts))
                        continue;
                    foundAny = true;
                    double d2 = MinDistSquaredToRingEdges(ring, q);
                    if (d2 > bestD2)
                    {
                        bestD2 = d2;
                        bestP = q;
                    }
                }
            }

            if (!foundAny)
                return false;

            p = bestP;
            return true;
        }

        private static double MinDistSquaredToRingEdges(IList<Point2d> ring, Point2d p)
        {
            double best = double.PositiveInfinity;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                double d2 = DistSquaredPointToSegment(p, a, b);
                if (d2 < best) best = d2;
            }
            return best;
        }

        private static double DistSquaredPointToSegment(Point2d p, Point2d a, Point2d b)
        {
            double vx = b.X - a.X;
            double vy = b.Y - a.Y;
            double wx = p.X - a.X;
            double wy = p.Y - a.Y;

            double c1 = vx * wx + vy * wy;
            if (c1 <= 0) return (p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y);

            double c2 = vx * vx + vy * vy;
            if (c2 <= 0) return (p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y);

            double t = c1 / c2;
            if (t >= 1) return (p.X - b.X) * (p.X - b.X) + (p.Y - b.Y) * (p.Y - b.Y);

            double px = a.X + t * vx;
            double py = a.Y + t * vy;
            double dx = p.X - px;
            double dy = p.Y - py;
            return dx * dx + dy * dy;
        }

        /// <summary>
        /// For each sprinkler whose nearest-boundary distance is greater than <paramref name="maxGapDu"/>, adds one extra sprinkler
        /// on the same WCS X (vertical axis), shifted toward the nearest boundary intersection on that X so it sits ~<paramref name="maxGapDu"/>
        /// from that boundary (toward the boundary, but remaining inside the polygon).
        /// </summary>
        private static List<Point2d> AddLocalNearWallCompanions(
            List<Point2d> ring,
            List<Point2d> sprinklers,
            double maxGapDu,
            double spacingDu,
            double strictInteriorInsetDu,
            double eps,
            List<Extents2d> keepouts)
        {
            if (ring == null || ring.Count < 3 || sprinklers == null || sprinklers.Count == 0)
                return sprinklers ?? new List<Point2d>();

            double gapTol = Math.Max(eps * 10.0, maxGapDu * 0.02);
            double minSepDu = Math.Max(eps * 500.0, Math.Min(spacingDu * 0.35, maxGapDu * 0.45));
            double minSepSq = minSepDu * minSepDu;

            var pts = new List<Point2d>(sprinklers);
            int count = sprinklers.Count;
            for (int i = 0; i < count; i++)
            {
                var p = sprinklers[i];
                if (!TryClosestBoundaryPointOnVerticalLine(ring, p.X, p.Y, eps, out var q, out double d))
                {
                    // Fallback: generic closest point if no vertical intersection found (rare, but possible).
                    if (!TryClosestPointOnRingEdges(ring, p, out q, out d))
                        continue;
                }

                if (!(d > maxGapDu + gapTol))
                    continue;

                // Move along WCS Y only, toward the boundary point on the same X.
                double sgn = (p.Y >= q.Y) ? 1.0 : -1.0;
                double y = q.Y + sgn * maxGapDu;
                if (spacingDu > eps)
                    y = SnapToGridPhase(y, spacingDu, p.Y, eps);
                var cand = new Point2d(p.X, y);
                if (!IsStrictlyInsideZone(ring, cand, strictInteriorInsetDu, eps))
                    continue;
                if (keepouts != null && keepouts.Count > 0 && PointInsideAnyKeepout(cand, keepouts))
                    continue;

                bool farEnough = true;
                for (int k = 0; k < pts.Count; k++)
                {
                    double dx2 = pts[k].X - cand.X;
                    double dy2 = pts[k].Y - cand.Y;
                    if (dx2 * dx2 + dy2 * dy2 < minSepSq)
                    {
                        farEnough = false;
                        break;
                    }
                }
                if (!farEnough)
                    continue;

                pts.Add(cand);
            }

            return pts;
        }

        /// <summary>
        /// For each sprinkler whose nearest shaft keepout boundary is farther than <paramref name="maxGapDu"/>,
        /// add a grid-aligned companion nudged toward the keepout edge so shaft-to-head gap is bounded.
        /// </summary>
        private static List<Point2d> AddLocalNearKeepoutCompanions(
            List<Point2d> ring,
            List<Point2d> sprinklers,
            double maxGapDu,
            double spacingDu,
            double strictInteriorInsetDu,
            double eps,
            List<Extents2d> keepouts)
        {
            if (ring == null || ring.Count < 3 || sprinklers == null || sprinklers.Count == 0 || keepouts == null || keepouts.Count == 0)
                return sprinklers ?? new List<Point2d>();

            double gapTol = Math.Max(eps * 10.0, maxGapDu * 0.02);
            double minSepDu = Math.Max(eps * 500.0, Math.Min(spacingDu * 0.35, maxGapDu * 0.45));
            double minSepSq = minSepDu * minSepDu;

            var pts = new List<Point2d>(sprinklers);
            int count = sprinklers.Count;
            for (int i = 0; i < count; i++)
            {
                var p = sprinklers[i];
                if (!TryClosestPointOnKeepoutEdges(keepouts, p, out var q, out double d))
                    continue;

                if (!(d > maxGapDu + gapTol))
                    continue;

                Point2d best = default;
                double bestD = double.PositiveInfinity;
                bool found = false;

                double sx = (p.X >= q.X) ? 1.0 : -1.0;
                double x = q.X + sx * maxGapDu;
                if (spacingDu > eps)
                    x = SnapToGridPhase(x, spacingDu, p.X, eps);
                var candX = new Point2d(x, p.Y);
                if (IsStrictlyInsideZone(ring, candX, strictInteriorInsetDu, eps) && !PointInsideAnyKeepout(candX, keepouts))
                {
                    double dKeepX = MinDistToKeepouts(keepouts, candX);
                    if (dKeepX <= maxGapDu + gapTol)
                    {
                        bool farEnoughX = true;
                        for (int k = 0; k < pts.Count; k++)
                        {
                            double dx2 = pts[k].X - candX.X;
                            double dy2 = pts[k].Y - candX.Y;
                            if (dx2 * dx2 + dy2 * dy2 < minSepSq)
                            {
                                farEnoughX = false;
                                break;
                            }
                        }
                        if (farEnoughX)
                        {
                            best = candX;
                            bestD = dKeepX;
                            found = true;
                        }
                    }
                }

                double sy = (p.Y >= q.Y) ? 1.0 : -1.0;
                double y = q.Y + sy * maxGapDu;
                if (spacingDu > eps)
                    y = SnapToGridPhase(y, spacingDu, p.Y, eps);
                var candY = new Point2d(p.X, y);
                if (IsStrictlyInsideZone(ring, candY, strictInteriorInsetDu, eps) && !PointInsideAnyKeepout(candY, keepouts))
                {
                    double dKeepY = MinDistToKeepouts(keepouts, candY);
                    if (dKeepY <= maxGapDu + gapTol)
                    {
                        bool farEnoughY = true;
                        for (int k = 0; k < pts.Count; k++)
                        {
                            double dx2 = pts[k].X - candY.X;
                            double dy2 = pts[k].Y - candY.Y;
                            if (dx2 * dx2 + dy2 * dy2 < minSepSq)
                            {
                                farEnoughY = false;
                                break;
                            }
                        }
                        if (farEnoughY && dKeepY < bestD)
                        {
                            best = candY;
                            bestD = dKeepY;
                            found = true;
                        }
                    }
                }

                if (found)
                    pts.Add(best);
            }

            return pts;
        }

        private static bool TryClosestPointOnKeepoutEdges(List<Extents2d> keepouts, Point2d p, out Point2d closest, out double dist)
        {
            closest = default;
            dist = double.PositiveInfinity;
            if (keepouts == null || keepouts.Count == 0)
                return false;

            bool any = false;
            for (int i = 0; i < keepouts.Count; i++)
            {
                var e = keepouts[i];
                var q = ClosestPointOnExtentsBoundary(e, p);
                double dx = p.X - q.X;
                double dy = p.Y - q.Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < dist)
                {
                    dist = d;
                    closest = q;
                    any = true;
                }
            }

            return any && dist < double.PositiveInfinity;
        }

        private static double MinDistToKeepouts(List<Extents2d> keepouts, Point2d p)
        {
            if (keepouts == null || keepouts.Count == 0)
                return double.PositiveInfinity;

            double best = double.PositiveInfinity;
            for (int i = 0; i < keepouts.Count; i++)
            {
                var q = ClosestPointOnExtentsBoundary(keepouts[i], p);
                double dx = p.X - q.X;
                double dy = p.Y - q.Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < best)
                    best = d;
            }

            return best;
        }

        private static Point2d ClosestPointOnExtentsBoundary(Extents2d e, Point2d p)
        {
            double minX = e.MinPoint.X;
            double minY = e.MinPoint.Y;
            double maxX = e.MaxPoint.X;
            double maxY = e.MaxPoint.Y;

            double cx = Math.Max(minX, Math.Min(maxX, p.X));
            double cy = Math.Max(minY, Math.Min(maxY, p.Y));

            bool inside = (p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY);
            if (!inside)
            {
                if (Math.Abs(cx - minX) < 1e-12 || Math.Abs(cx - maxX) < 1e-12 ||
                    Math.Abs(cy - minY) < 1e-12 || Math.Abs(cy - maxY) < 1e-12)
                    return new Point2d(cx, cy);

                double dxMin = Math.Abs(p.X - minX);
                double dxMax = Math.Abs(p.X - maxX);
                double dyMin = Math.Abs(p.Y - minY);
                double dyMax = Math.Abs(p.Y - maxY);

                double best = dxMin;
                var q = new Point2d(minX, cy);
                if (dxMax < best) { best = dxMax; q = new Point2d(maxX, cy); }
                if (dyMin < best) { best = dyMin; q = new Point2d(cx, minY); }
                if (dyMax < best) { q = new Point2d(cx, maxY); }
                return q;
            }

            double dLeft = p.X - minX;
            double dRight = maxX - p.X;
            double dBottom = p.Y - minY;
            double dTop = maxY - p.Y;

            double d = dLeft;
            var outP = new Point2d(minX, p.Y);
            if (dRight < d) { d = dRight; outP = new Point2d(maxX, p.Y); }
            if (dBottom < d) { d = dBottom; outP = new Point2d(p.X, minY); }
            if (dTop < d) { outP = new Point2d(p.X, maxY); }
            return outP;
        }

        /// <summary>
        /// Nudges only boundary-touching sprinklers a small amount toward the interior grid,
        /// preserving their row/column axis (X or Y) so pattern remains aligned.
        /// </summary>
        private static List<Point2d> NudgeBoundarySprinklersTowardGrid(
            List<Point2d> ring,
            List<Point2d> sprinklers,
            double spacingDu,
            double strictInteriorInsetDu,
            double eps,
            List<Extents2d> keepouts)
        {
            if (ring == null || ring.Count < 3 || sprinklers == null || sprinklers.Count == 0)
                return sprinklers ?? new List<Point2d>();

            var pts = new List<Point2d>(sprinklers);
            double alignTol = Math.Max(eps * 60.0, spacingDu * 0.08);
            double targetClearance = Math.Max(strictInteriorInsetDu, Math.Max(eps * 40.0, spacingDu * 0.05));
            double triggerTol = Math.Max(eps * 25.0, targetClearance * 0.04);
            double boundaryPushThreshold = Math.Max(0.0, targetClearance - triggerTol) / 1.5;
            double baseStep = Math.Max(eps * 100.0, Math.Max(spacingDu * 0.04, targetClearance * 0.12));
            double maxShift = Math.Max(spacingDu * 0.75, targetClearance * 2.5);

            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                double d = Math.Sqrt(MinDistSquaredToRingEdges(ring, p));
                if (d >= boundaryPushThreshold)
                    continue;

                bool foundBest = false;
                var best = p;
                double bestDist = d;
                double bestShift = double.PositiveInfinity;

                if (TryNudgeAlongAxis(ring, pts, i, moveAlongX: true, spacingDu, alignTol, baseStep, maxShift, targetClearance, strictInteriorInsetDu, eps, keepouts, out var candX, out double distX, out double shiftX))
                {
                    best = candX;
                    bestDist = distX;
                    bestShift = shiftX;
                    foundBest = true;
                }

                if (TryNudgeAlongAxis(ring, pts, i, moveAlongX: false, spacingDu, alignTol, baseStep, maxShift, targetClearance, strictInteriorInsetDu, eps, keepouts, out var candY, out double distY, out double shiftY))
                {
                    if (!foundBest ||
                        distY > bestDist + eps * 2.0 ||
                        (Math.Abs(distY - bestDist) <= eps * 2.0 && shiftY < bestShift))
                    {
                        best = candY;
                        bestDist = distY;
                        bestShift = shiftY;
                        foundBest = true;
                    }
                }

                if (foundBest && bestDist > d + eps * 2.0)
                    pts[i] = best;
            }

            return pts;
        }

        private static bool TryNudgeAlongAxis(
            List<Point2d> ring,
            List<Point2d> points,
            int index,
            bool moveAlongX,
            double spacingDu,
            double alignTol,
            double baseStep,
            double maxShift,
            double targetClearance,
            double strictInteriorInsetDu,
            double eps,
            List<Extents2d> keepouts,
            out Point2d bestCandidate,
            out double bestDistance,
            out double bestShift)
        {
            bestCandidate = default;
            bestDistance = double.NegativeInfinity;
            bestShift = double.PositiveInfinity;
            if (ring == null || ring.Count < 3 || points == null || index < 0 || index >= points.Count)
                return false;

            if (!TryFindNearestAlignedNeighbor(points, index, moveAlongX, alignTol, out var n))
                return false;

            var p = points[index];
            double sign = moveAlongX ? Math.Sign(n.X - p.X) : Math.Sign(n.Y - p.Y);
            if (Math.Abs(sign) < 0.5)
                return false;

            bool found = false;
            for (double shift = baseStep; shift <= maxShift + eps; shift += baseStep)
            {
                var candRaw = moveAlongX
                    ? new Point2d(p.X + sign * shift, p.Y)
                    : new Point2d(p.X, p.Y + sign * shift);

                // Keep nudge results on the original lattice phase so no off-grid heads are introduced.
                var cand = moveAlongX
                    ? new Point2d(SnapToGridPhase(candRaw.X, spacingDu, p.X, eps), p.Y)
                    : new Point2d(p.X, SnapToGridPhase(candRaw.Y, spacingDu, p.Y, eps));

                if (Math.Abs(cand.X - p.X) <= eps && Math.Abs(cand.Y - p.Y) <= eps)
                    continue;

                if (!IsStrictlyInsideZone(ring, cand, strictInteriorInsetDu, eps))
                    continue;
                if (keepouts != null && keepouts.Count > 0 && PointInsideAnyKeepout(cand, keepouts))
                    continue;

                double dist = Math.Sqrt(MinDistSquaredToRingEdges(ring, cand));
                if (dist > bestDistance)
                {
                    bestDistance = dist;
                    bestCandidate = cand;
                    bestShift = shift;
                    found = true;
                }

                if (dist >= targetClearance - eps * 2.0)
                    break;
            }

            return found;
        }

        private static bool TryFindNearestAlignedNeighbor(
            List<Point2d> points,
            int index,
            bool moveAlongX,
            double alignTol,
            out Point2d neighbor)
        {
            neighbor = default;
            if (points == null || index < 0 || index >= points.Count)
                return false;

            var p = points[index];
            double bestD2 = double.PositiveInfinity;
            bool found = false;

            for (int i = 0; i < points.Count; i++)
            {
                if (i == index)
                    continue;
                var q = points[i];

                bool aligned = moveAlongX
                    ? Math.Abs(q.Y - p.Y) <= alignTol
                    : Math.Abs(q.X - p.X) <= alignTol;
                if (!aligned)
                    continue;

                double dx = q.X - p.X;
                double dy = q.Y - p.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 <= 1e-18)
                    continue;

                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    neighbor = q;
                    found = true;
                }
            }

            return found;
        }

        private static int CountMovedPoints(List<Point2d> before, List<Point2d> after, double tol)
        {
            if (before == null || after == null)
                return 0;
            int n = Math.Min(before.Count, after.Count);
            int moved = 0;
            double tolSq = tol * tol;
            for (int i = 0; i < n; i++)
            {
                double dx = before[i].X - after[i].X;
                double dy = before[i].Y - after[i].Y;
                if (dx * dx + dy * dy > tolSq)
                    moved++;
            }
            return moved;
        }

        private static bool TryClosestBoundaryPointOnVerticalLine(
            IList<Point2d> ring,
            double x,
            double yRef,
            double eps,
            out Point2d closest,
            out double dist)
        {
            closest = default;
            dist = double.PositiveInfinity;
            if (ring == null || ring.Count < 3)
                return false;

            bool any = false;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                if (!TryIntersectVerticalLineWithSegment(x, a, b, eps, out double yHit))
                    continue;

                double d = Math.Abs(yRef - yHit);
                if (d < dist)
                {
                    dist = d;
                    closest = new Point2d(x, yHit);
                    any = true;
                }
            }

            return any && dist < double.PositiveInfinity;
        }

        private static bool TryIntersectVerticalLineWithSegment(double x, Point2d a, Point2d b, double eps, out double yHit)
        {
            yHit = 0;
            double dx = b.X - a.X;
            if (Math.Abs(dx) <= eps * 5)
            {
                // Segment is vertical or near-vertical: treat as intersecting only if collinear, then pick nearer endpoint.
                if (Math.Abs(x - a.X) > eps * 5)
                    return false;
                yHit = Math.Abs(a.Y - b.Y) < eps * 5 ? a.Y : (Math.Abs(a.Y) < Math.Abs(b.Y) ? a.Y : b.Y);
                return true;
            }

            double t = (x - a.X) / dx;
            if (t < -eps || t > 1.0 + eps)
                return false;

            yHit = a.Y + t * (b.Y - a.Y);
            return true;
        }

        private static bool TryClosestPointOnRingEdges(IList<Point2d> ring, Point2d p, out Point2d closest, out double dist)
        {
            closest = default;
            dist = double.PositiveInfinity;
            if (ring == null || ring.Count < 2)
                return false;

            bool any = false;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                var q = ClosestPointOnSegment(p, a, b);
                double dx = p.X - q.X;
                double dy = p.Y - q.Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < dist)
                {
                    dist = d;
                    closest = q;
                    any = true;
                }
            }
            return any && dist < double.PositiveInfinity;
        }

        private static Point2d ClosestPointOnSegment(Point2d p, Point2d a, Point2d b)
        {
            double vx = b.X - a.X;
            double vy = b.Y - a.Y;
            double wx = p.X - a.X;
            double wy = p.Y - a.Y;

            double c2 = vx * vx + vy * vy;
            if (!(c2 > 1e-18))
                return a;

            double t = (vx * wx + vy * wy) / c2;
            if (t <= 0) return a;
            if (t >= 1) return b;
            return new Point2d(a.X + t * vx, a.Y + t * vy);
        }

        private static List<Extents2d> CollectShaftKeepouts(Database db, List<Point2d> zoneRing, double padDu)
        {
            var keepouts = new List<Extents2d>();
            if (db == null || zoneRing == null || zoneRing.Count < 3)
                return keepouts;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is BlockReference br))
                        continue;

                    string name = GetBlockName(br, tr);
                    if (!string.Equals(name, "shaft", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var ins = new Point2d(br.Position.X, br.Position.Y);
                    if (!PointInPolygon(zoneRing, ins.X, ins.Y))
                        continue;

                    try
                    {
                        var e3 = br.GeometricExtents;
                        var min = new Point2d(e3.MinPoint.X - padDu, e3.MinPoint.Y - padDu);
                        var max = new Point2d(e3.MaxPoint.X + padDu, e3.MaxPoint.Y + padDu);
                        keepouts.Add(new Extents2d(min, max));
                    }
                    catch
                    {
                        // If extents can't be read, fall back to a small keepout around insertion.
                        double r = Math.Max(padDu, 1.0);
                        keepouts.Add(new Extents2d(
                            new Point2d(ins.X - r, ins.Y - r),
                            new Point2d(ins.X + r, ins.Y + r)));
                    }
                }
                tr.Commit();
            }

            return keepouts;
        }

        /// <summary>
        /// Returns a conservative inset so the entire sprinkler block graphics stay inside the boundary,
        /// not only the insertion point. Falls back to 0 when block definition/extents are unavailable.
        /// </summary>
        private static double GetSprinklerVisualInsetDu(Database db, double eps)
        {
            if (db == null)
                return 0.0;

            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    string sprinklerBlockName = SprinklerLayers.GetConfiguredSprinklerBlockName();
                    ObjectId blockId = ObjectId.Null;
                    if (bt.Has(sprinklerBlockName))
                    {
                        blockId = bt[sprinklerBlockName];
                    }
                    else
                    {
                        foreach (ObjectId oid in bt)
                        {
                            var cand = tr.GetObject(oid, OpenMode.ForRead, false) as BlockTableRecord;
                            if (cand == null || cand.IsLayout || cand.IsAnonymous)
                                continue;
                            if (!string.Equals(cand.Name, sprinklerBlockName, StringComparison.OrdinalIgnoreCase))
                                continue;
                            blockId = oid;
                            break;
                        }
                    }
                    if (blockId.IsNull)
                        return 0.0;

                    var btr = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
                    if (btr == null)
                        return 0.0;

                    // Compute the farthest corner distance from block origin in block space.
                    // This over-approximates symbol footprint and safely keeps it inside boundaries.
                    double maxRadiusSq = 0.0;
                    bool anyExtents = false;
                    foreach (ObjectId id in btr)
                    {
                        Entity ent;
                        try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                        catch { ent = null; }
                        if (ent == null)
                            continue;

                        Extents3d ext;
                        try { ext = ent.GeometricExtents; }
                        catch { continue; }

                        anyExtents = true;
                        AccumulateCornerRadiusSq(ext.MinPoint.X, ext.MinPoint.Y, ref maxRadiusSq);
                        AccumulateCornerRadiusSq(ext.MinPoint.X, ext.MaxPoint.Y, ref maxRadiusSq);
                        AccumulateCornerRadiusSq(ext.MaxPoint.X, ext.MinPoint.Y, ref maxRadiusSq);
                        AccumulateCornerRadiusSq(ext.MaxPoint.X, ext.MaxPoint.Y, ref maxRadiusSq);
                    }

                    if (!anyExtents || !(maxRadiusSq > 0.0))
                        return 0.0;

                    double visualRadius = Math.Sqrt(maxRadiusSq);
                    // Small safety pad for numerical noise and anti-aliased visual thickness.
                    return visualRadius + Math.Max(eps * 4.0, 1e-6);
                }
            }
            catch
            {
                return 0.0;
            }
        }

        private static void AccumulateCornerRadiusSq(double x, double y, ref double maxRadiusSq)
        {
            double r2 = x * x + y * y;
            if (r2 > maxRadiusSq)
                maxRadiusSq = r2;
        }

        private static string GetBlockName(BlockReference br, Transaction tr)
        {
            var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
            if (!br.IsDynamicBlock)
                return btr.Name;
            if (!br.DynamicBlockTableRecord.IsNull)
            {
                var dyn = (BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead);
                return dyn.Name;
            }
            return btr.Name;
        }

        private static List<Point2d> FilterByKeepouts(List<Point2d> points, List<Extents2d> keepouts)
        {
            if (points == null || points.Count == 0 || keepouts == null || keepouts.Count == 0)
                return points ?? new List<Point2d>();

            var outPts = new List<Point2d>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                if (!PointInsideAnyKeepout(points[i], keepouts))
                    outPts.Add(points[i]);
            }
            return outPts;
        }

        private static bool PointInsideAnyKeepout(Point2d p, List<Extents2d> keepouts)
        {
            for (int i = 0; i < keepouts.Count; i++)
            {
                var e = keepouts[i];
                if (p.X >= e.MinPoint.X && p.X <= e.MaxPoint.X &&
                    p.Y >= e.MinPoint.Y && p.Y <= e.MaxPoint.Y)
                    return true;
            }
            return false;
        }
    }
}


