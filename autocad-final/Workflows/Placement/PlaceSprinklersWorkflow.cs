using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Agent;
using autocad_final.AreaWorkflow;
using autocad_final.Blocks;
using autocad_final.Geometry;
using autocad_final.Validation;

namespace autocad_final.Workflows.Placement
{
    public static class PlaceSprinklersWorkflow
    {
        private const long MaxLatticeCells = 350_000;

        public static bool TryRun(Document doc, Polyline sourceZone, ObjectId boundaryEntityId, out string message)
        {
            message = null;
            AgentLog.Write("PlaceSprinklersWorkflow", "TryRun enter boundaryId=" + boundaryEntityId.ToString());
            if (doc == null || sourceZone == null)
            {
                message = "Invalid boundary input.";
                return false;
            }

            if (!BoundaryValidator.IsMcdFloorBoundary(doc.Database, boundaryEntityId))
            {
                message = "Selected entity must be on layer \"" + SprinklerLayers.McdFloorBoundaryLayer + "\".";
                return false;
            }

            // Sprinkler placement rule (per user requirement):
            // Treat 1 drawing unit as 1 meter. Do not attempt to infer/convert scale from INSUNITS.
            double extentHintDu = 0;
            try
            {
                var ext = sourceZone.GeometricExtents;
                extentHintDu = System.Math.Max(ext.MaxPoint.X - ext.MinPoint.X, ext.MaxPoint.Y - ext.MinPoint.Y);
            }
            catch
            {
                extentHintDu = 0;
            }

            double duPerMeter = 1.0;

            var cfg = RuntimeSettings.Load();
            double offsetDu = cfg.SprinklerToBoundaryDistanceM * duPerMeter;
            double spacing = cfg.SprinklerSpacingM * duPerMeter;
            AgentLog.Write("PlaceSprinklersWorkflow",
                "extentDu=" + extentHintDu.ToString("G6", CultureInfo.InvariantCulture) +
                " duPerM=" + duPerMeter.ToString("G6", CultureInfo.InvariantCulture) +
                " offsetDu=" + offsetDu.ToString("G6", CultureInfo.InvariantCulture) +
                " spacingDu=" + spacing.ToString("G6", CultureInfo.InvariantCulture));

            if (!(spacing > 1e-9) || !(offsetDu > 1e-9))
            {
                message =
                    "Place sprinklers: computed grid spacing or offset is effectively zero (check INSUNITS / drawing scale). " +
                    "INSUNITS=" + DrawingUnitsHelper.InsunitsLabel(doc.Database) + ".";
                AgentLog.Write("PlaceSprinklersWorkflow", "zero spacing or offset");
                return false;
            }

            // Reject absurd scales before building a huge lattice (would freeze AutoCAD).
            if (extentHintDu > 0)
            {
                long estCells = (long)Math.Ceiling(extentHintDu / spacing);
                estCells = estCells * estCells;
                if (estCells > MaxLatticeCells)
                {
                    message =
                        "Place sprinklers: grid would have too many cells for this boundary (scale may be wrong). " +
                        "INSUNITS=" + DrawingUnitsHelper.InsunitsLabel(doc.Database) +
                        ", spacing≈" + spacing.ToString("G6", System.Globalization.CultureInfo.InvariantCulture) + " DU.";
                    AgentLog.Write("PlaceSprinklersWorkflow", "too many estCells=" + estCells.ToString(CultureInfo.InvariantCulture));
                    return false;
                }
            }

            AgentLog.Write("PlaceSprinklersWorkflow", "OffsetService.TryBuildInwardOffsetRing start");
            if (!OffsetService.TryBuildInwardOffsetRing(
                    sourceZone,
                    offsetDu,
                    out List<Point2d> offsetRing,
                    out string offsetErr))
            {
                message = offsetErr;
                AgentLog.Write("PlaceSprinklersWorkflow", "offset fail: " + offsetErr);
                return false;
            }
            AgentLog.Write("PlaceSprinklersWorkflow", "offset ok verts=" + offsetRing.Count.ToString());

            // Anchor the grid to the OFFSET boundary itself:
            // use the offset-ring bounding box corner as the reference origin, then build the grid inward from it.
            PolygonUtils.GetBoundingBox(offsetRing, out double minBoundaryX, out double minBoundaryY, out _, out _);
            double baseOriginX = minBoundaryX;
            double baseOriginY = minBoundaryY;

            // If the boundary is slightly out-of-phase with the grid, a strict origin can produce
            // rows/columns >1500 away from straight edges. Try half-cell shifts and pick the best.
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

                    AgentLog.Write("PlaceSprinklersWorkflow", "phase xi=" + xi.ToString() + " yi=" + yi.ToString() + " origin=(" + gridOriginX.ToString("G6", CultureInfo.InvariantCulture) + "," + gridOriginY.ToString("G6", CultureInfo.InvariantCulture) + ")");
                    var gridCandidates = GridPlacementService.GenerateInteriorLatticeCandidates(offsetRing, spacing, gridOriginX, gridOriginY);
                    AgentLog.Write("PlaceSprinklersWorkflow", "lattice candidates=" + gridCandidates.Count.ToString());
                    if (gridCandidates.Count == 0)
                    {
                        latticeTooDense = true;
                        continue;
                    }
                    AgentLog.Write("PlaceSprinklersWorkflow", "FilterInsidePolygon start");
                    var filteredGrid = PointFilter.FilterInsidePolygon(gridCandidates, offsetRing, spacing);
                    AgentLog.Write("PlaceSprinklersWorkflow", "FilterInsidePolygon done inside=" + filteredGrid.Count.ToString());
                    if (filteredGrid.Count == 0)
                        continue;

                    AgentLog.Write("PlaceSprinklersWorkflow", "EnsureBoundaryCoverage start");
                    var finalPoints = BoundaryCoverageService.EnsureBoundaryCoverage(
                        filteredGrid,
                        offsetRing,
                        filteredGrid,
                        spacing,
                        offsetDu,
                        gridOriginX,
                        gridOriginY);
                    AgentLog.Write("PlaceSprinklersWorkflow", "EnsureBoundaryCoverage done count=" + finalPoints.Count.ToString());

                    // Snap every point back to the exact lattice to avoid float drift,
                    // then re-filter strictly inside the offset ring (never keep outside points).
                    finalPoints = SnapToGrid(finalPoints, gridOriginX, gridOriginY, spacing);
                    finalPoints = PointFilter.FilterInsidePolygon(finalPoints, offsetRing, spacing);
                    AgentLog.Write("PlaceSprinklersWorkflow", "after snap+filter count=" + finalPoints.Count.ToString());

                    AgentLog.Write("PlaceSprinklersWorkflow", "WorstEdgeDistance start ringN=" + offsetRing.Count.ToString() + " pts=" + finalPoints.Count.ToString());
                    double worst = WorstEdgeDistance(offsetRing, finalPoints);
                    AgentLog.Write("PlaceSprinklersWorkflow", "WorstEdgeDistance done worst=" + worst.ToString("G6", CultureInfo.InvariantCulture));
                    if (worst < bestWorstEdgeDist)
                    {
                        bestWorstEdgeDist = worst;
                        bestFinal = finalPoints;
                    }
                }
            }

