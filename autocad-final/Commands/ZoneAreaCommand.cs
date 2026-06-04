using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using autocad_final.Licensing;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;
using autocad_final.UI;
using System.Windows.Forms;

namespace autocad_final.Commands
{
    /// <summary>
    /// Zoning modes: <c>SPRINKLERZONEAREA</c> / <c>SHAFTZONEAREA</c> — equal-area axis-aligned strips (full floor; no 3000 m² cap).
    /// <c>SPRINKLERZONEAREA_GRID</c> — nearest-shaft grid zoning (full floor). <c>SPRINKLERZONEAREA_CAP</c> — same grid with ~3000 m²/shaft cap when INSUNITS allows.
    /// </summary>
    public class ZoneAreaCommand
    {
        /// <summary>
        /// Raised after a successful run (command line or <see cref="Run"/>). Used by the palette so it can queue
        /// <c>SPRINKLERZONEAREA</c> via <see cref="Document.SendStringToExecute"/> instead of calling <see cref="TryRun"/> from a WinForms click (which breaks entity hover / selection preview).
        /// </summary>
        public static event Action<PolygonMetrics> ZoneAreaCompleted;

        public enum ZoningMode
        {
            EqualAreaStrips,
            Grid,
            GridWithCap,
            ShaftMidlineStrips,
            /// <summary>Straight-cut recursive bisection targeting total_area / n_shafts per zone.</summary>
            EqualAreaBisection,
            /// <summary>Equal-area strips; one zone per shaft count; no shaft pairing or auto-assign (clustered shafts).</summary>
            ClusteredEqualStrips
        }

        [CommandMethod("SPRINKLERZONEAREA", CommandFlags.Modal)]
        [CommandMethod("SHAFTZONEAREA", CommandFlags.Modal)]
        public void SprinklerZoneArea()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            if (!TrialGuard.EnsureActive(doc.Editor)) return;
            Run(doc, ZoningMode.EqualAreaStrips);
        }

        /// <summary>
        /// Interactive pick of outer floor boundary, then the same automatic cascade as
        /// the create_sprinkler_zones agent tool (bisection → Voronoi+Lloyd → strips → midline → grid → cap).
        /// Palette label: "Zone boundary + threshold".
        /// </summary>
        [CommandMethod("SPRINKLERZONEAREA2", CommandFlags.Modal)]
        public void SprinklerZoneAreaImplementation2()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;
            if (!SelectPolygonBoundary.TrySelect(ed, out Polyline boundary, out ObjectId boundaryEntityId))
                return;

            var db = doc.Database;
            try
            {
                FindShaftsInsideBoundary.GetShaftHandlesAndPositionsInsideBoundary(db, boundary, out var shaftPts, out var shaftHandlesRaw);
                double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
                if (tol <= 0) tol = 1e-6;
                ShaftVoronoiZonesOnFloorPolyline.DedupeShaftSitesWithHandles(
                    shaftPts, shaftHandlesRaw, tol,
                    out var sites,
                    out _);
                if (sites.Count < 2)
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(
                        ed,
                        "Need at least two shaft sites inside the boundary for automatic zoning (found " +
                        sites.Count.ToString(CultureInfo.InvariantCulture) + ").",
                        MessageBoxIcon.Warning);
                    return;
                }

