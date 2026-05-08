using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.AreaWorkflow;
using autocad_final.Commands;
using autocad_final.Geometry;
using PolygonMetrics = autocad_final.AreaWorkflow.PolygonMetrics;

namespace autocad_final.Agent
{
    /// <summary>
    /// Agent write tools — each wraps an existing engine via AcadLockGuard patterns.
    /// All write tools:
    ///   1. Check the destructive guard (IsLocked / HasManualEdits in ProjectMemory).
    ///   2. Resolve the boundary handle to a Polyline clone.
    ///   3. Call the existing engine (RouteMainPipeCommand, ApplySprinklersCommand, etc.).
    ///   4. Record the decision to ProjectMemory and save.
    ///   5. Return structured JSON with status, summary, and next_step hint.
    /// </summary>
    public static class AgentWriteTools
    {
        /// <summary>
        /// Raised after a successful database write so <see cref="ToolDispatcher"/> can refresh
        /// <see cref="AgentReadTools.BuildSnapshot"/> and keep zone lists current.
        /// </summary>
        public static event Action<Document> AfterSuccessfulWrite;

        private static void NotifyWriteCommitted(Document doc)
        {
            DrawingMutationHelper.AfterSuccessfulWrite(doc);
            try { AfterSuccessfulWrite?.Invoke(doc); } catch { /* never block */ }
        }

        public sealed class RouteMainPipeOptions
        {
            public double? SpacingM { get; set; }
            public double? CoverageRadiusM { get; set; }
            /// <summary>"auto" (default), "horizontal", or "vertical".</summary>
            public string PreferredOrientation { get; set; }
            /// <summary>
            /// Trunk selection strategy:
            /// "sprinkler_driven" — evaluates both H and V scanlines, picks the trunk that minimises
            /// Σ(sprinkler→trunk manhattan) + λ·trunk_length (recommended for rectangular zones with regular grids);
            /// "skeleton" — medial-axis skeleton + longest-path (best for irregular / concave zones);
            /// "grid_aligned" — single-orientation scanline from the sprinkler grid;
            /// "spine" — longest inside segment along the zone's median axis, ignores head spacing;
            /// "shortest_path" — single-orientation min-cost scanline (legacy).
            /// </summary>
            public string Strategy { get; set; }

            /// <summary>Skeleton-only: grid cell size (meters). Default ≈ spacing/4. Smaller = finer skeleton, more cells.</summary>
            public double? SkeletonCellSizeM { get; set; }

            /// <summary>Skeleton-only: minimum clearance from boundary (meters) for skeleton cells. Default ≈ coverage_radius/2.</summary>
            public double? SkeletonMinClearanceM { get; set; }

            /// <summary>Skeleton-only: prune skeleton branches shorter than this (meters). Default ≈ one spacing.</summary>
            public double? SkeletonPruneBranchLengthM { get; set; }

            /// <summary>
            /// Sprinkler-driven / shortest_path: λ in cost = Σ(sprinkler→trunk) + λ·trunk_length. Default 0.1.
            /// Raise to shorten the trunk at the cost of longer branches; lower (→0) to let the trunk stretch
            /// until every sprinkler has the shortest possible drop.
            /// </summary>
            public double? MainPipeLengthPenalty { get; set; }
        }

        public sealed class PlaceSprinklersOptions
        {
            public double? SpacingM { get; set; }
            public double? CoverageRadiusM { get; set; }
            public double? MaxBoundaryGapM { get; set; }
            public bool? TrunkAnchored { get; set; }
            /// <summary>Shifts the grid origin along X in meters. Use to slide the grid to cover missed corners.</summary>
            public double? GridAnchorOffsetXM { get; set; }
            /// <summary>Shifts the grid origin along Y in meters. Use to slide the grid to cover missed corners.</summary>
            public double? GridAnchorOffsetYM { get; set; }
        }

        public sealed class DesignZoneOptions
        {
            // Shared by route + place
            public double? SpacingM { get; set; }
            public double? CoverageRadiusM { get; set; }
            // Route phase
            public string PreferredOrientation { get; set; }
            /// <summary>Main pipe routing strategy: "skeleton", "grid_aligned", "spine", "shortest_path".</summary>
            public string Strategy { get; set; }
            public double? SkeletonCellSizeM { get; set; }
            public double? SkeletonMinClearanceM { get; set; }
            public double? SkeletonPruneBranchLengthM { get; set; }
            public double? MainPipeLengthPenalty { get; set; }
            // Place phase
            public double? MaxBoundaryGapM { get; set; }
            public bool? TrunkAnchored { get; set; }
            public double? GridAnchorOffsetXM { get; set; }
            public double? GridAnchorOffsetYM { get; set; }
            /// <summary>When true the interceptor returns projected stats without committing.</summary>
            public bool? Preview { get; set; }
        }

        // ── cleanup_zone ──────────────────────────────────────────────────────────