            if (bestFinal == null && latticeTooDense)
            {
                message =
                    "Place sprinklers: lattice is too dense for this floor (bbox vs grid spacing). " +
                    "Check INSUNITS or reduce drawing extent; limit is about " +
                    MaxLatticeCells.ToString(System.Globalization.CultureInfo.InvariantCulture) + " cells.";
                AgentLog.Write("PlaceSprinklersWorkflow", "fail latticeTooDense");
                return false;
            }

            var finalPointsChosen = bestFinal ?? new List<Point2d>();
            int beforeExclusions = finalPointsChosen.Count;

            // Exclude shaft footprints (same rule as used elsewhere).
            var afterShafts = SprinklerShaftFootprintExclusion.RemovePointsInsideShaftFootprints(
                doc.Database,
                sourceZone,
                finalPointsChosen);
            int removedByShafts = Math.Max(0, beforeExclusions - (afterShafts?.Count ?? 0));

            // Exclude "rooms" (closed polylines with a label inside whose room-type is excluded).
            var afterRooms = SprinklerRoomFootprintExclusion.RemovePointsInsideExcludedRooms(
                doc.Database,
                sourceZone,
                afterShafts ?? new List<Point2d>(),
                out int excludedRoomCount,
                out int removedByRooms);

            AgentLog.Write(
                "PlaceSprinklersWorkflow",
                "chosen points=" + beforeExclusions.ToString(CultureInfo.InvariantCulture) +
                " after shaft exclusion=" + (afterShafts?.Count ?? 0).ToString(CultureInfo.InvariantCulture) +
                " removedByShafts=" + removedByShafts.ToString(CultureInfo.InvariantCulture) +
                " after room exclusion=" + (afterRooms?.Count ?? 0).ToString(CultureInfo.InvariantCulture) +
                " excludedRooms=" + excludedRoomCount.ToString(CultureInfo.InvariantCulture) +
                " removedByRooms=" + removedByRooms.ToString(CultureInfo.InvariantCulture) +
                " TryInsert start");

            if (!SprinklerBlockService.TryInsertSprinklersForOffsetPlacement(
                //this method created sprinkklers on the finalised grid points
                    doc,
                    sourceZone,
                    offsetRing,
                    afterRooms ?? new List<Point2d>(),
                    out string insertErr))
            {
                message = insertErr;
                AgentLog.Write("PlaceSprinklersWorkflow", "TryInsert fail: " + insertErr);
                return false;
            }

            AgentLog.Write("PlaceSprinklersWorkflow", "TryInsert ok");
            message =
                "Internal grid sprinklers placed. " +
                "Excluded " + removedByShafts.ToString(CultureInfo.InvariantCulture) + " inside shafts, " +
                removedByRooms.ToString(CultureInfo.InvariantCulture) + " inside " +
                excludedRoomCount.ToString(CultureInfo.InvariantCulture) + " rooms.";
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
                int ix = (int)System.Math.Round((p.X - originX) / spacing);
                int iy = (int)System.Math.Round((p.Y - originY) / spacing);
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