                using (doc.LockDocument())
                {
                    if (!SprinklerFloorZoningCascade.TryRun(
                            doc, boundary, boundaryEntityId, echoMessages: true,
                            out PolygonMetrics metrics, out string modeUsed, out _, out var fallbacks, out _))
                    {
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(
                            ed,
                            "Automatic zoning could not produce zone outlines. " + string.Join("; ", fallbacks),
                            MessageBoxIcon.Warning);
                        return;
                    }

                    // Clear any legacy global-boundary separator lines so the output is the closed zone
                    // polylines only (what the user expects to see/select).
                    try { ZoneGlobalBoundaryBuilder.TryClearForFloorBoundary(doc, boundaryEntityId, out _); } catch { /* ignore */ }

                    ed.WriteMessage("\nZoning mode used: " + modeUsed + "\n");
                    EditorWritePolygonNetArea.Run(ed, metrics.Area);
                    ed.WriteMessage("Perimeter: " + metrics.Perimeter.ToString("F3", CultureInfo.InvariantCulture) + "\n");
                    if (!string.IsNullOrEmpty(metrics.ZoningSummary))
                        ed.WriteMessage(metrics.ZoningSummary + "\n");

                    ZoneAreaCompleted?.Invoke(metrics);
                }
            }
            finally
            {
                try { boundary?.Dispose(); } catch { /* ignore */ }
            }
        }

        /// <summary>Nearest-shaft grid zoning without the 3000 m² per-shaft cap.</summary>
        [CommandMethod("SPRINKLERZONEAREA_GRID", CommandFlags.Modal)]
        public void SprinklerZoneAreaGrid()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            if (!TrialGuard.EnsureActive(doc.Editor)) return;
            Run(doc, ZoningMode.Grid);
        }

        /// <summary>Grid zoning with ~3000 m² per-shaft limit when INSUNITS supports it.</summary>
        [CommandMethod("SPRINKLERZONEAREA_CAP", CommandFlags.Modal)]
        public void SprinklerZoneAreaWithCap()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            if (!TrialGuard.EnsureActive(doc.Editor)) return;
            Run(doc, ZoningMode.GridWithCap);
        }

        public static void Run(Document doc, ZoningMode mode)
        {
            PolygonMetrics metrics;
            if (!TryRun(doc, out metrics, mode))
                return;

            var ed = doc.Editor;
            EditorWritePolygonNetArea.Run(ed, metrics.Area);
            ed.WriteMessage("Perimeter: " + metrics.Perimeter.ToString("F3", CultureInfo.InvariantCulture) + "\n");
            if (!string.IsNullOrEmpty(metrics.ZoningSummary))
                ed.WriteMessage(metrics.ZoningSummary + "\n");

            ZoneAreaCompleted?.Invoke(metrics);
        }

        public static bool TryRun(Document doc, out PolygonMetrics metrics, ZoningMode mode)
        {
            metrics = null;
            var ed = doc.Editor;

            bool requireFloorBoundaryLayer =
                mode == ZoningMode.EqualAreaStrips ||
                mode == ZoningMode.ShaftMidlineStrips ||
                mode == ZoningMode.ClusteredEqualStrips;

            var boundary = requireFloorBoundaryLayer
                ? SelectPolygonBoundaryOnSprinklerWorkLayer.Run(ed)
                : SelectPolygonBoundary.Run(ed);
            if (boundary == null)
                return false;

            try
            {
                return TryRunWithBoundary(
                    doc, boundary, mode, echoMessages: true, shaftMidlineSnapSearchMeters: 0,
                    createdZoneBoundaryHandles: null, out metrics);
            }
            finally
            {
                boundary.Dispose();
            }
        }

        /// <summary>
        /// Runs zoning for an existing closed floor boundary (any layer). Caller supplies a cloned <see cref="Polyline"/>; this method does not dispose it.
        /// </summary>
        /// <param name="shaftMidlineSnapSearchMeters">Snap distance for <see cref="ZoningMode.ShaftMidlineStrips"/>; 0 uses <see cref="ShaftMidlineStripZonesInPolygon2d.SnapSearchMeters"/>.</param>
        /// <param name="createdZoneBoundaryHandles">When non-null, receives hex handles of new zone outline polylines (in zone order).</param>
        /// <param name="gridLloydIterations">For <see cref="ZoningMode.Grid"/> only: Lloyd relaxation passes (0 = off). Ignored for <see cref="ZoningMode.GridWithCap"/>.</param>
        public static bool TryRunWithBoundary(
            Document doc,
            Polyline boundary,
            ZoningMode mode,
            bool echoMessages,
            double shaftMidlineSnapSearchMeters,
            List<string> createdZoneBoundaryHandles,
            out PolygonMetrics metrics)
            => TryRunWithBoundary(
                doc, boundary, mode, echoMessages, shaftMidlineSnapSearchMeters, createdZoneBoundaryHandles, 0, out metrics);

        public static bool TryRunWithBoundary(
            Document doc,
            Polyline boundary,
            ZoningMode mode,
            bool echoMessages,
            double shaftMidlineSnapSearchMeters,
            List<string> createdZoneBoundaryHandles,
            int gridLloydIterations,
            out PolygonMetrics metrics)
        {
            metrics = null;
            if (doc == null || boundary == null)
                return false;

            var ed = doc.Editor;
            var db = doc.Database;

            void Msg(string s)
            {
                if (echoMessages)
                    ed.WriteMessage(s);
            }

            var outlineHandles = createdZoneBoundaryHandles ?? new List<string>();

            FindShaftsInsideBoundary.GetShaftHandlesAndPositionsInsideBoundary(db, boundary, out var shaftPts, out var shaftHandlesRaw);
            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            if (tol <= 0) tol = 1e-6;
            ShaftVoronoiZonesOnFloorPolyline.DedupeShaftSitesWithHandles(
                shaftPts, shaftHandlesRaw, tol,
                out var sites,
                out var shaftHandlesDeduped);

            double rawArea = boundary.Area;

            metrics = new PolygonMetrics
            {
                Area = PolylineNetArea.Run(boundary),
                Perimeter = boundary.Length,
                Layer = boundary.Layer,
                RoomName = FindRoomNameInsideBoundary.Run(db, boundary),
                ShaftCount = shaftPts.Count,
                ShaftCoordinates = FormatShaftCoords(shaftPts)
            };

            int n = sites.Count;

            if (n == 0)
            {
                metrics.ZoneAreaPerShaftM2 = null;
                metrics.ZoningSummary =
                    "No shaft blocks inside the boundary — place shaft inserts (block name \"shaft\") to use zoning.";
                return true;
            }

            if (n == 1)
            {
                metrics.ZoneAreaPerShaftM2 = null;
                metrics.ZoningSummary =
                    "Only one shaft inside the boundary — zoning is not required (no zone outlines drawn).";
                return true;
            }

            if (mode == ZoningMode.EqualAreaStrips)
            {
                // Snap interior zone dividers onto nearby floor/room walls so green boundaries land on
                // the white architecture instead of cutting through open space.
                var wallSnapRings = CollectWallSnapRingsInsideFloor(db, boundary);
                double wallSnapRadiusDu = 0.0;
                try
                {
                    if (DrawingUnitsHelper.TryMetersToDrawingLength(
                            db.Insunits, ShaftMidlineStripZonesInPolygon2d.SnapSearchMeters, out double snapDu) && snapDu > 0)
                        wallSnapRadiusDu = snapDu;
                }
                catch { /* leave snapping off if scale is unknown */ }

                if (!EqualAreaAxisStripZonesInPolygon2d.TryBuildZoneRings(
                        boundary,
                        sites,
                        n,
                        tol,
                        pairToShafts: true,
                        wallSnapRings,
                        wallSnapRadiusDu,
                        out var zoneRings,
                        out var ringShaftIdx,
                        out bool splitVertical,
                        out string stripErr))
                {
                    Msg("\n" + stripErr + "\n");
                    metrics.ZoningSummary = stripErr;
                }
                else if (zoneRings.Count > 0)
                {
                    // Pull every zone-outline vertex onto the nearest floor/room wall so the green outline
                    // lies exactly on the white architecture (closes any small inset; no-op when already
                    // coincident). Interior divider vertices, far from any wall, are left untouched.
                    double zoneSnapTolDu = 0.0;
                    try
                    {
                        if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, ZoneOutlineWallSnapMeters, out double zsd) && zsd > 0)
                            zoneSnapTolDu = zsd;
                    }
                    catch { /* leave vertex snapping off if scale is unknown */ }

                    if (zoneSnapTolDu > 0)
                    {
                        var snapTargets = new List<IList<Point2d>>();
                        var floorRingForSnap = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(boundary);
                        if (floorRingForSnap != null && floorRingForSnap.Count >= 3)
                            snapTargets.Add(floorRingForSnap);
                        if (wallSnapRings != null)
                            snapTargets.AddRange(wallSnapRings);
                        SnapZoneRingVerticesToWalls(zoneRings, snapTargets, zoneSnapTolDu);
                    }

                    DrawingUnitsHelper.ComputeFormulaZoneTargets(
                        db,
                        rawArea,
                        n,
                        out double aTargetDu,
                        out double? floorM2Targets,
                        out double? aTargetM2,
                        out _);
                    double? floorM2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, rawArea, out _);

                    metrics.ZoningSummary = EqualAreaAxisStripZonesInPolygon2d.FormatStripZoningSummary(
                        floorM2Targets ?? floorM2,
                        n,
                        splitVertical,
                        aTargetDu);
                    if (aTargetM2.HasValue)
                        metrics.ZoningSummary += string.Format(
                            CultureInfo.InvariantCulture,
                            " Formula target ≈ {0:F2} m² per zone.",
                            aTargetM2.Value);

                    metrics.ZoneTable = new List<ZoneTableEntry>(zoneRings.Count);
                    for (int zi = 0; zi < zoneRings.Count; zi++)
                    {
                        double aDu = PolygonVerticalHalfPlaneClip2d.AbsArea(zoneRings[zi]);
                        double? m2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, aDu, out _);
                        int si = ringShaftIdx[zi];
                        string name = "Zone " + (zi + 1).ToString(CultureInfo.InvariantCulture);

                        metrics.ZoneTable.Add(new ZoneTableEntry
                        {
                            Name = name,
                            AreaDrawingUnits = aDu,
                            AreaM2 = m2,
                            ZoneOwnerIndex = si
                        });
                    }

                    ShaftVoronoiZonesOnFloorPolyline.AppendZoneOutlinePolylines(
                        doc, zoneRings, boundary, metrics.ZoneTable, zoneOutlinesOnFloorBoundaryLayer: true, outlineHandles);
                    FinishZoneOutlinesWithAutoShaftAssignment(db, metrics, outlineHandles, ringShaftIdx, shaftHandlesDeduped, boundary);
                    Msg("\nZone outlines added on layer \"" + SprinklerLayers.WorkLayer + "\" (floor boundary, dashed); labels on \"" +
                        SprinklerLayers.ZoneLabelLayer + "\".\n");
                    foreach (var z in metrics.ZoneTable)
                    {
                        if (z.AreaM2.HasValue)
                            Msg("  " + z.Name + ": " + z.AreaM2.Value.ToString("F2", CultureInfo.InvariantCulture) + " m²\n");
                        else
                            Msg("  " + z.Name + ": " + z.AreaDrawingUnits.ToString("F2", CultureInfo.InvariantCulture) + " sq. units\n");
                    }
                }

                return true;
            }

            if (mode == ZoningMode.ClusteredEqualStrips)
            {
                if (!ClusteredEqualAreaStripZonesInPolygon2d.TryBuildZoneRings(
                        boundary,
                        sites,
                        n,
                        tol,
                        out var zoneRings,
                        out bool splitVertical,
                        out string stripErr))
                {
                    Msg("\n" + stripErr + "\n");
                    metrics.ZoningSummary = stripErr;
                }
                else if (zoneRings.Count > 0)
                {
                    DrawingUnitsHelper.ComputeFormulaZoneTargets(
                        db,
                        rawArea,
                        n,
                        out double aTargetDu,
                        out double? floorM2Targets,
                        out double? aTargetM2,
                        out _);
                    double? floorM2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, rawArea, out _);

                    metrics.ZoningSummary =
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Clustered shafts: {0} equal-area strips ({1}); each zone ≈ {2:F2} sq. drawing units. Zones are not auto-linked to shafts — use ASSIGNSHAFTOZONE before routing.",
                            n,
                            splitVertical ? "vertical cuts" : "horizontal cuts",
                            aTargetDu);
                    if (floorM2Targets.HasValue || floorM2.HasValue)
                        metrics.ZoningSummary += string.Format(
                            CultureInfo.InvariantCulture,
                            " Floor {0:F2} m².",
                            (floorM2Targets ?? floorM2).Value);

                    if (aTargetM2.HasValue)
                        metrics.ZoningSummary += string.Format(
                            CultureInfo.InvariantCulture,
                            " Target ≈ {0:F2} m² per zone.",
                            aTargetM2.Value);

                    metrics.ZoneTable = new List<ZoneTableEntry>(zoneRings.Count);
                    for (int zi = 0; zi < zoneRings.Count; zi++)
                    {
                        double aDu = PolygonVerticalHalfPlaneClip2d.AbsArea(zoneRings[zi]);
                        double? m2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, aDu, out _);
                        string name = "Zone " + (zi + 1).ToString(CultureInfo.InvariantCulture);

                        metrics.ZoneTable.Add(new ZoneTableEntry
                        {
                            Name = name,
                            AreaDrawingUnits = aDu,
                            AreaM2 = m2,
                            ZoneOwnerIndex = null
                        });
                    }

                    ShaftVoronoiZonesOnFloorPolyline.AppendZoneOutlinePolylines(
                        doc, zoneRings, boundary, metrics.ZoneTable,
                        zoneOutlinesOnFloorBoundaryLayer: true, outlineHandles);
                    ApplyZoningKindToCreatedOutlines(db, outlineHandles, SprinklerXData.ZoningKindClusteredStrips);
                    AssignShaftToZoneCommand.EnsureShaftUidsForFloorBoundary(db, boundary);
                    Msg("\nZone outlines added (clustered-shaft equal strips, dashed). Assign each zone to a shaft with ASSIGNSHAFTOZONE before routing.\n");
                    foreach (var z in metrics.ZoneTable)
                    {
                        if (z.AreaM2.HasValue)
                            Msg("  " + z.Name + ": " + z.AreaM2.Value.ToString("F2", CultureInfo.InvariantCulture) + " m²\n");
                        else
                            Msg("  " + z.Name + ": " + z.AreaDrawingUnits.ToString("F2", CultureInfo.InvariantCulture) + " sq. units\n");
                    }
                }

                return true;
            }

            if (mode == ZoningMode.ShaftMidlineStrips)
            {
                var shaftBlocks = FindShaftsInsideBoundary.GetShaftBlocksInsideBoundary(db, boundary);
                var dedupBlocks = DedupeShaftBlocks(shaftBlocks, tol);
                if (dedupBlocks.Count < 2)
                {
                    metrics.ZoningSummary = "Need at least two shafts for zones.";
                    return true;
                }

                double snapM = shaftMidlineSnapSearchMeters > 0
                    ? shaftMidlineSnapSearchMeters
                    : ShaftMidlineStripZonesInPolygon2d.SnapSearchMeters;

                bool ok = ShaftMidlineStripZonesInPolygon2d.TryBuildZoneRingsMulti(
                        db,
                        boundary,
                        dedupBlocks,
                        tol,
                        snapM,
                        out var zoneRings,
                        out var ringShaftIdx,
                        out bool splitVertical,
                        out string stripErr);

                if (!ok || zoneRings.Count == 0)
                {
                    metrics.ZoningSummary = stripErr ?? "Strip zoning failed.";
                    Msg("\n" + metrics.ZoningSummary + "\n");
                    return true;
                }

                metrics.ZoningSummary =
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Zone implementation 2 (equal-area strips + snap to corner/wall within {0} m of ideal cut; Region clipping). ",
                        snapM) +
                    (splitVertical ? "Vertical cuts. " : "Horizontal cuts. ") +
                    (stripErr ?? string.Empty);

                metrics.ZoneTable = new List<ZoneTableEntry>(zoneRings.Count);
                var shaftPart = new int[dedupBlocks.Count];
                for (int zi = 0; zi < zoneRings.Count; zi++)
                {
                    double aDu = PolygonVerticalHalfPlaneClip2d.AbsArea(zoneRings[zi]);
                    double? m2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, aDu, out _);
                    int si = ringShaftIdx[zi];
                    if (si < 0) si = 0;
                    if (si >= shaftPart.Length) si = shaftPart.Length - 1;

                    shaftPart[si]++;
                    string name = shaftPart[si] == 1
                        ? "Zone " + (si + 1).ToString(CultureInfo.InvariantCulture)
                        : "Zone " + (si + 1).ToString(CultureInfo.InvariantCulture) + " (" +
                          shaftPart[si].ToString(CultureInfo.InvariantCulture) + ")";

                    metrics.ZoneTable.Add(new ZoneTableEntry
                    {
                        Name = name,
                        AreaDrawingUnits = aDu,
                        AreaM2 = m2,
                        ZoneOwnerIndex = si
                    });
                }

                ShaftVoronoiZonesOnFloorPolyline.AppendZoneOutlinePolylines(
                    doc, zoneRings, boundary, metrics.ZoneTable, zoneOutlinesOnFloorBoundaryLayer: true, outlineHandles);
                var shaftHexMidline = dedupBlocks.ConvertAll(b => b.BlockHandleHex);
                FinishZoneOutlinesWithAutoShaftAssignment(db, metrics, outlineHandles, ringShaftIdx, shaftHexMidline, boundary);
                Msg("\nZone outlines added on layer \"" + SprinklerLayers.WorkLayer + "\" (floor boundary, dashed); labels on \"" +
                    SprinklerLayers.ZoneLabelLayer + "\".\n");
                return true;
            }

            if (mode == ZoningMode.EqualAreaBisection)
            {
                var floorRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(boundary);
                if (floorRing == null || floorRing.Count < 3)
                {
                    metrics.ZoningSummary = "Could not sample floor ring for equal-area bisection.";
                    Msg("\n" + metrics.ZoningSummary + "\n");
                    return true;
                }

                if (!EqualAreaRecursiveBisection2d.TryBuildZoneRings(
                        floorRing, sites, null,
                        out var bisectRings, out var bisectOwners, out string bisectSummary))
                {
                    metrics.ZoningSummary = "Equal-area bisection failed: " + bisectSummary;
                    Msg("\n" + metrics.ZoningSummary + "\n");
                    return true;
                }

                metrics.ZoningSummary = bisectSummary;
                metrics.ZoneTable = new List<ZoneTableEntry>(bisectRings.Count);
                for (int zi = 0; zi < bisectRings.Count; zi++)
                {
                    double aDu = PolygonVerticalHalfPlaneClip2d.AbsArea(bisectRings[zi]);
                    double? m2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, aDu, out _);
                    int si = bisectOwners[zi];
                    string name = "Zone " + (si + 1).ToString(CultureInfo.InvariantCulture);
                    metrics.ZoneTable.Add(new ZoneTableEntry
                    {
                        Name = name,
                        AreaDrawingUnits = aDu,
                        AreaM2 = m2,
                        ZoneOwnerIndex = si
                    });
                }

                ShaftVoronoiZonesOnFloorPolyline.AppendZoneOutlinePolylines(
                    doc, bisectRings, boundary, metrics.ZoneTable,
                    zoneOutlinesOnFloorBoundaryLayer: false, outlineHandles);
                FinishZoneOutlinesWithAutoShaftAssignment(db, metrics, outlineHandles, bisectOwners, shaftHandlesDeduped, boundary);
                Msg("\nZone outlines added on layer \"" + SprinklerLayers.ZoneLayer + "\" (equal-area bisection, dashed); labels on \"" +
                    SprinklerLayers.ZoneLabelLayer + "\".\n");
                return true;
            }

            // Grid / GridWithCap
            {
                bool enforceCap = mode == ZoningMode.GridWithCap;
                int lloydIter = (!enforceCap && gridLloydIterations > 0) ? gridLloydIterations : 0;
                metrics.ZoneAreaPerShaftM2 = null;
                double? floorM2Out = DrawingUnitsHelper.TryGetAreaSquareMeters(db, rawArea, out _);

                if (!GridNearestShaftZoning2d.TryBuildZoneRings(
                        boundary,
                        sites,
                        db,
                        tol,
                        GridNearestShaftZoning2d.DefaultCellSizeMeters,
                        enforceCap,
                        lloydIter,
                        out var zoneRings,
                        out var ringShaftIdx,
                        out double uncoveredDu,
                        out bool insunitsM2Cap,
                        out bool perShaftCapEnforced,
                        out double cellStepDu,
                        out bool _,
                        out bool gridCoarsened,
                        out int gridCols,
                        out int gridRows,
                        out string err))
                {
                    Msg("\n" + err + "\n");
                    metrics.ZoningSummary = err;
                }
                else if (zoneRings.Count > 0)
                {
                    metrics.ZoningSummary = GridNearestShaftZoning2d.FormatZoningSummary(
                        floorM2Out,
                        n,
                        GridNearestShaftZoning2d.DefaultCellSizeMeters,
                        cellStepDu,
                        insunitsM2Cap,
                        perShaftCapEnforced,
                        uncoveredDu,
                        db,
                        gridCoarsened,
                        gridCols,
                        gridRows,
                        usedLloydRelaxation: lloydIter > 0,
                        lloydIterations: lloydIter);

                    metrics.ZoneTable = new List<ZoneTableEntry>(zoneRings.Count);
                    var shaftPart = new int[n];
                    int uncPart = 0;
                    for (int zi = 0; zi < zoneRings.Count; zi++)
                    {
                        double aDu = PolygonVerticalHalfPlaneClip2d.AbsArea(zoneRings[zi]);
                        double? m2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, aDu, out _);
                        int si = ringShaftIdx[zi];
                        string name;
                        if (si < 0)
                        {
                            uncPart++;
                            name = uncPart == 1
                                ? "Uncovered"
                                : "Uncovered (" + uncPart.ToString(CultureInfo.InvariantCulture) + ")";
                        }
                        else
                        {
                            shaftPart[si]++;
                            name = shaftPart[si] == 1
                                ? "Zone " + (si + 1).ToString(CultureInfo.InvariantCulture)
                                : "Zone " + (si + 1).ToString(CultureInfo.InvariantCulture) + " (" +
                                  shaftPart[si].ToString(CultureInfo.InvariantCulture) + ")";
                        }

                        metrics.ZoneTable.Add(new ZoneTableEntry
                        {
                            Name = name,
                            AreaDrawingUnits = aDu,
                            AreaM2 = m2,
                            ZoneOwnerIndex = si
                        });
                    }

                    ShaftVoronoiZonesOnFloorPolyline.AppendZoneOutlinePolylines(
                        doc, zoneRings, boundary, metrics.ZoneTable, zoneOutlinesOnFloorBoundaryLayer: false, outlineHandles);
                    FinishZoneOutlinesWithAutoShaftAssignment(db, metrics, outlineHandles, ringShaftIdx, shaftHandlesDeduped, boundary);
                    Msg("\nZone outlines added on layer \"" + SprinklerLayers.ZoneLayer + "\" (green, dashed); labels on \"" +
                        SprinklerLayers.ZoneLabelLayer + "\".\n");
                    foreach (var z in metrics.ZoneTable)
                    {
                        if (z.AreaM2.HasValue)
                            Msg("  " + z.Name + ": " + z.AreaM2.Value.ToString("F2", CultureInfo.InvariantCulture) + " m²\n");
                        else
                            Msg("  " + z.Name + ": " + z.AreaDrawingUnits.ToString("F2", CultureInfo.InvariantCulture) + " sq. units\n");
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Applies LLM-supplied straight cuts (N−1 segments for N shafts), validates one shaft per piece, and draws zone outlines.
        /// </summary>
        public static bool TryRunWithLlmCuts(
            Document doc,
            Polyline boundary,
            bool echoMessages,
            IList<LlmCutZoning2d.Cut> cuts,
            List<string> createdZoneBoundaryHandles,
            out PolygonMetrics metrics)
        {
            metrics = null;
            if (doc == null || boundary == null)
                return false;

            var ed = doc.Editor;
            var db = doc.Database;

            void Msg(string s)
            {
                if (echoMessages)
                    ed.WriteMessage(s);
            }

            var outlineHandles = createdZoneBoundaryHandles ?? new List<string>();

            FindShaftsInsideBoundary.GetShaftHandlesAndPositionsInsideBoundary(db, boundary, out var shaftPts, out var shaftHandlesRaw);
            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            if (tol <= 0) tol = 1e-6;
            ShaftVoronoiZonesOnFloorPolyline.DedupeShaftSitesWithHandles(
                shaftPts, shaftHandlesRaw, tol,
                out var sites,
                out var shaftHandlesDeduped);

            metrics = new PolygonMetrics
            {
                Area = PolylineNetArea.Run(boundary),
                Perimeter = boundary.Length,
                Layer = boundary.Layer,
                RoomName = FindRoomNameInsideBoundary.Run(db, boundary),
                ShaftCount = shaftPts.Count,
                ShaftCoordinates = FormatShaftCoords(shaftPts)
            };

            int n = sites.Count;
            if (n == 0)
            {
                metrics.ZoneAreaPerShaftM2 = null;
                metrics.ZoningSummary =
                    "No shaft blocks inside the boundary — place shaft inserts (block name \"shaft\") to use zoning.";
                return true;
            }

            if (n == 1)
            {
                metrics.ZoneAreaPerShaftM2 = null;
                metrics.ZoningSummary =
                    "Only one shaft inside the boundary — zoning is not required (no zone outlines drawn).";
                return true;
            }

            if (cuts == null)
            {
                metrics.ZoningSummary = "LLM cut zoning: cuts list is null.";
                Msg("\n" + metrics.ZoningSummary + "\n");
                return true;
            }

            if (cuts.Count != n - 1)
            {
                metrics.ZoningSummary =
                    "LLM cut zoning: expected " + (n - 1).ToString(CultureInfo.InvariantCulture) +
                    " cuts for " + n.ToString(CultureInfo.InvariantCulture) + " shafts, got " +
                    cuts.Count.ToString(CultureInfo.InvariantCulture) + ".";
                Msg("\n" + metrics.ZoningSummary + "\n");
                return true;
            }

            var floorRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(boundary);
            if (floorRing == null || floorRing.Count < 3)
            {
                metrics.ZoningSummary = "Could not sample floor ring for LLM cut zoning.";
                Msg("\n" + metrics.ZoningSummary + "\n");
                return true;
            }

            if (!LlmCutZoning2d.TryApplyCuts(floorRing, sites, cuts, out var zoneRings, out _, out string llmSummary))
            {
                metrics.ZoningSummary = "LLM cut zoning failed: " + llmSummary;
                Msg("\n" + metrics.ZoningSummary + "\n");
                return true;
            }

            metrics.ZoningSummary = llmSummary;
            metrics.ZoneTable = new List<ZoneTableEntry>(zoneRings.Count);
            for (int zi = 0; zi < zoneRings.Count; zi++)
            {
                double aDu = PolygonVerticalHalfPlaneClip2d.AbsArea(zoneRings[zi]);
                double? m2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, aDu, out _);
                metrics.ZoneTable.Add(new ZoneTableEntry
                {
                    Name = "Zone " + (zi + 1).ToString(CultureInfo.InvariantCulture),
                    AreaDrawingUnits = aDu,
                    AreaM2 = m2,
                    ZoneOwnerIndex = zi
                });
            }

            ShaftVoronoiZonesOnFloorPolyline.AppendZoneOutlinePolylines(
                doc, zoneRings, boundary, metrics.ZoneTable,
                zoneOutlinesOnFloorBoundaryLayer: false, outlineHandles);
            var llmOwners = new List<int>(zoneRings.Count);
            for (int zi = 0; zi < zoneRings.Count; zi++)
                llmOwners.Add(zi);
            FinishZoneOutlinesWithAutoShaftAssignment(db, metrics, outlineHandles, llmOwners, shaftHandlesDeduped, boundary);
            Msg("\nZone outlines added on layer \"" + SprinklerLayers.ZoneLayer + "\" (LLM straight cuts, dashed); labels on \"" +
                SprinklerLayers.ZoneLabelLayer + "\".\n");
            return true;
        }

        /// <summary>Returns true when zoning produced at least one zone outline row.</summary>
        public static bool OutlinesWereDrawn(PolygonMetrics metrics)
            => metrics?.ZoneTable != null && metrics.ZoneTable.Count > 0;

        /// <summary>Writes <see cref="SprinklerXData.KeyZoningKind"/> on newly created zone outline polylines.</summary>
        public static void ApplyZoningKindToCreatedOutlines(
            Database db,
            IList<string> outlineHandlesHex,
            string zoningKind)
        {
            if (db == null || outlineHandlesHex == null || string.IsNullOrWhiteSpace(zoningKind))
                return;

            try
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                using (doc?.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    SprinklerXData.EnsureRegApp(tr, db);
                    for (int i = 0; i < outlineHandlesHex.Count; i++)
                    {
                        string hx = outlineHandlesHex[i];
                        if (string.IsNullOrEmpty(hx)) continue;
                        Handle h;
                        try { h = new Handle(Convert.ToInt64(hx, 16)); }
                        catch { continue; }
                        ObjectId id = ObjectId.Null;
                        try { id = db.GetObjectId(false, h, 0); } catch { continue; }
                        if (id.IsNull) continue;
                        var ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                        if (ent != null)
                            SprinklerXData.ApplyZoningKindTag(ent, zoningKind);
                    }
                    tr.Commit();
                }
            }
            catch { /* best-effort */ }
        }

        private static void FinishZoneOutlinesWithAutoShaftAssignment(
            Database db,
            PolygonMetrics metrics,
            List<string> outlineHandles,
            IList<int> ownerIndexPerRing,
            IList<string> shaftHandleHexPerDedupedSite,
            Polyline floorBoundary)
        {
            if (outlineHandles == null || outlineHandles.Count == 0 || metrics == null ||
                ownerIndexPerRing == null || shaftHandleHexPerDedupedSite == null)
                return;
            AssignShaftToZoneCommand.ApplyDefaultShaftAssignmentsForCreatedZones(
                db, outlineHandles, ownerIndexPerRing, shaftHandleHexPerDedupedSite, floorBoundary);
            AssignShaftToZoneCommand.MergeShaftAssignmentDisplayNamesIntoZoneTable(db, metrics);
        }

        /// <summary>Max distance a zone-outline vertex may be pulled to land exactly on a floor/room wall (meters).</summary>
        private const double ZoneOutlineWallSnapMeters = 2.0;

        /// <summary>
        /// Pulls each zone-ring vertex onto the nearest point of any target wall ring (floor boundary + room outlines)
        /// when within <paramref name="snapTolDu"/>. Outer zone edges and divider endpoints sit on walls and snap onto
        /// them exactly; interior divider vertices are far from any wall and stay put. Topology is preserved (vertices
        /// only move by &lt;= tolerance toward the wall they already track).
        /// </summary>
        private static void SnapZoneRingVerticesToWalls(
            List<List<Point2d>> zoneRings,
            IList<IList<Point2d>> targetRings,
            double snapTolDu)
        {
            if (zoneRings == null || targetRings == null || targetRings.Count == 0 || snapTolDu <= 0)
                return;

            double tol2 = snapTolDu * snapTolDu;
            foreach (var zr in zoneRings)
            {
                if (zr == null || zr.Count < 3)
                    continue;

                for (int i = 0; i < zr.Count; i++)
                {
                    Point2d v = zr[i];
                    Point2d best = v;
                    double bestD2 = tol2;

                    foreach (var target in targetRings)
                    {
                        if (target == null || target.Count < 2)
                            continue;
                        int n = target.Count;
                        for (int s = 0; s < n; s++)
                        {
                            Point2d a = target[s];
                            Point2d b = target[(s + 1) % n];
                            Point2d p = ClosestPointOnSegment2d(v, a, b);
                            double dx = p.X - v.X, dy = p.Y - v.Y;
                            double d2 = dx * dx + dy * dy;
                            if (d2 < bestD2)
                            {
                                bestD2 = d2;
                                best = p;
                            }
                        }
                    }

                    if (bestD2 < tol2)
                        zr[i] = best;
                }
            }
        }

        private static Point2d ClosestPointOnSegment2d(Point2d p, Point2d a, Point2d b)
        {
            double abx = b.X - a.X, aby = b.Y - a.Y;
            double len2 = abx * abx + aby * aby;
            if (len2 <= 1e-18)
                return a;
            double t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2;
            if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
            return new Point2d(a.X + t * abx, a.Y + t * aby);
        }

        /// <summary>
        /// Collects closed room/floor outline rings (layers "MCD-room" and "MCD-floor boundary") whose centroid
        /// lies inside the floor boundary. Their axis-aligned walls become snap targets for equal-area zone dividers.
        /// The floor boundary's own ring is supplied separately by the strip engine, so it need not be included here.
        /// </summary>
        private static List<IList<Point2d>> CollectWallSnapRingsInsideFloor(Database db, Polyline floorBoundary)
        {
            var result = new List<IList<Point2d>>();
            if (db == null || floorBoundary == null)
                return result;

            List<Point2d> floorRing = null;
            try { floorRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(floorBoundary); }
            catch { floorRing = null; }
            if (floorRing == null || floorRing.Count < 3)
                return result;

            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        if (id.IsErased) continue;
                        Polyline pl = null;
                        try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.WasErased) { continue; }
                        if (pl == null || !pl.Closed || pl.NumberOfVertices < 3)
                            continue;

                        string lay = (pl.Layer ?? string.Empty).Trim();
                        bool isRoomLayer =
                            string.Equals(lay, SprinklerLayers.McdRoomBoundaryLayer, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(lay, SprinklerLayers.McdFloorBoundaryLayer, StringComparison.OrdinalIgnoreCase);
                        if (!isRoomLayer)
                            continue;

                        List<Point2d> ring;
                        try { ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(pl); }
                        catch { continue; }
                        if (ring == null || ring.Count < 3)
                            continue;

                        // Only rooms/sub-areas inside the floor are relevant. Centroid-in-floor also excludes
                        // the outer floor polyline itself (its centroid is inside, but its walls duplicate the
                        // floor ring the engine already uses, which is harmless if it slips through).
                        Point2d c = PolygonUtils.ApproxCentroidAreaWeighted(ring);
                        if (!PolygonUtils.PointInPolygon(floorRing, c))
                            continue;

                        result.Add(ring);
                    }
                    tr.Commit();
                }
            }
            catch { /* best-effort snap candidates */ }

            return result;
        }

        private static List<FindShaftsInsideBoundary.ShaftBlockInfo> DedupeShaftBlocks(
            IList<FindShaftsInsideBoundary.ShaftBlockInfo> blocks,
            double tolerance)
        {
            var outList = new List<FindShaftsInsideBoundary.ShaftBlockInfo>();
            if (blocks == null) return outList;

            double tol = tolerance > 0 ? tolerance : 1e-6;
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                var p = new Point2d(b.Position.X, b.Position.Y);
                bool dup = false;
                for (int k = 0; k < outList.Count; k++)
                {
                    var q = new Point2d(outList[k].Position.X, outList[k].Position.Y);
                    if (p.GetDistanceTo(q) <= tol)
                    {
                        dup = true;
                        break;
                    }
                }

                if (!dup)
                    outList.Add(b);
            }

            return outList;
        }

        private static string FormatShaftCoords(System.Collections.Generic.IList<Autodesk.AutoCAD.Geometry.Point3d> points)
        {
            if (points == null || points.Count == 0)
                return string.Empty;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < points.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append('(');
                sb.Append(points[i].X.ToString("F2", CultureInfo.InvariantCulture));
                sb.Append(", ");
                sb.Append(points[i].Y.ToString("F2", CultureInfo.InvariantCulture));
                sb.Append(')');
            }
            return sb.ToString();
        }
    }
}