        /// <summary>
        /// Erases all automated sprinkler content tagged to this zone boundary.
        /// Always requires the caller to pass override_manual_edits=true when the zone
        /// has manual edits, because cleanup is the most destructive operation.
        /// </summary>
        public static string CleanupZone(Document doc, string boundaryHandleHex, bool overrideManualEdits, ProjectMemory memory)
        {
            if (doc == null) return Err("No document.");
            if (string.IsNullOrWhiteSpace(boundaryHandleHex)) return Err("boundary_handle is required.");

            var guard = CheckDestructiveGuard(memory, boundaryHandleHex, overrideManualEdits);
            if (guard != null) return guard;

            var db = doc.Database;
            if (!TryResolveBoundary(db, boundaryHandleHex, out Polyline zone, out ObjectId boundaryId))
                return Err("Could not resolve boundary handle '" + boundaryHandleHex + "'. Use get_all_closed_polylines to find valid zone boundaries.");

            try
            {
                var ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zone);
                if (ring == null || ring.Count < 3)
                    return Err("Zone boundary is invalid (fewer than 3 vertices).");

                int erased = 0;
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    SprinklerXData.EnsureRegApp(tr, db);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    erased = SprinklerZoneAutomationCleanup.ClearPriorAutomatedContent(tr, ms, ring, boundaryHandleHex, boundaryId);
                    tr.Commit();
                }

                RecordDecision(doc, memory, "cleanup_zone", boundaryHandleHex, null, "Erased " + erased + " entities.");
                NotifyWriteCommitted(doc);

