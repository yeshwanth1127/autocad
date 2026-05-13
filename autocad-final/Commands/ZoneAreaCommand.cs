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
            EqualAreaBisection
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
                mode == ZoningMode.ShaftMidlineStrips;

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
                if (!EqualAreaAxisStripZonesInPolygon2d.TryBuildZoneRings(
                        boundary,
                        sites,
                        n,
                        tol,
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
