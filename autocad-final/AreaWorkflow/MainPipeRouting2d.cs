using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Agent;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    public static class MainPipeRouting2d
    {
        /// <summary>Appended to route summary when a strict orthogonal trunk scanline cannot serve every head.</summary>
        private const string OrthogonalCoverageWarningText =
            "Warning: some sprinklers may need non-orthogonal branches.";

        public sealed class RouteResult
        {
            public List<Point2d> Sprinklers;
            public List<Point2d> TrunkPath;
            public List<Point2d> ConnectorPath;
            public bool TrunkIsHorizontal;
            public string Summary;
        }

        public static bool TryRoute(
            Polyline zoneBoundary,
            Point3d shaftPoint,
            Database db,
            double sprinklerSpacingMeters,
            double sprinklerCoverageRadiusMeters,
            out RouteResult result,
            out string errorMessage,
            string preferredOrientation = null,
            string strategy = null,
            double? skeletonCellSizeMeters = null,
            double? skeletonMinClearanceMeters = null,
            double? skeletonPruneBranchLengthMeters = null,
            double? mainPipeLengthPenalty = null,
            List<Point2d> sprinklersOverride = null)
        {
            result = null;
            errorMessage = null;

            if (zoneBoundary == null)
            {
                errorMessage = "Zone boundary is null.";
                return false;
            }

            // Sample ring first so extent can inform unit auto-detection.
            AgentLog.Write("TryRoute", "GetRingForPlanarClipping");
            List<Point2d> ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zoneBoundary);
            if (ring.Count < 3)
            {
                errorMessage = "Zone boundary must be a closed polygon with at least 3 vertices.";
                return false;
            }

            GetExtents(ring, out double minX, out double minY, out double maxX, out double maxY);
            double extent = Math.Max(maxX - minX, maxY - minY);
            double eps = Math.Max(BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db), 1e-9 * Math.Max(extent, 1.0));

            // Auto-detect drawing scale from zone extent so the grid is correct even when
            // INSUNITS doesn't match the actual coordinate scale of the drawing.
            if (!DrawingUnitsHelper.TryAutoGetDrawingScale(db.Insunits, sprinklerSpacingMeters, extent, out double duPerMeter) || duPerMeter <= 0)
            {
                errorMessage = "Could not determine drawing scale (check INSUNITS).";
                return false;
            }
            double spacingDu = sprinklerSpacingMeters * duPerMeter;
            double offsetDu  = sprinklerCoverageRadiusMeters * duPerMeter;
            AgentLog.Write("TryRoute", "extent=" + extent.ToString("F0") + " duPerMeter=" + duPerMeter.ToString("G4") + " spacingDu=" + spacingDu.ToString("F0") + " offsetDu=" + offsetDu.ToString("F0"));

            // A) Sprinklers: either override (existing heads) or generated grid.
            List<Point2d> sprinklers;
            string placeSummary = null;
            bool requireOrthogonalCoverage = sprinklersOverride != null;
            if (sprinklersOverride != null)
            {
                sprinklers = new List<Point2d>(sprinklersOverride);
                if (sprinklers.Count == 0)
                {
                    errorMessage = "No sprinklers were provided for routing.";
                    return false;
                }
                AgentLog.Write("TryRoute", "sprinklersOverride count=" + sprinklers.Count);
            }
            else
            {
                AgentLog.Write("TryRoute", "TryPlaceForZoneRing start");
                if (!SprinklerGridPlacement2d.TryPlaceForZoneRing(
                        ring,
                        db,
                        spacingMeters: sprinklerSpacingMeters,
                        coverageRadiusMeters: sprinklerCoverageRadiusMeters,
                        maxBoundarySprinklerGapMeters: RuntimeSettings.Load().SprinklerToBoundaryDistanceM,
                        out var place,
                        out string placeErr))
                {
                    errorMessage = placeErr;
                    return false;
                }

                sprinklers = place.Sprinklers ?? new List<Point2d>();
                if (sprinklers.Count == 0)
                {
                    errorMessage = "Sprinkler placement produced 0 points.";
                    return false;
                }
                placeSummary = place.Summary;
                AgentLog.Write("TryRoute", "TryPlaceForZoneRing done sprinklers=" + sprinklers.Count);
            }

            // B) Choose trunk orientation and best trunk segment (or skeleton path).
            string strategyNorm = string.IsNullOrWhiteSpace(strategy) ? "grid_aligned" : strategy.Trim().ToLowerInvariant();
            // Allow skeleton even when routing from existing heads; it won't guarantee clean orthogonal drops.
            bool wantSkeleton = string.Equals(strategyNorm, "skeleton", StringComparison.OrdinalIgnoreCase);
            if (requireOrthogonalCoverage && !wantSkeleton && strategyNorm != "sprinkler_driven" && strategyNorm != "grid_aligned")
            {
                // For existing heads, route a single straight trunk that can orthogonally reach all sprinklers.
                strategyNorm = "sprinkler_driven";
            }
            bool wantCenterL = string.Equals(strategyNorm, "center_l", StringComparison.OrdinalIgnoreCase)
                || string.Equals(strategyNorm, "centerl", StringComparison.OrdinalIgnoreCase);
            bool trunkHorizontal = string.Equals(preferredOrientation, "horizontal", StringComparison.OrdinalIgnoreCase) ? true
                                 : string.Equals(preferredOrientation, "vertical",   StringComparison.OrdinalIgnoreCase) ? false
                                 : ChooseGridDominantOrientation(sprinklers, eps * 20.0, fallback: ChooseTrunkOrientation(sprinklers));
            bool orientationLocked = string.Equals(preferredOrientation, "horizontal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(preferredOrientation, "vertical", StringComparison.OrdinalIgnoreCase);
            AgentLog.Write("TryRoute", "strategy=" + strategyNorm + " initial horiz=" + trunkHorizontal);

            if (wantCenterL)
            {
                // Simple, stable “route main pipe” behavior:
                // - trunk is a straight H/V scanline through the zone centroid (nudged if needed)
                // - connector meets the trunk at the point closest to the shaft / snapped entry
                //   (short orthogonal or A* path inside the zone).
                Point2d mid = PolygonUtils.ApproxCentroidAreaWeighted(ring);
                NudgeMidpointOffSprinklers(ref mid, sprinklers, spacingDu, eps);

                double desiredAxis = trunkHorizontal ? mid.Y : mid.X;
                Segment trunkSegCenter;
                if (!TryPickLongestSpanNearAxisSearch(ring, desiredAxis, trunkHorizontal, eps, out trunkSegCenter))
                {
                    // If centroid axis doesn't intersect the polygon (rare for concave), try a spine-like search.
                    if (!TryPickSpineTrunkSegment(ring, trunkHorizontal, eps, out trunkSegCenter, out _))
                    {
                        errorMessage = "Could not place a centered trunk inside the zone.";
                        return false;
                    }
                }

                var trunkPathCenter = new List<Point2d>(2)
                {
                    new Point2d(trunkSegCenter.X0, trunkSegCenter.Y0),
                    new Point2d(trunkSegCenter.X1, trunkSegCenter.Y1)
                };

                var trunkSegC = trunkSegCenter;
                var shaft2C = new Point2d(shaftPoint.X, shaftPoint.Y);
                var ep0C = new Point2d(trunkSegC.X0, trunkSegC.Y0);
                var ep1C = new Point2d(trunkSegC.X1, trunkSegC.Y1);

                bool shaftOutsideC = !PointInPolygon(ring, shaft2C.X, shaft2C.Y);
                Point2d connectStartC = shaft2C;
                if (shaftOutsideC)
                {
                    double minNudge = spacingDu / 4.0;
                    var snapAimC = ClosestPointOnSegmentL2(shaft2C, trunkSegC);
                    bool snappedC = TrySnapInsideRing(ring, shaft2C, snapAimC, eps, minNudge, out connectStartC);
                    if (!snappedC)
                    {
                        GetExtents(ring, out double cxMinC, out double cyMinC, out double cxMaxC, out double cyMaxC);
                        var centroidC = new Point2d(0.5 * (cxMinC + cxMaxC), 0.5 * (cyMinC + cyMaxC));
                        snappedC = TrySnapInsideRing(ring, shaft2C, centroidC, eps, minNudge, out connectStartC);
                    }
                    if (!snappedC)
                    {
                        errorMessage = "Shaft is outside the zone and no entry point found on the boundary.";
                        return false;
                    }
                }

                Point2d targetC = ClosestPointOnSegmentL2(connectStartC, trunkSegC);
                if (targetC.GetDistanceTo(ep0C) <= eps) targetC = ep0C;
                else if (targetC.GetDistanceTo(ep1C) <= eps) targetC = ep1C;

                if (!TryOrthogonalConnectInside(ring, connectStartC, targetC, spacingDu / 6.0, out var connectorC))
                {
                    if (!TryAStarConnectInside(ring, connectStartC, targetC, spacingDu / 6.0, eps, out connectorC))
                    {
                        errorMessage = "Could not route an orthogonal connector inside the zone.";
                        return false;
                    }
                }

                if (shaftOutsideC && connectorC != null && connectorC.Count > 0
                    && shaft2C.GetDistanceTo(connectorC[0]) > eps)
                {
                    connectorC.Insert(0, shaft2C);
                }

                result = new RouteResult
                {
                    Sprinklers = sprinklers,
                    TrunkPath = SimplifyCollinear(trunkPathCenter, eps),
                    ConnectorPath = SimplifyCollinear(connectorC, eps),
                    TrunkIsHorizontal = trunkHorizontal,
                    Summary = string.Format(
                        CultureInfo.InvariantCulture,
                        "Sprinklers: {0}. Trunk: {1} (centered). {2}",
                        sprinklers.Count,
                        trunkHorizontal ? "horizontal" : "vertical",
                        placeSummary ?? string.Empty)
                };
                return true;
            }

            // Skeleton strategy: build the trunk as a medial-axis path.
            // Supported even for existing heads; caller/summary will warn about potential non-orthogonal branches.
            if (strategyNorm == "skeleton")
            {
                var shaft2sk = new Point2d(shaftPoint.X, shaftPoint.Y);
                bool shaftOutSk = !PointInPolygon(ring, shaft2sk.X, shaft2sk.Y);
                Point2d entryInsideSk = shaft2sk;
                if (shaftOutSk)
                {
                    // Approximate target before we have a trunk: use ring centroid for nudge direction.
                    GetExtents(ring, out double cxMin, out double cyMin, out double cxMax, out double cyMax);
                    var centroid = new Point2d(0.5 * (cxMin + cxMax), 0.5 * (cyMin + cyMax));
                    double minNudgeSk = spacingDu / 4.0;
                    if (!TrySnapInsideRing(ring, shaft2sk, centroid, eps, minNudgeSk, out entryInsideSk))
                    {
                        errorMessage = "Shaft is outside the zone and no entry point found on the boundary.";
                        return false;
                    }
                }

                double cellSize = (skeletonCellSizeMeters.HasValue && skeletonCellSizeMeters.Value > 0)
                    ? skeletonCellSizeMeters.Value * duPerMeter
                    : Math.Max(spacingDu / 4.0, eps * 50.0);
                double minClr = (skeletonMinClearanceMeters.HasValue && skeletonMinClearanceMeters.Value >= 0)
                    ? skeletonMinClearanceMeters.Value * duPerMeter
                    : sprinklerCoverageRadiusMeters * duPerMeter * 0.5;
                double pruneLen = (skeletonPruneBranchLengthMeters.HasValue && skeletonPruneBranchLengthMeters.Value >= 0)
                    ? skeletonPruneBranchLengthMeters.Value * duPerMeter
                    : sprinklerSpacingMeters * duPerMeter; // one spacing default

                var skelOpt = new SkeletonRouting2d.Options
                {
                    CellSizeDu      = cellSize,
                    MinClearanceDu  = minClr,
                    PruneLengthDu   = pruneLen
                };

                AgentLog.Write("TryRoute", "Skeleton opts cellDu=" + cellSize.ToString("F1") + " clrDu=" + minClr.ToString("F1") + " pruneDu=" + pruneLen.ToString("F1"));

                if (!SkeletonRouting2d.TryBuildTrunk(ring, entryInsideSk, skelOpt, out var skelResult, out string skelErr))
                {
                    errorMessage = "Skeleton routing failed: " + skelErr;
                    return false;
                }

                var trunkPathSk = skelResult.TrunkPath;
                // Ensure trunk starts exactly at entryInsideSk so the connector lines up.
                if (trunkPathSk.Count > 0 && trunkPathSk[0].GetDistanceTo(entryInsideSk) > eps)
                    trunkPathSk.Insert(0, entryInsideSk);

                // Connector = shaft → entryInsideSk (if shaft is outside) or empty step.
                var connectorSk = new List<Point2d>();
                if (shaftOutSk)
                    connectorSk.Add(shaft2sk);
                connectorSk.Add(entryInsideSk);

                result = new RouteResult
                {
                    Sprinklers        = sprinklers,
                    TrunkPath         = SimplifyCollinear(trunkPathSk, eps),
                    ConnectorPath     = SimplifyCollinear(connectorSk, eps),
                    TrunkIsHorizontal = skelResult.TrunkIsHorizontal,
                    Summary = string.Format(
                        CultureInfo.InvariantCulture,
                        "Sprinklers: {0}. Trunk (skeleton): {1} vertices, length {2:F1}. {3}",
                        sprinklers.Count,
                        trunkPathSk.Count,
                        skelResult.TrunkLengthDu,
                        placeSummary ?? string.Empty)
                };
                if (sprinklersOverride != null)
                {
                    result.Summary = (result.Summary ?? string.Empty) +
                        (string.IsNullOrEmpty(result.Summary) ? string.Empty : "\n") +
                        OrthogonalCoverageWarningText;
                }
                return true;
            }

            AgentLog.Write("TryRoute", "TryPickBestTrunkSegment start horizontal=" + trunkHorizontal);

            Segment trunkSeg = default;
            string trunkErr = null;
            bool ok = false;
            double lambda = (mainPipeLengthPenalty.HasValue && mainPipeLengthPenalty.Value >= 0)
                ? mainPipeLengthPenalty.Value
                : 0.1;
            switch (strategyNorm)
            {
                case "spine":
                    if (requireOrthogonalCoverage)
                    {
                        ok = false;
                        trunkErr = "Spine trunk strategy is not supported when routing from existing sprinkler heads.";
                    }
                    else
                    {
                        ok = TryPickSpineTrunkSegment(ring, trunkHorizontal, eps, out trunkSeg, out trunkErr);
                    }
                    break;
                case "shortest_path":
                    if (requireOrthogonalCoverage)
                    {
                        ok = TryPickMidpointBasedCoveringTrunk(
                            ring, sprinklers, spacingDu, eps, lambda,
                            orientationLocked, trunkHorizontal,
                            out trunkSeg, out bool midHzSp, out trunkErr);
                        if (ok)
                            trunkHorizontal = midHzSp;
                    }
                    else
                    {
                        ok = TryPickBestTrunkSegment(ring, sprinklers, spacingDu, eps, trunkHorizontal, lambda, out trunkSeg, out trunkErr);
                    }
                    break;
                case "sprinkler_driven":
                    // Zone centroid (nudged off any sprinkler), trunk through that point; H vs V from branch-distance cost.
                    ok = TryPickMidpointBasedCoveringTrunk(
                        ring, sprinklers, spacingDu, eps, lambda,
                        orientationLocked, trunkHorizontal,
                        out trunkSeg, out bool midHoriz, out trunkErr);
                    if (ok)
                        trunkHorizontal = midHoriz;
                    break;
                case "grid_aligned":
                default:
                    if (requireOrthogonalCoverage)
                    {
                        ok = TryPickMidpointBasedCoveringTrunk(
                            ring, sprinklers, spacingDu, eps, lambda,
                            orientationLocked, trunkHorizontal,
                            out trunkSeg, out bool midHzGrid, out trunkErr);
                        if (ok)
                            trunkHorizontal = midHzGrid;
                    }
                    else
                    {
                        ok = TryPickGridAlignedTrunkSegment(ring, sprinklers, spacingDu, eps, trunkHorizontal, out trunkSeg, out trunkErr)
                             || TryPickBestTrunkSegment(ring, sprinklers, spacingDu, eps, trunkHorizontal, lambda, out trunkSeg, out trunkErr);
                    }
                    break;
            }

            string orthogonalCoverageWarning = null;
            if (!ok && requireOrthogonalCoverage)
            {
                AgentLog.Write("TryRoute", "Orthogonal covering trunk unavailable — fallback to best-effort sprinkler-driven trunk.");
                if (TryPickSprinklerDrivenTrunk(ring, sprinklers, spacingDu, eps, lambda, requireCoverAll: false,
                        out trunkSeg, out trunkHorizontal, out trunkErr))
                {
                    ok = true;
                    orthogonalCoverageWarning = OrthogonalCoverageWarningText;
                }
            }

            if (!ok)
            {
                errorMessage = trunkErr;
                return false;
            }

            if (requireOrthogonalCoverage)
            {
                double clusterTol = Math.Max(eps * 20.0, spacingDu * 0.2);
                if (!SegmentCoversAllSprinklers(sprinklers, trunkSeg, trunkHorizontal, clusterTol))
                {
                    AgentLog.Write("TryRoute", "Trunk span does not cover all sprinklers — routing anyway.");
                    orthogonalCoverageWarning = orthogonalCoverageWarning ?? OrthogonalCoverageWarningText;
                }
            }

            var trunkPath = new List<Point2d>(2)
            {
                new Point2d(trunkSeg.X0, trunkSeg.Y0),
                new Point2d(trunkSeg.X1, trunkSeg.Y1)
            };

            AgentLog.Write("TryRoute", "TryPickBestTrunkSegment done");
            // C) Connect shaft to trunk at the point on the trunk closest to the in-zone routing start
            // (minimizes connector length vs tying the hit point to the zone centroid).
            var shaft2 = new Point2d(shaftPoint.X, shaftPoint.Y);
            var ep0 = new Point2d(trunkSeg.X0, trunkSeg.Y0);
            var ep1 = new Point2d(trunkSeg.X1, trunkSeg.Y1);

            // Shafts/risers often sit on or outside the zone boundary — snap the connector's
            // start point to just inside the ring so the orthogonal/A* routers don't reject it.
            bool shaftOutside = !PointInPolygon(ring, shaft2.X, shaft2.Y);
            Point2d connectStart = shaft2;
            if (shaftOutside)
            {
                // Nudge at least one A* cell inward so the cell-center inside check doesn't reject the entry.
                double minNudge = spacingDu / 4.0;
                var snapAim = ClosestPointOnSegmentL2(shaft2, trunkSeg);
                bool snapped = TrySnapInsideRing(ring, shaft2, snapAim, eps, minNudge, out connectStart);
                if (!snapped)
                {
                    // Fallback: ray toward bbox center (same as prior centroid fallback for snap only).
                    GetExtents(ring, out double cxMin, out double cyMin, out double cxMax, out double cyMax);
                    var centroid = new Point2d(0.5 * (cxMin + cxMax), 0.5 * (cyMin + cyMax));
                    snapped = TrySnapInsideRing(ring, shaft2, centroid, eps, minNudge, out connectStart);
                }
                if (!snapped)
                {
                    AgentLog.Write("TryRoute", "shaft outside ring and snap failed");
                    errorMessage = "Shaft is outside the zone and no entry point found on the boundary.";
                    return false;
                }
                AgentLog.Write("TryRoute", "shaft outside ring — snapped entry to ("
                    + connectStart.X.ToString("F2", CultureInfo.InvariantCulture) + ","
                    + connectStart.Y.ToString("F2", CultureInfo.InvariantCulture) + ")");
            }

            var target = ClosestPointOnSegmentL2(connectStart, trunkSeg);
            if (target.GetDistanceTo(ep0) <= eps) target = ep0;
            else if (target.GetDistanceTo(ep1) <= eps) target = ep1;

            AgentLog.Write("TryRoute", "TryOrthogonalConnectInside start");
            if (!TryOrthogonalConnectInside(ring, connectStart, target, spacingDu / 6.0, out var connector))
            {
                // Fallback to A* inside polygon on a coarse grid.
                AgentLog.Write("TryRoute", "TryAStarConnectInside start");
                if (!TryAStarConnectInside(ring, connectStart, target, spacingDu / 6.0, eps, out connector))
                {
                    AgentLog.Write("TryRoute", "TryAStarConnectInside failed");
                    errorMessage = "Could not route an orthogonal connector inside the zone.";
                    return false;
                }
                AgentLog.Write("TryRoute", "TryAStarConnectInside done");
            }
            else
            {
                AgentLog.Write("TryRoute", "TryOrthogonalConnectInside done");
            }

            // Prepend the true shaft position so the drawn connector starts at the riser,
            // crossing the boundary as a single visible segment.
            if (shaftOutside && connector != null && connector.Count > 0
                && shaft2.GetDistanceTo(connector[0]) > eps)
            {
                connector.Insert(0, shaft2);
            }

            string trunkNote = requireOrthogonalCoverage ? ", through zone centroid (branch-cost)" : string.Empty;
            string summaryTail = placeSummary ?? string.Empty;
            if (!string.IsNullOrEmpty(orthogonalCoverageWarning))
                summaryTail = string.IsNullOrEmpty(summaryTail)
                    ? orthogonalCoverageWarning
                    : summaryTail + "\n" + orthogonalCoverageWarning;

            result = new RouteResult
            {
                Sprinklers = sprinklers,
                TrunkPath = SimplifyCollinear(trunkPath, eps),
                ConnectorPath = SimplifyCollinear(connector, eps),
                TrunkIsHorizontal = trunkHorizontal,
                Summary = string.Format(
                    CultureInfo.InvariantCulture,
                    "Sprinklers: {0}. Trunk: {1}{2}. {3}",
                    sprinklers.Count,
                    trunkHorizontal ? "horizontal" : "vertical",
                    trunkNote,
                    summaryTail)
            };
            return true;
        }

        private static bool TryPickLongestSpanNearAxis(List<Point2d> ring, double axis, bool horizontal, double eps, out Segment best)
        {
            best = default;
            double bestLen = 0;
            bool found = false;

            if (horizontal)
            {
                foreach (var s in HorizontalSegmentsAtY(ring, axis, eps))
                {
                    double len = Math.Abs(s.X1 - s.X0);
                    if (len > bestLen)
                    {
                        bestLen = len;
                        best = new Segment(s.X0, axis, s.X1, axis);
                        found = true;
                    }
                }
            }
            else
            {
                foreach (var s in VerticalSegmentsAtX(ring, axis, eps))
                {
                    double len = Math.Abs(s.Y1 - s.Y0);
                    if (len > bestLen)
                    {
                        bestLen = len;
                        best = new Segment(axis, s.Y0, axis, s.Y1);
                        found = true;
                    }
                }
            }

            return found;
        }

        private static bool TryPickLongestSpanNearAxisSearch(
            List<Point2d> ring,
            double desiredAxis,
            bool horizontal,
            double eps,
            out Segment best)
        {
            best = default;
            if (ring == null || ring.Count < 3)
                return false;

            GetExtents(ring, out double minX, out double minY, out double maxX, out double maxY);
            double ortho = horizontal ? (maxY - minY) : (maxX - minX);
            double step = Math.Max(ortho * 0.02, eps * 50.0);
            int maxAttempts = 35;

            bool foundAny = false;
            double bestLen = 0;

            // Evaluate desired axis first, then alternating nudges above/below to find the longest usable span.
            for (int attempt = 0; attempt <= maxAttempts; attempt++)
            {
                double axis;
                if (attempt == 0)
                {
                    axis = desiredAxis;
                }
                else
                {
                    int sign = (attempt % 2 == 1) ? 1 : -1;
                    int mag = (attempt + 1) / 2;
                    axis = desiredAxis + sign * mag * step;
                }

                Segment cand;
                if (!TryPickLongestSpanNearAxis(ring, axis, horizontal, eps, out cand))
                    continue;

                double len = horizontal ? Math.Abs(cand.X1 - cand.X0) : Math.Abs(cand.Y1 - cand.Y0);
                if (!foundAny || len > bestLen)
                {
                    bestLen = len;
                    best = cand;
                    foundAny = true;
                }
            }

            return foundAny;
        }

        /// <summary>
        /// Builds a connector polyline from a shaft point to a given straight trunk segment, attempting to keep the path inside the zone.
        /// Uses the same orthogonal-then-A* fallback strategy as <see cref="TryRoute"/>.
        /// </summary>
        public static bool TryBuildConnectorInsideZone(
            List<Point2d> zoneRing,
            Database db,
            Point2d shaftPoint,
            Point2d trunkStart,
            Point2d trunkEnd,
            double sprinklerSpacingMeters,
            out List<Point2d> connectorPath,
            out string errorMessage)
        {
            connectorPath = null;
            errorMessage = null;

            if (zoneRing == null || zoneRing.Count < 3)
            {
                errorMessage = "Zone boundary ring is invalid.";
                return false;
            }
            if (db == null)
            {
                errorMessage = "Database is null.";
                return false;
            }

            GetExtents(zoneRing, out double minX, out double minY, out double maxX, out double maxY);
            double extent = Math.Max(maxX - minX, maxY - minY);
            double eps = Math.Max(BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db), 1e-9 * Math.Max(extent, 1.0));

            if (!DrawingUnitsHelper.TryAutoGetDrawingScale(db.Insunits, sprinklerSpacingMeters, extent, out double duPerMeter) || duPerMeter <= 0)
            {
                errorMessage = "Could not determine drawing scale (check INSUNITS).";
                return false;
            }
            double spacingDu = sprinklerSpacingMeters * duPerMeter;

            var trunkSegConn = new Segment(trunkStart.X, trunkStart.Y, trunkEnd.X, trunkEnd.Y);
            var snapAimConn = ClosestPointOnSegmentL2(shaftPoint, trunkSegConn);

            bool shaftOutside = !PointInPolygon(zoneRing, shaftPoint.X, shaftPoint.Y);
            Point2d connectStart = shaftPoint;
            if (shaftOutside)
            {
                double minNudge = spacingDu / 4.0;
                bool snappedConn = TrySnapInsideRing(zoneRing, shaftPoint, snapAimConn, eps, minNudge, out connectStart);
                if (!snappedConn)
                {
                    GetExtents(zoneRing, out double cxMinZ, out double cyMinZ, out double cxMaxZ, out double cyMaxZ);
                    var centroidZ = new Point2d(0.5 * (cxMinZ + cxMaxZ), 0.5 * (cyMinZ + cyMaxZ));
                    snappedConn = TrySnapInsideRing(zoneRing, shaftPoint, centroidZ, eps, minNudge, out connectStart);
                }
                if (!snappedConn)
                {
                    errorMessage = "Shaft is outside the zone and no entry point found on the boundary.";
                    return false;
                }
            }

            var target = ClosestPointOnSegmentL2(connectStart, trunkSegConn);

            if (!TryOrthogonalConnectInside(zoneRing, connectStart, target, spacingDu / 6.0, out var connector))
            {
                if (!TryAStarConnectInside(zoneRing, connectStart, target, spacingDu / 6.0, eps, out connector))
                {
                    errorMessage = "Could not route an orthogonal connector inside the zone.";
                    return false;
                }
            }

            if (shaftOutside && connector != null && connector.Count > 0
                && shaftPoint.GetDistanceTo(connector[0]) > eps)
            {
                connector.Insert(0, shaftPoint);
            }

            connectorPath = SimplifyCollinear(connector, eps);
            return true;
        }

        private readonly struct Segment
        {
            public readonly double X0, Y0, X1, Y1;
            public Segment(double x0, double y0, double x1, double y1)
            {
                X0 = x0; Y0 = y0; X1 = x1; Y1 = y1;
            }
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

        // Sprinkler placement now lives in SprinklerGridPlacement2d.

        private static bool ChooseTrunkOrientation(List<Point2d> sprinklers)
        {
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var p in sprinklers)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            return (maxX - minX) >= (maxY - minY);
        }

        /// <summary>
        /// Prefers whichever axis produces the strongest regular grid — for a sprinkler layout
        /// with M unique rows × N unique columns, a horizontal trunk is best when N (columns) ≥ M (rows),
        /// because it lets us drop one perpendicular branch per column. Falls back to the bbox heuristic
        /// when rows/columns can't be resolved (sparse or noisy layouts).
        /// </summary>
        private static bool ChooseGridDominantOrientation(List<Point2d> sprinklers, double clusterTol, bool fallback)
        {
            if (sprinklers == null || sprinklers.Count < 2) return fallback;
            int rows = CountUniqueScalars(sprinklers, p => p.Y, clusterTol);
            int cols = CountUniqueScalars(sprinklers, p => p.X, clusterTol);
            if (rows <= 1 && cols <= 1) return fallback;
            // Horizontal trunk when there are at least as many distinct columns as rows.
            return cols >= rows;
        }

        private static int CountUniqueScalars(List<Point2d> pts, Func<Point2d, double> sel, double tol)
        {
            var vals = new List<double>(pts.Count);
            foreach (var p in pts) vals.Add(sel(p));
            vals.Sort();
            int unique = 0;
            double last = double.NegativeInfinity;
            for (int i = 0; i < vals.Count; i++)
            {
                if (i == 0 || Math.Abs(vals[i] - last) > tol) { unique++; last = vals[i]; }
            }
            return unique;
        }

        /// <summary>
        /// Grid-aligned trunk: pick the sprinkler scanline (row or column) whose inside segment
        /// perpendicularly intersects the most columns/rows, with a tiebreaker that prefers rows
        /// closest to the grid's geometric median. Every sprinkler gets a clean perpendicular drop.
        /// </summary>
        private static bool TryPickGridAlignedTrunkSegment(
            List<Point2d> ring,
            List<Point2d> sprinklers,
            double spacingDu,
            double eps,
            bool horizontal,
            out Segment best,
            out string errorMessage)
        {
            best = default;
            errorMessage = null;
            if (sprinklers == null || sprinklers.Count == 0)
            {
                errorMessage = "No sprinklers to align grid trunk to.";
                return false;
            }

            double clusterTol = Math.Max(eps * 20.0, spacingDu * 0.2);

            // Gather unique row (or column) positions as grid lines.
            var lines = new List<double>();
            foreach (var p in sprinklers) lines.Add(horizontal ? p.Y : p.X);
            lines.Sort();
            lines = DedupScalars(lines, clusterTol);
            if (lines.Count == 0)
            {
                errorMessage = "No grid scanlines available.";
                return false;
            }

            double median = lines[lines.Count / 2];

            double bestScore = double.MaxValue;
            bool found = false;
            foreach (double c in lines)
            {
                if (horizontal)
                {
                    foreach (var s in HorizontalSegmentsAtY(ring, c, eps))
                    {
                        int covered = CountPerpendicularlyCovered(sprinklers, c, true, s.X0, s.X1, clusterTol);
                        if (covered <= 0) continue;
                        double missed = sprinklers.Count - covered;
                        double offCentre = Math.Abs(c - median);
                        double length = Math.Abs(s.X1 - s.X0);
                        double score = missed * 1000.0 + offCentre - length * 1e-6;
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = new Segment(s.X0, c, s.X1, c);
                            found = true;
                        }
                    }
                }
                else
                {
                    foreach (var s in VerticalSegmentsAtX(ring, c, eps))
                    {
                        int covered = CountPerpendicularlyCovered(sprinklers, c, false, s.Y0, s.Y1, clusterTol);
                        if (covered <= 0) continue;
                        double missed = sprinklers.Count - covered;
                        double offCentre = Math.Abs(c - median);
                        double length = Math.Abs(s.Y1 - s.Y0);
                        double score = missed * 1000.0 + offCentre - length * 1e-6;
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = new Segment(c, s.Y0, c, s.Y1);
                            found = true;
                        }
                    }
                }
            }

            if (!found)
            {
                errorMessage = "Grid-aligned trunk could not fit inside the zone on any sprinkler row/column.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Count of sprinklers that can drop a perpendicular branch from the trunk scanline —
        /// sprinkler (x,y) is covered when its orthogonal foot on the scanline lies within [a0,a1].
        /// </summary>
        private static int CountPerpendicularlyCovered(
            List<Point2d> sprinklers,
            double scanValue,
            bool horizontal,
            double a0,
            double a1,
            double clusterTol)
        {
            double lo = Math.Min(a0, a1) - clusterTol;
            double hi = Math.Max(a0, a1) + clusterTol;
            int n = 0;
            foreach (var p in sprinklers)
            {
                double along = horizontal ? p.X : p.Y;
                if (along >= lo && along <= hi) n++;
            }
            return n;
        }

        /// <summary>
        /// Spine trunk: run along the zone's median axis (not a sprinkler row) using the longest
        /// inside segment. Used when the sprinkler grid is sparse/irregular so no single row
        /// would carry enough branches.
        /// </summary>
        private static bool TryPickSpineTrunkSegment(
            List<Point2d> ring,
            bool horizontal,
            double eps,
            out Segment best,
            out string errorMessage)
        {
            best = default;
            errorMessage = null;
            GetExtents(ring, out double minX, out double minY, out double maxX, out double maxY);

            double spine = horizontal ? 0.5 * (minY + maxY) : 0.5 * (minX + maxX);
            double ortho = horizontal ? (maxY - minY) : (maxX - minX);
            double step  = Math.Max(ortho * 0.02, eps * 50.0); // nudge step — 2% of zone depth
            int maxAttempts = 25;

            for (int attempt = 0; attempt <= maxAttempts; attempt++)
            {
                // Alternate nudges above/below spine so we find a line that actually cuts the zone.
                int sign = (attempt % 2 == 0) ? 1 : -1;
                int mag = (attempt + 1) / 2;
                double axis = spine + sign * mag * step;

                double bestLen = 0;
                Segment local = default;
                bool have = false;
                if (horizontal)
                {
                    foreach (var s in HorizontalSegmentsAtY(ring, axis, eps))
                    {
                        double len = Math.Abs(s.X1 - s.X0);
                        if (len > bestLen) { bestLen = len; local = new Segment(s.X0, axis, s.X1, axis); have = true; }
                    }
                }
                else
                {
                    foreach (var s in VerticalSegmentsAtX(ring, axis, eps))
                    {
                        double len = Math.Abs(s.Y1 - s.Y0);
                        if (len > bestLen) { bestLen = len; local = new Segment(axis, s.Y0, axis, s.Y1); have = true; }
                    }
                }
                if (have)
                {
                    best = local;
                    return true;
                }
            }

            errorMessage = "Spine trunk could not fit inside the zone near its median axis (irregular shape?).";
            return false;
        }

        private static bool TryPickBestTrunkSegment(
            List<Point2d> ring,
            List<Point2d> sprinklers,
            double spacingDu,
            double eps,
            bool horizontal,
            double lambda,
            out Segment best,
            out string errorMessage)
        {
            best = default;
            errorMessage = null;

            // Candidate scanlines from sprinkler rows/cols.
            var candidates = new List<double>();
            if (horizontal)
            {
                foreach (var p in sprinklers) candidates.Add(p.Y);
            }
            else
            {
                foreach (var p in sprinklers) candidates.Add(p.X);
            }
            candidates.Sort();
            candidates = DedupScalars(candidates, eps * 10);
            AgentLog.Write("TryPickBestTrunkSegment", "scanlines=" + candidates.Count + " sprinklers=" + sprinklers.Count);

            // Safety cap — downstream loop is O(scanlines * ring * sprinklers).
            // 2000 unique scanlines × 1000 sprinklers × 500 ring verts ≈ 10⁹ ops; bail instead of freezing.
            if (candidates.Count > 2000)
            {
                errorMessage = "Too many candidate trunk scanlines (" + candidates.Count + "). Zone likely has inconsistent units — check INSUNITS.";
                return false;
            }

            double bestCost = double.MaxValue;
            bool found = false;

            foreach (double c in candidates)
            {
                if (horizontal)
                {
                    var segs = HorizontalSegmentsAtY(ring, c, eps);
                    foreach (var s in segs)
                    {
                        var seg = new Segment(s.X0, c, s.X1, c);
                        double cost = CostToSegment(sprinklers, seg, horizontal) + lambda * Math.Abs(s.X1 - s.X0);
                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            best = seg;
                            found = true;
                        }
                    }
                }
                else
                {
                    var segs = VerticalSegmentsAtX(ring, c, eps);
                    foreach (var s in segs)
                    {
                        var seg = new Segment(c, s.Y0, c, s.Y1);
                        double cost = CostToSegment(sprinklers, seg, horizontal) + lambda * Math.Abs(s.Y1 - s.Y0);
                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            best = seg;
                            found = true;
                        }
                    }
                }
            }

            if (!found)
            {
                errorMessage = "Could not place a trunk inside the zone (no scanline intersections).";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Sprinkler-driven trunk: evaluates both horizontal AND vertical grid-aligned candidates
        /// (scanlines at sprinkler rows/columns) and picks the globally minimum-cost trunk where
        /// cost = Σ manhattan(sprinkler → trunk foot) + λ · trunk length.
        /// The winning orientation is returned in <paramref name="pickedHorizontal"/>.
        /// </summary>
        private static bool TryPickSprinklerDrivenTrunk(
            List<Point2d> ring,
            List<Point2d> sprinklers,
            double spacingDu,
            double eps,
            double lambda,
            bool requireCoverAll,
            out Segment best,
            out bool pickedHorizontal,
            out string errorMessage)
        {
            best = default;
            pickedHorizontal = true;
            errorMessage = null;

            bool hOk = requireCoverAll
                ? TryPickCoveringTrunkSegment(ring, sprinklers, spacingDu, eps, horizontal: true, lambda, out var hSeg, out string hErr)
                : TryPickBestTrunkSegment(ring, sprinklers, spacingDu, eps, horizontal: true, lambda, out hSeg, out hErr);
            bool vOk = requireCoverAll
                ? TryPickCoveringTrunkSegment(ring, sprinklers, spacingDu, eps, horizontal: false, lambda, out var vSeg, out string vErr)
                : TryPickBestTrunkSegment(ring, sprinklers, spacingDu, eps, horizontal: false, lambda, out vSeg, out vErr);

            if (!hOk && !vOk)
            {
                errorMessage = "Sprinkler-driven trunk: " + (hErr ?? vErr ?? "no valid scanline");
                return false;
            }

            double hCost = hOk ? CostToSegment(sprinklers, hSeg, true)  + lambda * Math.Abs(hSeg.X1 - hSeg.X0) : double.MaxValue;
            double vCost = vOk ? CostToSegment(sprinklers, vSeg, false) + lambda * Math.Abs(vSeg.Y1 - vSeg.Y0) : double.MaxValue;

            if (hCost <= vCost)
            {
                best = hSeg;
                pickedHorizontal = true;
            }
            else
            {
                best = vSeg;
                pickedHorizontal = false;
            }
            AgentLog.Write("TryPickSprinklerDrivenTrunk",
                "H=" + (hOk ? hCost.ToString("F1", CultureInfo.InvariantCulture) : "x") +
                " V=" + (vOk ? vCost.ToString("F1", CultureInfo.InvariantCulture) : "x") +
                " pick=" + (pickedHorizontal ? "H" : "V"));
            return true;
        }

        private static bool SegmentCoversAllSprinklers(List<Point2d> sprinklers, Segment seg, bool horizontal, double tol)
        {
            if (sprinklers == null || sprinklers.Count == 0)
                return false;

            if (horizontal)
            {
                double lo = Math.Min(seg.X0, seg.X1) - tol;
                double hi = Math.Max(seg.X0, seg.X1) + tol;
                for (int i = 0; i < sprinklers.Count; i++)
                {
                    double x = sprinklers[i].X;
                    if (x < lo || x > hi)
                        return false;
                }
                return true;
            }
            else
            {
                double lo = Math.Min(seg.Y0, seg.Y1) - tol;
                double hi = Math.Max(seg.Y0, seg.Y1) + tol;
                for (int i = 0; i < sprinklers.Count; i++)
                {
                    double y = sprinklers[i].Y;
                    if (y < lo || y > hi)
                        return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Straight trunk through the zone centroid (area-weighted), optionally nudged off a coincident sprinkler.
        /// Orientation follows minimum total perpendicular branch run + λ·trunk length unless <paramref name="orientationLocked"/>.
        /// </summary>
        private static bool TryPickMidpointBasedCoveringTrunk(
            List<Point2d> ring,
            List<Point2d> sprinklers,
            double spacingDu,
            double eps,
            double lambda,
            bool orientationLocked,
            bool preferHorizontal,
            out Segment best,
            out bool pickedHorizontal,
            out string errorMessage)
        {
            best = default;
            pickedHorizontal = true;
            errorMessage = null;
            if (ring == null || ring.Count < 3 || sprinklers == null || sprinklers.Count == 0)
            {
                errorMessage = "Invalid ring or sprinklers for midpoint trunk.";
                return false;
            }

            double clusterTol = Math.Max(eps * 20.0, spacingDu * 0.2);
            Point2d mid = PolygonUtils.ApproxCentroidAreaWeighted(ring);
            NudgeMidpointOffSprinklers(ref mid, sprinklers, spacingDu, eps);

            bool hOk = TryBestSpanThroughMid(
                ring, sprinklers, mid, horizontal: true, eps, clusterTol, lambda,
                requireMidOnSpan: true, out Segment hSeg, out double hCost);
            if (!hOk)
            {
                hOk = TryBestSpanThroughMid(
                    ring, sprinklers, mid, horizontal: true, eps, clusterTol, lambda,
                    requireMidOnSpan: false, out hSeg, out hCost);
            }

            bool vOk = TryBestSpanThroughMid(
                ring, sprinklers, mid, horizontal: false, eps, clusterTol, lambda,
                requireMidOnSpan: true, out Segment vSeg, out double vCost);
            if (!vOk)
            {
                vOk = TryBestSpanThroughMid(
                    ring, sprinklers, mid, horizontal: false, eps, clusterTol, lambda,
                    requireMidOnSpan: false, out vSeg, out vCost);
            }

            if (orientationLocked)
            {
                if (preferHorizontal)
                {
                    if (!hOk)
                    {
                        errorMessage =
                            "Midpoint-based trunk: preferred horizontal main cannot serve all sprinklers from the zone centroid line.";
                        return false;
                    }
                    best = hSeg;
                    pickedHorizontal = true;
                }
                else
                {
                    if (!vOk)
                    {
                        errorMessage =
                            "Midpoint-based trunk: preferred vertical main cannot serve all sprinklers from the zone centroid line.";
                        return false;
                    }
                    best = vSeg;
                    pickedHorizontal = false;
                }
            }
            else
            {
                if (!hOk && !vOk)
                {
                    errorMessage =
                        "Midpoint-based trunk: no horizontal or vertical line through the zone centroid serves all sprinklers orthogonally.";
                    return false;
                }

                if (!vOk || (hOk && hCost <= vCost))
                {
                    best = hSeg;
                    pickedHorizontal = true;
                }
                else
                {
                    best = vSeg;
                    pickedHorizontal = false;
                }
            }

            AgentLog.Write("TryPickMidpointBasedCoveringTrunk",
                "mid=(" + mid.X.ToString("F1", CultureInfo.InvariantCulture) + "," + mid.Y.ToString("F1", CultureInfo.InvariantCulture) + ") H=" +
                (hOk ? hCost.ToString("F1", CultureInfo.InvariantCulture) : "x") +
                " V=" + (vOk ? vCost.ToString("F1", CultureInfo.InvariantCulture) : "x") +
                " pick=" + (pickedHorizontal ? "H" : "V"));
            return true;
        }

        private static void NudgeMidpointOffSprinklers(ref Point2d mid, List<Point2d> sprinklers, double spacingDu, double eps)
        {
            double tol = Math.Max(eps * 30.0, spacingDu * 0.06);
            bool Coincident(Point2d q)
            {
                for (int i = 0; i < sprinklers.Count; i++)
                {
                    if (q.GetDistanceTo(sprinklers[i]) <= tol)
                        return true;
                }
                return false;
            }

            if (!Coincident(mid))
                return;

            double step = Math.Max(spacingDu * 0.15, eps * 80.0);
            var dirs = new[]
            {
                new Vector2d(1, 0), new Vector2d(-1, 0), new Vector2d(0, 1), new Vector2d(0, -1),
                new Vector2d(1, 1), new Vector2d(1, -1), new Vector2d(-1, 1), new Vector2d(-1, -1)
            };
            for (int m = 1; m <= 6; m++)
            {
                for (int d = 0; d < dirs.Length; d++)
                {
                    var dir = dirs[d];
                    double len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
                    if (len <= 1e-12)
                        continue;
                    var trial = new Point2d(mid.X + dir.X / len * step * m, mid.Y + dir.Y / len * step * m);
                    if (!Coincident(trial))
                    {
                        mid = trial;
                        return;
                    }
                }
            }
        }

        private static bool TryBestSpanThroughMid(
            List<Point2d> ring,
            List<Point2d> sprinklers,
            Point2d mid,
            bool horizontal,
            double eps,
            double clusterTol,
            double lambda,
            bool requireMidOnSpan,
            out Segment best,
            out double bestCost)
        {
            best = default;
            bestCost = double.MaxValue;
            bool found = false;

            if (horizontal)
            {
                foreach (var s in HorizontalSegmentsAtY(ring, mid.Y, eps))
                {
                    double x0 = s.X0, x1 = s.X1;
                    double lo = Math.Min(x0, x1), hi = Math.Max(x0, x1);
                    if (requireMidOnSpan && (mid.X < lo - eps * 5 || mid.X > hi + eps * 5))
                        continue;
                    var seg = new Segment(x0, mid.Y, x1, mid.Y);
                    if (!SegmentCoversAllSprinklers(sprinklers, seg, horizontal: true, clusterTol))
                        continue;
                    double perp = 0;
                    for (int i = 0; i < sprinklers.Count; i++)
                        perp += Math.Abs(sprinklers[i].Y - mid.Y);
                    double cost = perp + lambda * (hi - lo);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = seg;
                        found = true;
                    }
                }
            }
            else
            {
                foreach (var s in VerticalSegmentsAtX(ring, mid.X, eps))
                {
                    double y0 = s.Y0, y1 = s.Y1;
                    double lo = Math.Min(y0, y1), hi = Math.Max(y0, y1);
                    if (requireMidOnSpan && (mid.Y < lo - eps * 5 || mid.Y > hi + eps * 5))
                        continue;
                    var seg = new Segment(mid.X, y0, mid.X, y1);
                    if (!SegmentCoversAllSprinklers(sprinklers, seg, horizontal: false, clusterTol))
                        continue;
                    double perp = 0;
                    for (int i = 0; i < sprinklers.Count; i++)
                        perp += Math.Abs(sprinklers[i].X - mid.X);
                    double cost = perp + lambda * (hi - lo);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = seg;
                        found = true;
                    }
                }
            }

            return found;
        }

        private static bool TryPickCoveringTrunkSegment(
            List<Point2d> ring,
            List<Point2d> sprinklers,
            double spacingDu,
            double eps,
            bool horizontal,
            double lambda,
            out Segment best,
            out string errorMessage)
        {
            best = default;
            errorMessage = null;
            if (sprinklers == null || sprinklers.Count == 0)
            {
                errorMessage = "No sprinklers to route trunk against.";
                return false;
            }

            double clusterTol = Math.Max(eps * 20.0, spacingDu * 0.2);

            // Candidate scanlines from sprinkler rows/cols.
            var candidates = new List<double>();
            if (horizontal) { foreach (var p in sprinklers) candidates.Add(p.Y); }
            else { foreach (var p in sprinklers) candidates.Add(p.X); }
            candidates.Sort();
            candidates = DedupScalars(candidates, clusterTol);
            if (candidates.Count == 0)
            {
                errorMessage = "No candidate trunk scanlines.";
                return false;
            }

            double bestCost = double.MaxValue;
            bool found = false;

            foreach (double c in candidates)
            {
                if (horizontal)
                {
                    foreach (var s in HorizontalSegmentsAtY(ring, c, eps))
                    {
                        var seg = new Segment(s.X0, c, s.X1, c);
                        if (!SegmentCoversAllSprinklers(sprinklers, seg, horizontal: true, tol: clusterTol))
                            continue;

                        // Cost reduces to Σ perpendicular distance because every foot lies inside the segment span.
                        double perp = 0;
                        for (int i = 0; i < sprinklers.Count; i++)
                            perp += Math.Abs(sprinklers[i].Y - c);
                        double cost = perp + lambda * Math.Abs(s.X1 - s.X0);
                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            best = seg;
                            found = true;
                        }
                    }
                }
                else
                {
                    foreach (var s in VerticalSegmentsAtX(ring, c, eps))
                    {
                        var seg = new Segment(c, s.Y0, c, s.Y1);
                        if (!SegmentCoversAllSprinklers(sprinklers, seg, horizontal: false, tol: clusterTol))
                            continue;

                        double perp = 0;
                        for (int i = 0; i < sprinklers.Count; i++)
                            perp += Math.Abs(sprinklers[i].X - c);
                        double cost = perp + lambda * Math.Abs(s.Y1 - s.Y0);
                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            best = seg;
                            found = true;
                        }
                    }
                }
            }

            if (!found)
            {
                errorMessage =
                    "Could not find a straight trunk scanline inside the zone that spans all sprinklers. " +
                    "This zone may require multiple mains or a non-straight trunk to serve all heads orthogonally.";
                return false;
            }

            return true;
        }

        private readonly struct Span
        {
            public readonly double X0, X1;
            public Span(double x0, double x1) { X0 = x0; X1 = x1; }
        }

        private readonly struct SpanY
        {
            public readonly double Y0, Y1;
            public SpanY(double y0, double y1) { Y0 = y0; Y1 = y1; }
        }

        private static List<Span> HorizontalSegmentsAtY(List<Point2d> ring, double y, double eps)
        {
            var xs = new List<double>();
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                // Half-open rule to avoid double-count at vertices.
                bool crosses = (a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y);
                if (!crosses)
                    continue;
                double dy = b.Y - a.Y;
                if (Math.Abs(dy) <= eps)
                    continue;
                double t = (y - a.Y) / dy;
                double x = a.X + t * (b.X - a.X);
                xs.Add(x);
            }

            xs.Sort();
            xs = DedupScalars(xs, eps * 10);
            var segs = new List<Span>();
            for (int i = 0; i + 1 < xs.Count; i += 2)
            {
                double x0 = xs[i];
                double x1 = xs[i + 1];
                if (x1 - x0 > eps)
                    segs.Add(new Span(x0, x1));
            }
            return segs;
        }

        private static List<SpanY> VerticalSegmentsAtX(List<Point2d> ring, double x, double eps)
        {
            var ys = new List<double>();
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                bool crosses = (a.X <= x && b.X > x) || (b.X <= x && a.X > x);
                if (!crosses)
                    continue;
                double dx = b.X - a.X;
                if (Math.Abs(dx) <= eps)
                    continue;
                double t = (x - a.X) / dx;
                double y = a.Y + t * (b.Y - a.Y);
                ys.Add(y);
            }

            ys.Sort();
            ys = DedupScalars(ys, eps * 10);
            var segs = new List<SpanY>();
            for (int i = 0; i + 1 < ys.Count; i += 2)
            {
                double y0 = ys[i];
                double y1 = ys[i + 1];
                if (y1 - y0 > eps)
                    segs.Add(new SpanY(y0, y1));
            }
            return segs;
        }

        private static double CostToSegment(List<Point2d> sprinklers, Segment seg, bool horizontal)
        {
            double cost = 0;
            foreach (var p in sprinklers)
            {
                cost += ManhattanDistanceToSegment(p, seg, horizontal);
            }
            return cost;
        }

        private static double ManhattanDistanceToSegment(Point2d p, Segment seg, bool horizontal)
        {
            if (horizontal)
            {
                double y = seg.Y0;
                double xMin = Math.Min(seg.X0, seg.X1);
                double xMax = Math.Max(seg.X0, seg.X1);
                double dx = p.X < xMin ? xMin - p.X : (p.X > xMax ? p.X - xMax : 0.0);
                return Math.Abs(p.Y - y) + dx;
            }
            else
            {
                double x = seg.X0;
                double yMin = Math.Min(seg.Y0, seg.Y1);
                double yMax = Math.Max(seg.Y0, seg.Y1);
                double dy = p.Y < yMin ? yMin - p.Y : (p.Y > yMax ? p.Y - yMax : 0.0);
                return Math.Abs(p.X - x) + dy;
            }
        }

        /// <summary>
        /// Snap a point lying outside the ring to a point just inside it, biased along the line
        /// toward <paramref name="insideRef"/> (a point guaranteed inside). Tries the ray-entry
        /// point first; falls back to the closest ring point nudged toward <paramref name="insideRef"/>.
        /// </summary>
        private static bool TrySnapInsideRing(List<Point2d> ring, Point2d outside, Point2d insideRef, double eps, double minNudgeWorld, out Point2d snapped)
        {
            snapped = outside;

            // 1) Find the first ring crossing on the ray outside → insideRef and step past it.
            double sx = outside.X, sy = outside.Y;
            double vx = insideRef.X - sx, vy = insideRef.Y - sy;
            double vlen = Math.Sqrt(vx * vx + vy * vy);
            if (vlen > eps)
            {
                double bestT = double.PositiveInfinity;
                int n = ring.Count;
                for (int i = 0; i < n; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % n];
                    double ex = b.X - a.X, ey = b.Y - a.Y;
                    double denom = vx * ey - vy * ex;
                    if (Math.Abs(denom) < 1e-18) continue;
                    double t = ((a.X - sx) * ey - (a.Y - sy) * ex) / denom;
                    double u = ((a.X - sx) * vy - (a.Y - sy) * vx) / denom;
                    if (t < -eps || t > 1 + eps) continue;
                    if (u < -eps || u > 1 + eps) continue;
                    if (t < bestT) bestT = t;
                }
                if (bestT < double.PositiveInfinity)
                {
                    double nudgeWorld = Math.Max(Math.Max(eps * 20.0, vlen * 0.002), minNudgeWorld);
                    // Try escalating nudges if the first lands on or past the exit edge (thin sliver case).
                    for (int k = 0; k < 4; k++)
                    {
                        double tEntry = Math.Min(1.0, bestT + (nudgeWorld * (1 << k)) / vlen);
                        var cand = new Point2d(sx + tEntry * vx, sy + tEntry * vy);
                        if (PointInPolygon(ring, cand.X, cand.Y))
                        {
                            snapped = cand;
                            return true;
                        }
                    }
                }
            }

            // 2) Fallback: closest point on ring, nudged toward insideRef.
            var closest = ClosestPointOnRing(ring, outside);
            double dx = insideRef.X - closest.X, dy = insideRef.Y - closest.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= eps) return false;
            double nudge = Math.Max(Math.Max(eps * 20.0, len * 0.01), minNudgeWorld);
            for (int k = 0; k < 4; k++)
            {
                double step = nudge * (1 << k);
                var candidate = new Point2d(closest.X + dx / len * step, closest.Y + dy / len * step);
                if (PointInPolygon(ring, candidate.X, candidate.Y))
                {
                    snapped = candidate;
                    return true;
                }
            }
            return false;
        }

        private static Point2d ClosestPointOnRing(List<Point2d> ring, Point2d p)
        {
            Point2d best = ring[0];
            double bestD2 = double.MaxValue;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                var seg = new Segment(a.X, a.Y, b.X, b.Y);
                var q = ClosestPointOnSegmentL2(p, seg);
                double d2 = (q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y);
                if (d2 < bestD2) { bestD2 = d2; best = q; }
            }
            return best;
        }

        private static Point2d ClosestPointOnSegmentL2(Point2d p, Segment seg)
        {
            double x0 = seg.X0, y0 = seg.Y0, x1 = seg.X1, y1 = seg.Y1;
            double dx = x1 - x0;
            double dy = y1 - y0;
            double d2 = dx * dx + dy * dy;
            if (d2 <= 1e-18)
                return new Point2d(x0, y0);
            double t = ((p.X - x0) * dx + (p.Y - y0) * dy) / d2;
            t = Math.Max(0.0, Math.Min(1.0, t));
            return new Point2d(x0 + t * dx, y0 + t * dy);
        }

        private static bool TryOrthogonalConnectInside(List<Point2d> ring, Point2d a, Point2d b, double step, out List<Point2d> path)
        {
            path = null;
            var p1 = new List<Point2d> { a, new Point2d(b.X, a.Y), b };
            var p2 = new List<Point2d> { a, new Point2d(a.X, b.Y), b };
            bool ok1 = PathSegmentsInside(ring, p1, step);
            bool ok2 = PathSegmentsInside(ring, p2, step);
            if (ok1 && ok2)
            {
                path = OrthogonalLPathLength(p1) <= OrthogonalLPathLength(p2) ? p1 : p2;
                return true;
            }
            if (ok1)
            {
                path = p1;
                return true;
            }
            if (ok2)
            {
                path = p2;
                return true;
            }
            return false;
        }

        private static double OrthogonalLPathLength(List<Point2d> pts)
        {
            if (pts == null || pts.Count < 2)
                return 0;
            double sum = 0;
            for (int i = 0; i < pts.Count - 1; i++)
                sum += pts[i].GetDistanceTo(pts[i + 1]);
            return sum;
        }

        private static bool PathSegmentsInside(List<Point2d> ring, List<Point2d> path, double step)
        {
            if (path == null || path.Count < 2)
                return false;
            double s = Math.Max(step, 1e-6);
            for (int i = 0; i < path.Count - 1; i++)
            {
                var a = path[i];
                var b = path[i + 1];
                double len = a.GetDistanceTo(b);
                int n = (int)Math.Ceiling(len / s);
                if (n < 1) n = 1;
                if (n > 2000) n = 2000; // cap to prevent unbounded loops on tiny step values
                for (int k = 0; k <= n; k++)
                {
                    double t = n == 0 ? 0 : (double)k / n;
                    double x = a.X + (b.X - a.X) * t;
                    double y = a.Y + (b.Y - a.Y) * t;
                    if (!PointInPolygon(ring, x, y))
                        return false;
                }
            }
            return true;
        }

        private static bool TryAStarConnectInside(List<Point2d> ring, Point2d start, Point2d goal, double cell, double eps, out List<Point2d> path)
        {
            path = null;
            double c = Math.Max(cell, eps * 10);
            GetExtents(ring, out double minX, out double minY, out double maxX, out double maxY);

            // Pad bbox slightly.
            minX -= c; minY -= c; maxX += c; maxY += c;

            int cols = (int)Math.Ceiling((maxX - minX) / c);
            int rows = (int)Math.Ceiling((maxY - minY) / c);
            if (cols < 5) cols = 5;
            if (rows < 5) rows = 5;
            if ((long)cols * rows > 2_000_000)
            {
                // Avoid huge grids.
                return false;
            }

            (int sx, int sy) = ToCell(start, minX, minY, c);
            (int gx, int gy) = ToCell(goal, minX, minY, c);

            sx = ClampInt(sx, 0, cols - 1);
            sy = ClampInt(sy, 0, rows - 1);
            gx = ClampInt(gx, 0, cols - 1);
            gy = ClampInt(gy, 0, rows - 1);

            bool Inside(int ix, int iy)
            {
                double x = minX + (ix + 0.5) * c;
                double y = minY + (iy + 0.5) * c;
                return PointInPolygon(ring, x, y);
            }

            if (!Inside(sx, sy) || !Inside(gx, gy))
                return false;

            int[,] came = new int[cols, rows];
            for (int i = 0; i < cols; i++)
                for (int j = 0; j < rows; j++)
                    came[i, j] = -1;

            double[,] gScore = new double[cols, rows];
            for (int i = 0; i < cols; i++)
                for (int j = 0; j < rows; j++)
                    gScore[i, j] = double.PositiveInfinity;

            var open = new MinHeap();
            int startLin = GridToLinear(sx, sy, cols);
            int goalLin = GridToLinear(gx, gy, cols);
            gScore[sx, sy] = 0;
            open.Push(startLin, Heuristic(sx, sy, gx, gy));

            int[] dxs = { 1, -1, 0, 0 };
            int[] dys = { 0, 0, 1, -1 };

            int astarIterations = 0;
            const int MaxAstarIterations = 500_000;
            while (open.Count > 0)
            {
                if (++astarIterations > MaxAstarIterations)
                    return false; // grid too complex, fall back to straight connector

                int curLin = open.PopMinKey();
                GridFromLinear(curLin, cols, out int cx, out int cy);
                if (curLin == goalLin)
                    break;

                double gCur = gScore[cx, cy];
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + dxs[d];
                    int ny = cy + dys[d];
                    if (nx < 0 || ny < 0 || nx >= cols || ny >= rows) continue;
                    if (!Inside(nx, ny)) continue;

                    double tentative = gCur + 1.0;
                    if (tentative < gScore[nx, ny])
                    {
                        gScore[nx, ny] = tentative;
                        came[nx, ny] = curLin;
                        open.Push(GridToLinear(nx, ny, cols), tentative + Heuristic(nx, ny, gx, gy));
                    }
                }
            }

            if (came[gx, gy] < 0)
                return false;

            var cells = new List<int>();
            int kcur = goalLin;
            while (kcur != startLin)
            {
                cells.Add(kcur);
                GridFromLinear(kcur, cols, out int x, out int y);
                kcur = came[x, y];
                if (kcur < 0) return false;
            }
            cells.Add(startLin);
            cells.Reverse();

            var pts = new List<Point2d>(cells.Count);
            foreach (var ck in cells)
            {
                GridFromLinear(ck, cols, out int ix, out int iy);
                pts.Add(new Point2d(minX + (ix + 0.5) * c, minY + (iy + 0.5) * c));
            }

            // Force exact endpoints.
            if (pts.Count > 0) pts[0] = start;
            if (pts.Count > 1) pts[pts.Count - 1] = goal;

            path = pts;
            return true;
        }

        private static (int ix, int iy) ToCell(Point2d p, double minX, double minY, double cell)
        {
            int ix = (int)Math.Floor((p.X - minX) / cell);
            int iy = (int)Math.Floor((p.Y - minY) / cell);
            return (ix, iy);
        }

        private static int ClampInt(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private static double Heuristic(int x, int y, int gx, int gy) => Math.Abs(gx - x) + Math.Abs(gy - y);

        /// <summary>Row-major linear index (iy * cols + ix); safe when cols×rows ≤ 2M and ix, iy &lt; cols, rows.</summary>
        private static int GridToLinear(int ix, int iy, int cols) => iy * cols + ix;

        private static void GridFromLinear(int idx, int cols, out int ix, out int iy)
        {
            iy = idx / cols;
            ix = idx - iy * cols;
        }

        private sealed class MinHeap
        {
            private readonly List<(int Key, double Pri)> _a = new List<(int, double)>();
            public int Count => _a.Count;

            public void Push(int key, double pri)
            {
                _a.Add((key, pri));
                SiftUp(_a.Count - 1);
            }

            public int PopMinKey()
            {
                var root = _a[0].Key;
                var last = _a[_a.Count - 1];
                _a.RemoveAt(_a.Count - 1);
                if (_a.Count > 0)
                {
                    _a[0] = last;
                    SiftDown(0);
                }
                return root;
            }

            private void SiftUp(int i)
            {
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (_a[p].Pri <= _a[i].Pri) break;
                    (_a[p], _a[i]) = (_a[i], _a[p]);
                    i = p;
                }
            }

            private void SiftDown(int i)
            {
                int n = _a.Count;
                while (true)
                {
                    int l = i * 2 + 1;
                    int r = l + 1;
                    int best = i;
                    if (l < n && _a[l].Pri < _a[best].Pri) best = l;
                    if (r < n && _a[r].Pri < _a[best].Pri) best = r;
                    if (best == i) break;
                    (_a[best], _a[i]) = (_a[i], _a[best]);
                    i = best;
                }
            }
        }

        private static List<double> DedupScalars(List<double> vals, double tol)
        {
            var outV = new List<double>();
            foreach (var v in vals)
            {
                if (outV.Count == 0 || Math.Abs(outV[outV.Count - 1] - v) > tol)
                    outV.Add(v);
            }
            return outV;
        }

        private static List<Point2d> SimplifyCollinear(List<Point2d> pts, double eps)
        {
            if (pts == null || pts.Count < 3)
                return pts ?? new List<Point2d>();

            var outPts = new List<Point2d>();
            outPts.Add(pts[0]);
            for (int i = 1; i < pts.Count - 1; i++)
            {
                var a = outPts[outPts.Count - 1];
                var b = pts[i];
                var c = pts[i + 1];
                var ab = b - a;
                var bc = c - b;
                double cross = ab.X * bc.Y - ab.Y * bc.X;
                if (Math.Abs(cross) <= eps * eps * 10)
                    continue;
                outPts.Add(b);
            }
            outPts.Add(pts[pts.Count - 1]);
            return outPts;
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
    }
}