                return JsonSupport.Serialize(new
                {
                    status         = "ok",
                    boundary_handle = boundaryHandleHex,
                    entities_erased = erased,
                    next_step      = "Zone cleared. Call route_main_pipe next to begin the design pipeline."
                });
            }
            catch (Exception ex)
            {
                return Err("cleanup_zone failed: " + ex.Message);
            }
            finally
            {
                try { zone.Dispose(); } catch { /* ignore */ }
            }
        }

        // ── route_main_pipe ───────────────────────────────────────────────────────

        /// <summary>
        /// Detects the nearest shaft, computes trunk + connector path, and draws tagged main
        /// pipe entities for the zone. Requires sprinkler layers and a valid closed boundary.
        /// </summary>
        public static string RouteMainPipe(Document doc, string boundaryHandleHex, bool overrideManualEdits, ProjectMemory memory)
            => RouteMainPipe(doc, boundaryHandleHex, overrideManualEdits, memory, null);

        public static string RouteMainPipe(Document doc, string boundaryHandleHex, bool overrideManualEdits, ProjectMemory memory, RouteMainPipeOptions options)
        {
            if (doc == null) return Err("No document.");
            if (string.IsNullOrWhiteSpace(boundaryHandleHex)) return Err("boundary_handle is required.");

            var guard = CheckDestructiveGuard(memory, boundaryHandleHex, overrideManualEdits);
            if (guard != null) return guard;

            var db = doc.Database;
            AgentLog.Write("RouteMainPipe", "resolving handle '" + boundaryHandleHex + "'");
            if (!TryResolveBoundary(db, boundaryHandleHex, out Polyline zone, out ObjectId _))
                return Err("Could not resolve boundary handle '" + boundaryHandleHex + "'.");

            AgentLog.Write("RouteMainPipe", "boundary resolved, starting TryRouteMainPipeForZone");
            try
            {
                var cfgRm            = RuntimeSettings.Load();
                double spacingM      = options?.SpacingM ?? memory?.DefaultSpacingM ?? cfgRm.SprinklerSpacingM;
                double radiusM       = options?.CoverageRadiusM ?? 1.5;
                string orientation   = options?.PreferredOrientation;
                string strategy      = options?.Strategy;
                AgentLog.Write("RouteMainPipe", "spacingM=" + spacingM + " radiusM=" + radiusM + " orientation=" + orientation + " strategy=" + (strategy ?? "grid_aligned"));
                if (!RouteMainPipeCommand.TryRouteMainPipeForZone(
                        doc, zone, boundaryHandleHex,
                        out string routeErr, out string routeSummary,
                        sprinklerSpacingMeters: spacingM,
                        sprinklerCoverageRadiusMeters: radiusM,
                        preferredOrientation: orientation,
                        strategy: strategy,
                        skeletonCellSizeMeters: options?.SkeletonCellSizeM,
                        skeletonMinClearanceMeters: options?.SkeletonMinClearanceM,
                        skeletonPruneBranchLengthMeters: options?.SkeletonPruneBranchLengthM,
                        mainPipeLengthPenalty: options?.MainPipeLengthPenalty))
                {
                    AgentLog.Write("RouteMainPipe", "TryRouteMainPipeForZone failed: " + (routeErr ?? "Unknown error."));
                    return Err("route_main_pipe failed: " + (routeErr ?? "Unknown error."));
                }
                AgentLog.Write("RouteMainPipe", "TryRouteMainPipeForZone succeeded: " + routeSummary);

                var p = options == null ? null : JsonSupport.Serialize(new { spacing_m = options.SpacingM, coverage_radius_m = options.CoverageRadiusM, preferred_orientation = options.PreferredOrientation, strategy = options.Strategy });
                RecordDecision(doc, memory, "route_main_pipe", boundaryHandleHex, p, routeSummary);
                NotifyWriteCommitted(doc);

                return JsonSupport.Serialize(new
                {
                    status          = "ok",
                    boundary_handle  = boundaryHandleHex,
                    summary          = routeSummary,
                    next_step        = "Main pipe routed. Call place_sprinklers next."
                });
            }
            catch (Exception ex)
            {
                return Err("route_main_pipe failed: " + ex.Message);
            }
            finally
            {
                try { zone.Dispose(); } catch { /* ignore */ }
            }
        }

        // ── place_sprinklers ──────────────────────────────────────────────────────

        /// <summary>
        /// Computes a trunk-anchored sprinkler grid and inserts pendent sprinkler block
        /// references for the zone. Clears prior sprinklers for this zone first.
        /// Requires main pipe to already be routed in the zone.
        /// </summary>
        public static string PlaceSprinklers(Document doc, string boundaryHandleHex, bool overrideManualEdits, ProjectMemory memory)
            => PlaceSprinklers(doc, boundaryHandleHex, overrideManualEdits, memory, null);

        public static string PlaceSprinklers(Document doc, string boundaryHandleHex, bool overrideManualEdits, ProjectMemory memory, PlaceSprinklersOptions options)
        {
            if (doc == null) return Err("No document.");
            if (string.IsNullOrWhiteSpace(boundaryHandleHex)) return Err("boundary_handle is required.");

            var guard = CheckDestructiveGuard(memory, boundaryHandleHex, overrideManualEdits);
            if (guard != null) return guard;

            var db = doc.Database;
            if (!TryResolveBoundary(db, boundaryHandleHex, out Polyline zone, out ObjectId _))
                return Err("Could not resolve boundary handle '" + boundaryHandleHex + "'.");

            try
            {
                var cfgPs            = RuntimeSettings.Load();
                double spacingM    = options?.SpacingM ?? memory?.DefaultSpacingM ?? cfgPs.SprinklerSpacingM;
                double radiusM     = options?.CoverageRadiusM ?? 1.5;
                double gapM        = options?.MaxBoundaryGapM ?? cfgPs.SprinklerToBoundaryDistanceM;
                bool trunkAnchored = options?.TrunkAnchored ?? true;
                double offsetXM    = options?.GridAnchorOffsetXM ?? 0;
                double offsetYM    = options?.GridAnchorOffsetYM ?? 0;
                if (!ApplySprinklersCommand.TryApplySprinklersForZone(
                        doc, zone, boundaryHandleHex,
                        out string placeMsg,
                        useTrunkAnchoredGrid: trunkAnchored,
                        spacingMeters: spacingM,
                        coverageRadiusMeters: radiusM,
                        maxBoundarySprinklerGapMeters: gapM,
                        gridAnchorOffsetXMeters: offsetXM,
                        gridAnchorOffsetYMeters: offsetYM))
                    return Err("place_sprinklers failed: " + (placeMsg ?? "Unknown error."));

                var p = options == null ? null : JsonSupport.Serialize(new
                {
                    spacing_m = options.SpacingM,
                    coverage_radius_m = options.CoverageRadiusM,
                    max_boundary_gap_m = options.MaxBoundaryGapM,
                    trunk_anchored = options.TrunkAnchored,
                    grid_anchor_offset_x_m = options.GridAnchorOffsetXM,
                    grid_anchor_offset_y_m = options.GridAnchorOffsetYM
                });
                RecordDecision(doc, memory, "place_sprinklers", boundaryHandleHex, p, placeMsg);
                NotifyWriteCommitted(doc);

                return JsonSupport.Serialize(new
                {
                    status          = "ok",
                    boundary_handle  = boundaryHandleHex,
                    summary          = placeMsg,
                    next_step        = "Sprinklers placed. Call attach_branches to connect branch piping."
                });
            }
            catch (Exception ex)
            {
                return Err("place_sprinklers failed: " + ex.Message);
            }
            finally
            {
                try { zone.Dispose(); } catch { /* ignore */ }
            }
        }

        // ── attach_branches ───────────────────────────────────────────────────────

        /// <summary>
        /// Reads the trunk axis and sprinkler positions inside the zone, then draws
        /// branch pipe segments connecting each sprinkler head to the main trunk.
        /// Requires main pipe and sprinklers to already be placed.
        /// </summary>
        public static string AttachBranches(Document doc, string boundaryHandleHex, bool overrideManualEdits, ProjectMemory memory)
        {
            if (doc == null) return Err("No document.");
            if (string.IsNullOrWhiteSpace(boundaryHandleHex)) return Err("boundary_handle is required.");

            var guard = CheckDestructiveGuard(memory, boundaryHandleHex, overrideManualEdits);
            if (guard != null) return guard;

            var db = doc.Database;
            if (!TryResolveBoundary(db, boundaryHandleHex, out Polyline zone, out ObjectId _))
                return Err("Could not resolve boundary handle '" + boundaryHandleHex + "'.");

            try
            {
                var ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zone);
                if (ring == null || ring.Count < 3)
                    return Err("Zone boundary is invalid.");

                if (!AttachBranchesCommand.TryAttachBranchesForZone(
                        doc, db, zone, ring, boundaryHandleHex, out string branchErr))
                    return Err("attach_branches failed: " + (branchErr ?? "Unknown error."));
                if (!AttachBranchesCommand.TryPlaceReducersForZone(
                        doc, db, zone, ring, boundaryHandleHex, routeBranchPipesFromConnectorFirst: false, ObjectId.Null, out string redErr))
                    return Err("place_reducers failed: " + (redErr ?? "Unknown error."));

                RecordDecision(doc, memory, "attach_branches", boundaryHandleHex, null, "Branch piping attached.");
                NotifyWriteCommitted(doc);

                return JsonSupport.Serialize(new
                {
                    status          = "ok",
                    boundary_handle  = boundaryHandleHex,
                    next_step        = "Zone design complete. Call validate_coverage to verify NFPA compliance, then move to the next zone."
                });
            }
            catch (Exception ex)
            {
                return Err("attach_branches failed: " + ex.Message);
            }
            finally
            {
                try { zone.Dispose(); } catch { /* ignore */ }
            }
        }

        // ── create_sprinkler_zones ─────────────────────────────────────────────────

        /// <summary>
        /// Splits a floor/room closed polyline into shaft-driven zones: grid Voronoi per shaft with Lloyd relaxation
        /// (near-equal areas), then equal-area strips, shaft midline strips, plain grid, then grid+cap.
        /// </summary>
        public static string CreateSprinklerZones(Document doc, string floorBoundaryHandleHex, ProjectMemory memory)
        {
            if (doc == null) return Err("No document.");
            if (string.IsNullOrWhiteSpace(floorBoundaryHandleHex))
                return Err("floor_boundary_handle is required — pass a closed polyline handle, or \"auto\" to detect from the drawing.");

            var db = doc.Database;
            string resolvedFloorHandle = floorBoundaryHandleHex.Trim();
            string autoDetectionNote = null;
            if (string.Equals(resolvedFloorHandle, "auto", StringComparison.OrdinalIgnoreCase))
            {
                var sites2d = FindShaftsInsideBoundary.CollectGlobalShaftSitePoints2d(db);
                if (sites2d.Count < 2)
                    return Err(
                        "auto: need at least two shaft block inserts (name/layer contains \"shaft\") or set_shaft_hint points " +
                        "before auto-detecting a floor boundary (found " + sites2d.Count + ").");

                if (!FloorBoundaryAutoDiscovery.TryAutoResolveFloorBoundary(
                        doc, sites2d, cloneToWorkLayer: true, out resolvedFloorHandle, out autoDetectionNote))
                    return Err("auto: " + (autoDetectionNote ?? "Could not find a suitable closed polyline."));
            }

            if (!TryResolveBoundary(db, resolvedFloorHandle, out Polyline floor, out ObjectId floorEntityId))
                return Err("Could not resolve floor_boundary_handle to a closed polyline.");

            var shaftPts = FindShaftsInsideBoundary.GetShaftPositionsInsideBoundary(db, floor);
            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            var sites = ShaftVoronoiZonesOnFloorPolyline.DedupeShaftSites(shaftPts, tol);
            int n = sites.Count;

            var fallbacksAttempted = new List<string>();
            int priorZonesErased = 0;

            try
            {
                if (n < 2)
                {
                    return JsonSupport.Serialize(new
                    {
                        status = "error",
                        message = "Need at least two distinct shaft sites inside the floor boundary for automatic zoning (found " +
                                  n.ToString(CultureInfo.InvariantCulture) + ").",
                        shaft_sites = n,
                        next_step = "Place at least two \"shaft\" block inserts inside the floor polyline, then retry create_sprinkler_zones.",
                        fallbacks_attempted = fallbacksAttempted
                    });
                }

                PolygonMetrics metrics;
                string modeUsed;
                List<string> createdHandles;

                using (doc.LockDocument())
                {
                    if (!SprinklerFloorZoningCascade.TryRun(
                            doc, floor, floorEntityId, echoMessages: false,
                            out metrics, out modeUsed, out createdHandles, out fallbacksAttempted, out priorZonesErased))
                    {
                        return JsonSupport.Serialize(new
                        {
                            status = "error",
                            message = "Automatic zoning could not produce zone outlines for this floor boundary.",
                            floor_boundary_handle = resolvedFloorHandle,
                            fallbacks_attempted = fallbacksAttempted,
                            next_step = "Try a simpler floor polygon, verify shafts inside the boundary, or zone manually."
                        });
                    }
                }

                var zonesJson = new List<object>();
                for (int i = 0; i < metrics.ZoneTable.Count; i++)
                {
                    var row = metrics.ZoneTable[i];
                    string bh = i < createdHandles.Count ? createdHandles[i] : null;
                    zonesJson.Add(new
                    {
                        name = row.Name,
                        area_m2 = row.AreaM2,
                        boundary_handle = bh
                    });
                }

                var ringsPerShaft = new int[n];
                foreach (var row in metrics.ZoneTable)
                {
                    if (row.ZoneOwnerIndex.HasValue && row.ZoneOwnerIndex.Value >= 0 && row.ZoneOwnerIndex.Value < n)
                        ringsPerShaft[row.ZoneOwnerIndex.Value]++;
                }

                bool hasSplitShaftZones = ringsPerShaft.Any(c => c > 1);
                int shaftAssignedRings = metrics.ZoneTable.Count(z => (z.ZoneOwnerIndex ?? -1) >= 0);
                int uncoveredRings = metrics.ZoneTable.Count(z => z.ZoneOwnerIndex.HasValue && z.ZoneOwnerIndex.Value < 0);
                bool zonesMatchShafts = !hasSplitShaftZones && shaftAssignedRings == n && uncoveredRings == 0;

                RecordDecision(doc, memory, "create_sprinkler_zones", resolvedFloorHandle, modeUsed, metrics.ZoningSummary);
                NotifyWriteCommitted(doc);

                return JsonSupport.Serialize(new
                {
                    status = "ok",
                    floor_boundary_handle = resolvedFloorHandle,
                    floor_boundary_auto_detection = autoDetectionNote,
                    mode_used = modeUsed,
                    zoning_summary = metrics.ZoningSummary,
                    shaft_site_count = n,
                    zone_outline_count = metrics.ZoneTable.Count,
                    shaft_assigned_zone_outlines = shaftAssignedRings,
                    uncovered_zone_outlines = uncoveredRings,
                    has_split_shaft_zones = hasSplitShaftZones,
                    zones_match_shafts = zonesMatchShafts,
                    zones_created = zonesJson,
                    prior_zone_entities_erased = priorZonesErased,
                    fallbacks_attempted = fallbacksAttempted,
                    next_step = "Call list_zones to refresh. Run design_zone with each new zone boundary_handle." +
                        (hasSplitShaftZones
                            ? " WARNING: at least one shaft produced more than one zone outline — labels may show 'Zone N (2)'. Prefer re-running zoning after grid fixes or report to developer."
                            : string.Empty)
                });
            }
            catch (Exception ex)
            {
                return Err("create_sprinkler_zones failed: " + ex.Message);
            }
            finally
            {
                try { floor.Dispose(); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Subdivides the outer floor parcel using agent-supplied straight cut segments (WCS).
        /// Requires exactly N−1 cuts for N shaft sites; each resulting piece must contain one shaft.
        /// </summary>
        public static string CreateZonesByCuts(Document doc, string argumentsJson, ProjectMemory memory)
        {
            if (doc == null) return Err("No document.");
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return Err("Tool arguments JSON is required.");

            CreateZonesByCutsToolArgs parsed;
            try
            {
                parsed = JsonSupport.DeserializeDataContract<CreateZonesByCutsToolArgs>(argumentsJson);
            }
            catch (Exception ex)
            {
                return Err("create_zones_by_cuts: invalid JSON — " + ex.Message);
            }

            if (parsed == null || string.IsNullOrWhiteSpace(parsed.FloorBoundaryHandle))
                return Err("floor_boundary_handle is required.");

            var db = doc.Database;
            string resolvedFloorHandle = parsed.FloorBoundaryHandle.Trim();
            string autoDetectionNote = null;
            if (string.Equals(resolvedFloorHandle, "auto", StringComparison.OrdinalIgnoreCase))
            {
                var sites2d = FindShaftsInsideBoundary.CollectGlobalShaftSitePoints2d(db);
                if (sites2d.Count < 2)
                    return Err(
                        "auto: need at least two shaft block inserts (name/layer contains \"shaft\") or set_shaft_hint points " +
                        "before auto-detecting a floor boundary (found " + sites2d.Count + ").");

                if (!FloorBoundaryAutoDiscovery.TryAutoResolveFloorBoundary(
                        doc, sites2d, cloneToWorkLayer: true, out resolvedFloorHandle, out autoDetectionNote))
                    return Err("auto: " + (autoDetectionNote ?? "Could not find a suitable closed polyline."));
            }

            if (!TryResolveBoundary(db, resolvedFloorHandle, out Polyline floor, out ObjectId floorEntityId))
                return Err("Could not resolve floor_boundary_handle to a closed polyline.");

            var shaftPts = FindShaftsInsideBoundary.GetShaftPositionsInsideBoundary(db, floor);
            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            var sites = ShaftVoronoiZonesOnFloorPolyline.DedupeShaftSites(shaftPts, tol);
            int n = sites.Count;

            if (n < 2)
            {
                try { floor.Dispose(); } catch { /* ignore */ }
                return JsonSupport.Serialize(new
                {
                    status = "error",
                    message = "Need at least two distinct shaft sites inside the floor boundary for cut zoning (found " +
                              n.ToString(CultureInfo.InvariantCulture) + ").",
                    shaft_sites = n
                });
            }

            if (parsed.Cuts == null || parsed.Cuts.Length != n - 1)
            {
                try { floor.Dispose(); } catch { /* ignore */ }
                return Err(
                    "cuts must be an array of length " + (n - 1).ToString(CultureInfo.InvariantCulture) +
                    " (N shaft sites ⇒ N−1 cut segments). Got " +
                    (parsed.Cuts == null ? "null" : parsed.Cuts.Length.ToString(CultureInfo.InvariantCulture)) + ".");
            }

            var cuts = new List<LlmCutZoning2d.Cut>(parsed.Cuts.Length);
            foreach (var c in parsed.Cuts)
                cuts.Add(new LlmCutZoning2d.Cut(new Point2d(c.x1, c.y1), new Point2d(c.x2, c.y2)));

            int priorZonesErased = 0;

            try
            {
                PolygonMetrics metrics;
                var createdHandles = new List<string>();
                const string modeUsed = "llm_straight_cuts";

                using (doc.LockDocument())
                {
                    var floorRing = new List<Point2d>();
                    try
                    {
                        int fv = floor.NumberOfVertices;
                        for (int k = 0; k < fv; k++)
                        {
                            var v = floor.GetPoint3dAt(k);
                            floorRing.Add(new Point2d(v.X, v.Y));
                        }
                    }
                    catch { floorRing = null; }

                    if (floorRing != null && floorRing.Count >= 3)
                    {
                        using (var tr = db.TransactionManager.StartTransaction())
                        {
                            SprinklerXData.EnsureRegApp(tr, db);
                            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                            priorZonesErased = SprinklerZoneAutomationCleanup.ClearPriorZoneOutlinesInsideFloor(
                                tr, ms, floorRing, floorEntityId);
                            tr.Commit();
                        }
                    }

                    ZoneAreaCommand.TryRunWithLlmCuts(
                        doc, floor, echoMessages: false, cuts, createdHandles, out metrics);

                    if (!ZoneAreaCommand.OutlinesWereDrawn(metrics))
                    {
                        return JsonSupport.Serialize(new
                        {
                            status = "error",
                            message = metrics?.ZoningSummary ?? "LLM cut zoning produced no zone outlines.",
                            floor_boundary_handle = resolvedFloorHandle,
                            floor_boundary_auto_detection = autoDetectionNote,
                            prior_zone_entities_erased = priorZonesErased
                        });
                    }

                    var zonesJson = new List<object>();
                    for (int i = 0; i < metrics.ZoneTable.Count; i++)
                    {
                        var row = metrics.ZoneTable[i];
                        string bh = i < createdHandles.Count ? createdHandles[i] : null;
                        zonesJson.Add(new
                        {
                            name = row.Name,
                            area_m2 = row.AreaM2,
                            boundary_handle = bh
                        });
                    }

                    var ringsPerShaft = new int[n];
                    foreach (var row in metrics.ZoneTable)
                    {
                        if (row.ZoneOwnerIndex.HasValue && row.ZoneOwnerIndex.Value >= 0 && row.ZoneOwnerIndex.Value < n)
                            ringsPerShaft[row.ZoneOwnerIndex.Value]++;
                    }

                    bool hasSplitShaftZones = ringsPerShaft.Any(c => c > 1);
                    int shaftAssignedRings = metrics.ZoneTable.Count(z => (z.ZoneOwnerIndex ?? -1) >= 0);
                    int uncoveredRings = metrics.ZoneTable.Count(z => z.ZoneOwnerIndex.HasValue && z.ZoneOwnerIndex.Value < 0);
                    bool zonesMatchShafts = !hasSplitShaftZones && shaftAssignedRings == n && uncoveredRings == 0;

                    RecordDecision(doc, memory, "create_zones_by_cuts", resolvedFloorHandle, modeUsed, metrics.ZoningSummary);
                    NotifyWriteCommitted(doc);

                    return JsonSupport.Serialize(new
                    {
                        status = "ok",
                        floor_boundary_handle = resolvedFloorHandle,
                        floor_boundary_auto_detection = autoDetectionNote,
                        mode_used = modeUsed,
                        zoning_summary = metrics.ZoningSummary,
                        shaft_site_count = n,
                        zone_outline_count = metrics.ZoneTable.Count,
                        shaft_assigned_zone_outlines = shaftAssignedRings,
                        uncovered_zone_outlines = uncoveredRings,
                        has_split_shaft_zones = hasSplitShaftZones,
                        zones_match_shafts = zonesMatchShafts,
                        zones_created = zonesJson,
                        prior_zone_entities_erased = priorZonesErased,
                        next_step = "Call list_zones to refresh. Run design_zone with each new zone boundary_handle."
                    });
                }
            }
            catch (Exception ex)
            {
                return Err("create_zones_by_cuts failed: " + ex.Message);
            }
            finally
            {
                try { floor.Dispose(); } catch { /* ignore */ }
            }
        }

        // ── design_zone ───────────────────────────────────────────────────────────

        /// <summary>
        /// Full pipeline for one zone: cleanup → route main pipe (internal coverage grid, then skeleton-first routing with fallbacks) → place sprinklers → attach branches.
        /// Prefer this over calling the four steps individually unless you need partial control.
        /// </summary>
        public static string DesignZone(Document doc, string boundaryHandleHex, bool overrideManualEdits, ProjectMemory memory)
            => DesignZone(doc, boundaryHandleHex, overrideManualEdits, memory, null);

        public static string DesignZone(Document doc, string boundaryHandleHex, bool overrideManualEdits, ProjectMemory memory, DesignZoneOptions options)
        {
            if (doc == null) return Err("No document.");
            if (string.IsNullOrWhiteSpace(boundaryHandleHex)) return Err("boundary_handle is required.");

            var guard = CheckDestructiveGuard(memory, boundaryHandleHex, overrideManualEdits);
            if (guard != null) return guard;

            var db = doc.Database;
            if (!TryResolveBoundary(db, boundaryHandleHex, out Polyline zone, out ObjectId boundaryId))
                return Err("Could not resolve boundary handle '" + boundaryHandleHex + "'.");

            try
            {
                var ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zone);
                if (ring == null || ring.Count < 3)
                    return Err("Zone boundary is invalid.");

                var steps = new List<string>();

                // ── Step 1: cleanup ───────────────────────────────────────────────
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    SprinklerXData.EnsureRegApp(tr, db);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    int cleared = SprinklerZoneAutomationCleanup.ClearPriorAutomatedContent(
                        tr, ms, ring, boundaryHandleHex, boundaryId);
                    tr.Commit();
                    if (cleared > 0) steps.Add("Cleared " + cleared + " prior entities.");
                }

                NotifyWriteCommitted(doc);
                if (!TryReloadZoneBoundary(db, boundaryHandleHex, ref zone, ref boundaryId, out ring))
                    return Err("design_zone: boundary handle no longer resolves after cleanup.");

                // ── Step 2: route main pipe ───────────────────────────────────────
                var cfgDz            = RuntimeSettings.Load();
                double spacingM   = options?.SpacingM ?? memory?.DefaultSpacingM ?? cfgDz.SprinklerSpacingM;
                double radiusM    = options?.CoverageRadiusM ?? 1.5;
                string orient     = options?.PreferredOrientation;
                string routeStrategy = options?.Strategy;
                if (!RouteMainPipeCommand.TryRouteMainPipeForZone(
                        doc, zone, boundaryHandleHex,
                        out string routeErr, out string routeSummary,
                        sprinklerSpacingMeters: spacingM,
                        sprinklerCoverageRadiusMeters: radiusM,
                        preferredOrientation: orient,
                        strategy: routeStrategy,
                        skeletonCellSizeMeters: options?.SkeletonCellSizeM,
                        skeletonMinClearanceMeters: options?.SkeletonMinClearanceM,
                        skeletonPruneBranchLengthMeters: options?.SkeletonPruneBranchLengthM,
                        mainPipeLengthPenalty: options?.MainPipeLengthPenalty))
                    return Err("route_main_pipe step failed: " + (routeErr ?? "Unknown error."));
                steps.Add("Pipe: " + routeSummary);

                NotifyWriteCommitted(doc);
                if (!TryReloadZoneBoundary(db, boundaryHandleHex, ref zone, ref boundaryId, out ring))
                    return Err("design_zone: boundary handle no longer resolves after route_main_pipe.");

                // ── Step 3: place sprinklers ──────────────────────────────────────
                double gapM        = options?.MaxBoundaryGapM ?? cfgDz.SprinklerToBoundaryDistanceM;
                bool trunkAnchored = options?.TrunkAnchored ?? true;
                double offsetXM    = options?.GridAnchorOffsetXM ?? 0;
                double offsetYM    = options?.GridAnchorOffsetYM ?? 0;
                if (!ApplySprinklersCommand.TryApplySprinklersForZone(
                        doc, zone, boundaryHandleHex,
                        out string placeMsg,
                        useTrunkAnchoredGrid: trunkAnchored,
                        spacingMeters: spacingM,
                        coverageRadiusMeters: radiusM,
                        maxBoundarySprinklerGapMeters: gapM,
                        gridAnchorOffsetXMeters: offsetXM,
                        gridAnchorOffsetYMeters: offsetYM))
                    return Err("place_sprinklers step failed: " + (placeMsg ?? "Unknown error."));
                steps.Add(placeMsg);

                NotifyWriteCommitted(doc);
                if (!TryReloadZoneBoundary(db, boundaryHandleHex, ref zone, ref boundaryId, out ring))
                    return Err("design_zone: boundary handle no longer resolves after place_sprinklers.");

                // ── Step 4: attach branches ───────────────────────────────────────
                if (!AttachBranchesCommand.TryAttachBranchesForZone(
                        doc, db, zone, ring, boundaryHandleHex, out string branchErr))
                    return Err("attach_branches step failed: " + (branchErr ?? "Unknown error."));
                if (!AttachBranchesCommand.TryPlaceReducersForZone(
                        doc, db, zone, ring, boundaryHandleHex, routeBranchPipesFromConnectorFirst: false, ObjectId.Null, out string redErrDz))
                    return Err("place_reducers step failed: " + (redErrDz ?? "Unknown error."));
                steps.Add("Branches attached.");

                var summary = string.Join(" ", steps);
                var optParams = options == null ? (overrideManualEdits ? "override=true" : null)
                    : JsonSupport.Serialize(new { override_manual_edits = overrideManualEdits, spacing_m = options.SpacingM, preferred_orientation = options.PreferredOrientation, trunk_anchored = options.TrunkAnchored, grid_offset_x = options.GridAnchorOffsetXM, grid_offset_y = options.GridAnchorOffsetYM });
                RecordDecision(doc, memory, "design_zone", boundaryHandleHex, optParams, summary);
                NotifyWriteCommitted(doc);

                return JsonSupport.Serialize(new
                {
                    status          = "ok",
                    boundary_handle  = boundaryHandleHex,
                    summary,
                    steps,
                    next_step        = "Zone fully designed. Call validate_coverage to verify NFPA compliance, then design_zone the next zone."
                });
            }
            catch (Exception ex)
            {
                return Err("design_zone failed: " + ex.Message);
            }
            finally
            {
                try { zone.Dispose(); } catch { /* ignore */ }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static bool TryReloadZoneBoundary(
            Database db,
            string boundaryHandleHex,
            ref Polyline zone,
            ref ObjectId boundaryId,
            out List<Point2d> ring)
        {
            ring = null;
            try { zone?.Dispose(); } catch { /* ignore */ }
            zone = null;
            if (!TryResolveBoundary(db, boundaryHandleHex, out zone, out boundaryId))
                return false;
            ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zone);
            return ring != null && ring.Count >= 3;
        }

        /// <summary>
        /// Resolves a handle hex string to a cloned Polyline (safe to use after the
        /// read transaction commits). Returns false if the handle doesn't resolve to
        /// a closed Polyline or Polyline2d.
        /// </summary>
        internal static bool TryResolveBoundary(Database db, string handleHex, out Polyline zone, out ObjectId boundaryId)
        {
            zone = null;
            boundaryId = ObjectId.Null;
            if (string.IsNullOrWhiteSpace(handleHex) || db == null) return false;

            try
            {
                string cleaned = handleHex.Trim();
                if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    cleaned = cleaned.Substring(2);
                long v = Convert.ToInt64(cleaned, 16);
                var handle = new Handle(v);
                if (!db.TryGetObjectId(handle, out boundaryId)) return false;

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var obj = tr.GetObject(boundaryId, OpenMode.ForRead);

                    if (obj is Polyline lw && lw.Closed)
                    {
                        zone = (Polyline)lw.Clone();
                    }
                    else if (obj is Polyline2d p2d)
                    {
                        var conv = BoundaryEntityToClosedLwPolyline.FromPolyline2d(p2d, db);
                        if (conv.Closed) zone = (Polyline)conv.Clone();
                        conv.Dispose();
                    }

                    tr.Commit();
                }
                return zone != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Returns an error JSON string if the zone is locked or has manual edits that
        /// were not explicitly overridden. Returns null if the write is permitted.
        /// </summary>
        private static string CheckDestructiveGuard(ProjectMemory memory, string boundaryHandleHex, bool overrideManualEdits)
        {
            if (memory?.Zones == null) return null;
            if (!memory.Zones.TryGetValue(boundaryHandleHex, out var zm) || zm == null) return null;

            if (zm.IsLocked)
            {
                var note = !string.IsNullOrEmpty(zm.EngineerNote) ? " (" + zm.EngineerNote + ")" : string.Empty;
                return Err("Zone " + boundaryHandleHex + " is locked" + note +
                           ". Locked zones cannot be modified by the agent. Inform the user.");
            }

            if (zm.HasManualEdits && !overrideManualEdits)
            {
                var when = !string.IsNullOrEmpty(zm.LastManualEditDate) ? " (last: " + zm.LastManualEditDate + ")" : string.Empty;
                return Err("Zone " + boundaryHandleHex + " has manual engineer edits" + when +
                           ". Pass override_manual_edits=true to proceed, or ask the engineer first.");
            }

            return null;
        }

        private static void RecordDecision(Document doc, ProjectMemory memory, string action, string boundaryHandle, string parameters, string outcome)
        {
            if (memory == null) return;
            try
            {
                memory.RecordDecision(new DecisionSnapshot
                {
                    ZoneId     = boundaryHandle,
                    Action     = action,
                    Parameters = parameters,
                    Outcome    = outcome,
                    Timestamp  = DateTime.UtcNow.ToString("o")
                });
                memory.Save(doc);
            }
            catch { /* never block a write op because of memory save failure */ }
        }

        private static string Err(string message)
            => JsonSupport.Serialize(new { status = "error", message });
    }

    [DataContract]
    internal sealed class CreateZonesByCutsToolArgs
    {
        [DataMember(Name = "floor_boundary_handle")]
        public string FloorBoundaryHandle { get; set; }

        [DataMember(Name = "cuts")]
        public CutSegmentDto[] Cuts { get; set; }
    }

    [DataContract]
    internal sealed class CutSegmentDto
    {
        [DataMember] public double x1 { get; set; }
        [DataMember] public double y1 { get; set; }
        [DataMember] public double x2 { get; set; }
        [DataMember] public double y2 { get; set; }
    }
}
