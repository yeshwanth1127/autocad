using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;
using autocad_final.Validation;

namespace autocad_final.AreaWorkflow
{
    internal static class FixOnSlantWorkflow
    {
        private const double OffsetDrawingUnits = 1500.0;
        private const double BoundaryTolerance = 150.0;
        private const double OrthogonalTolerance = 10.0;
        private const double NeighborDistanceThreshold = 3500.0;

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

            if (!TryPruneSlantBoundarySprinklers(doc, selectedZone, offsetRing, out int removed, out string pruneErr))
            {
                message = pruneErr;
                return false;
            }

            message = "Fix on slant complete. Removed " + removed + " isolated slant-boundary sprinklers.";
            return true;
        }

        private static bool TryPruneSlantBoundarySprinklers(
            Document doc,
            Polyline selectedZone,
            List<Point2d> offsetRing,
            out int removedCount,
            out string error)
        {
            removedCount = 0;
            error = null;
            if (doc == null || selectedZone == null || offsetRing == null || offsetRing.Count < 3)
            {
                error = "Invalid slant-fix context.";
                return false;
            }

            var zoneRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(selectedZone);
            if (zoneRing == null || zoneRing.Count < 3)
            {
                error = "Selected boundary is invalid.";
                return false;
            }

            var slantedSegments = BuildSlantedSegments(offsetRing);
            if (slantedSegments.Count == 0)
                return true;

            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    var allSprinklers = new List<(BlockReference br, Point2d p)>();
                    var slantBoundarySprinklers = new List<(BlockReference br, Point2d p)>();

                    foreach (ObjectId id in ms)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead, false) is BlockReference br))
                            continue;
                        if (!string.Equals(br.Layer, SprinklerLayers.ZoneLayer, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!SprinklerLayers.IsPendentSprinklerBlock(tr, br))
                            continue;

                        var p = new Point2d(br.Position.X, br.Position.Y);
                        if (!PolygonUtils.PointInPolygon(zoneRing, p))
                            continue;

                        allSprinklers.Add((br, p));
                        if (DistanceToSegments(slantedSegments, p) <= BoundaryTolerance)
                            slantBoundarySprinklers.Add((br, p));
                    }

                    for (int i = 0; i < slantBoundarySprinklers.Count; i++)
                    {
                        var candidate = slantBoundarySprinklers[i];
                        if (!HasNeighbor(allSprinklers, candidate.p))
                        {
                            candidate.br.UpgradeOpen();
                            candidate.br.Erase();
                            removedCount++;
                        }
                    }

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

        private static bool HasNeighbor(List<(BlockReference br, Point2d p)> allSprinklers, Point2d origin)
        {
            for (int i = 0; i < allSprinklers.Count; i++)
            {
                var p = allSprinklers[i].p;
                if (origin.GetDistanceTo(p) <= 1e-6)
                    continue;
                if (origin.GetDistanceTo(p) <= NeighborDistanceThreshold)
                    return true;
            }
            return false;
        }

        private static List<(Point2d a, Point2d b)> BuildSlantedSegments(List<Point2d> ring)
        {
            var segments = new List<(Point2d a, Point2d b)>();
            if (ring == null || ring.Count < 3)
                return segments;

            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var a = ring[j];
                var b = ring[i];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                bool horizontal = Math.Abs(dy) <= OrthogonalTolerance;
                bool vertical = Math.Abs(dx) <= OrthogonalTolerance;
                if (!horizontal && !vertical)
                    segments.Add((a, b));
            }

            return segments;
        }

        private static double DistanceToSegments(List<(Point2d a, Point2d b)> segments, Point2d p)
        {
            double best = double.PositiveInfinity;
            for (int i = 0; i < segments.Count; i++)
            {
                double d = PolygonUtils.DistancePointToSegment(p, segments[i].a, segments[i].b);
                if (d < best) best = d;
            }
            return best;
        }

    }
}
