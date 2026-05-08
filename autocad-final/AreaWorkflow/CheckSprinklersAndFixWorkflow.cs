using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;
using autocad_final.Validation;
using autocad_final.Workflows.Placement;

namespace autocad_final.AreaWorkflow
{
    internal static class CheckSprinklersAndFixWorkflow
    {
        private const double OffsetDrawingUnits = 1500.0;
        private const double GridSpacingDrawingUnits = 3000.0;

        internal static bool TryRun(Document doc, Polyline selectedZone, ObjectId boundaryEntityId, out string message)
        {
            message = null;
            if (doc == null || selectedZone == null)
            {
                message = "Invalid input.";
                return false;
            }

            var db = doc.Database;
            if (!BoundaryValidator.IsGlobalZoneBoundary(db, boundaryEntityId))
            {
                message = "Selected entity must be on layer \"" + SprinklerLayers.McdZoneBoundaryLayer +
                    "\" (or legacy \"" + SprinklerLayers.ZoneGlobalBoundaryLayer + "\").";
                return false;
            }

            if (!OffsetService.TryBuildInwardOffsetRing(selectedZone, OffsetDrawingUnits, out List<Point2d> offsetRing, out string offsetErr))
            {
                message = offsetErr;
                return false;
            }

            if (!TryRelayoutAllSprinklers(doc, selectedZone, offsetRing, out int removed, out int added, out string fixErr))
            {
                message = fixErr;
                return false;
            }

            message = "Re-layout complete. Removed " + removed + " sprinklers and placed " + added + " sprinklers.";
            return true;
        }

        private static bool TryRelayoutAllSprinklers(
            Document doc,
            Polyline selectedZone,
            List<Point2d> offsetRing,
            out int removedCount,
            out int addedCount,
            out string error)
        {
            removedCount = 0;
            addedCount = 0;
            error = null;
            if (doc == null || selectedZone == null || offsetRing == null || offsetRing.Count < 3)
            {
                error = "Invalid sprinkler re-layout context.";
                return false;
            }

            var db = doc.Database;
            var zoneRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(selectedZone);
            if (zoneRing == null || zoneRing.Count < 3)
            {
                error = "Selected boundary is invalid.";
                return false;
            }

            // Outer ring: place sprinklers exactly ON the inward offset boundary at 3000 spacing.
            var boundarySprinklers = BoundaryPlacementService.GenerateAlongRing(offsetRing, GridSpacingDrawingUnits);

            // Interior: grid intersections inside the offset ring (dynamic; still grid-aligned).
            PolygonUtils.GetBoundingBox(offsetRing, out double minBoundaryX, out double minBoundaryY, out _, out _);
            double baseOriginX = minBoundaryX;
            double baseOriginY = minBoundaryY;
            double half = GridSpacingDrawingUnits * 0.5;
            double[] ox = { baseOriginX, baseOriginX + half };
            double[] oy = { baseOriginY, baseOriginY + half };

            List<Point2d> bestInterior = null;
            double bestWorst = double.PositiveInfinity;
            for (int xi = 0; xi < ox.Length; xi++)
            {
                for (int yi = 0; yi < oy.Length; yi++)
                {
                    var candidates = GridPlacementService.GenerateInteriorLatticeCandidates(offsetRing, GridSpacingDrawingUnits, ox[xi], oy[yi]);
                    var filtered = PointFilter.FilterInsidePolygon(candidates, offsetRing, GridSpacingDrawingUnits);
                    if (filtered.Count == 0) continue;

                    double worst = WorstEdgeDistance(offsetRing, filtered);
                    if (worst < bestWorst)
                    {
                        bestWorst = worst;
                        bestInterior = filtered;
                    }
                }
            }

            var finalPoints = new List<Point2d>();
            double dedupeEps = GridSpacingDrawingUnits * 0.5;
            for (int i = 0; i < boundarySprinklers.Count; i++)
                if (!PolygonUtils.ContainsPoint(finalPoints, boundarySprinklers[i], dedupeEps))
                    finalPoints.Add(boundarySprinklers[i]);
            if (bestInterior != null)
            {
                for (int i = 0; i < bestInterior.Count; i++)
                    if (!PolygonUtils.ContainsPoint(finalPoints, bestInterior[i], dedupeEps))
                        finalPoints.Add(bestInterior[i]);
            }

            finalPoints = SprinklerShaftFootprintExclusion.RemovePointsInsideShaftFootprints(db, selectedZone, finalPoints);

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    // Erase existing sprinklers inside the selected zone (full re-layout).
                    foreach (ObjectId id in ms)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead, false) is BlockReference br))
                            continue;
                        if (!string.Equals(br.Layer, SprinklerLayers.ZoneLayer, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(br.Layer, SprinklerLayers.McdSprinklersLayer, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!SprinklerLayers.IsPendentSprinklerBlock(tr, br))
                            continue;

                        var p = new Point2d(br.Position.X, br.Position.Y);
                        if (!PolygonUtils.PointInPolygon(zoneRing, p))
                            continue;

                        br.UpgradeOpen();
                        br.Erase();
                        removedCount++;
                    }

                    // Insert new sprinklers (boundary-on-offset + interior grid).
                    if (!PendentSprinklerBlockInsert.TryGetBlockDefinitionId(tr, db, out ObjectId blockDefId, out string blockErr))
                    {
                        error = blockErr;
                        tr.Abort();
                        return false;
                    }

                    ObjectId layerId = SprinklerLayers.EnsureMcdSprinklersLayer(tr, db);

                    // Keep the inward offset boundary polyline visible for QA/debug.
                    try
                    {
                        var pl = CreateClosedPolyline(offsetRing, selectedZone.LayerId);
                        ms.AppendEntity(pl);
                        tr.AddNewlyCreatedDBObject(pl, true);
                    }
                    catch { /* ignore */ }

                    string boundaryHandleHex = null;
                    try { boundaryHandleHex = selectedZone.Handle.ToString(); } catch { /* ignore */ }

                    PendentSprinklerBlockInsert.AppendBlocksAtPoints(
                        tr, db, ms, selectedZone, finalPoints, blockDefId, layerId, boundaryHandleHex,
                        rotationRadians: Math.PI * 0.5);
                    addedCount = finalPoints.Count;

                    tr.Commit();
                    return true;
                }
                catch (System.Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
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

        private static Polyline CreateClosedPolyline(List<Point2d> ring, ObjectId layerId)
        {
            var pl = new Polyline(ring.Count);
            for (int i = 0; i < ring.Count; i++)
                pl.AddVertexAt(i, ring[i], 0, 0, 0);
            pl.Closed = true;
            pl.LayerId = layerId;
            return pl;
        }

    }
}
