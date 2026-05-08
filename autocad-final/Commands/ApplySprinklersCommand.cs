using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using autocad_final.Licensing;
using autocad_final.Agent;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;
using autocad_final.UI;
using System.Windows.Forms;
using System.Collections.Generic;

namespace autocad_final.Commands
{
    public class ApplySprinklersCommand
    {
        [CommandMethod("SPRINKLERAPPLYSPRINKLERS", CommandFlags.Modal)]
        public void ApplySprinklers()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;
            var db = doc.Database;

            if (!SelectPolygonBoundary.TrySelect(ed, out var zone, out ObjectId boundaryEntityId))
                return;

            string boundaryHandleHex;
            using (var tr0 = db.TransactionManager.StartTransaction())
            {
                SprinklerXData.EnsureRegApp(tr0, db);
                var dbo = tr0.GetObject(boundaryEntityId, OpenMode.ForRead);
                boundaryHandleHex = dbo.Handle.ToString();
                tr0.Commit();
            }

            try
            {
                if (!TryApplySprinklersForZone(doc, zone, boundaryHandleHex, out string applyMsg, useTrunkAnchoredGrid: false))
                {
                    if (!string.IsNullOrEmpty(applyMsg))
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, applyMsg, MessageBoxIcon.Warning);
                    return;
                }

                ed.WriteMessage("\n" + applyMsg + "\n");
                try { ed.Regen(); } catch { /* ignore */ }
            }
            finally
            {
                try { zone.Dispose(); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Places sprinkler blocks for the zone when main pipe is already routed inside it.
        /// When <paramref name="useTrunkAnchoredGrid"/> is true, uses the same trunk-anchored grid as
        /// <c>SPRINKLERREBUILDFROMTRUNK</c> so rows/columns follow the tagged main pipe (including after manual trunk moves).
        /// </summary>
        internal static bool TryApplySprinklersForZone(
            Document doc,
            Polyline zone,
            string boundaryHandleHex,
            out string message,
            bool useTrunkAnchoredGrid = false,
            double? spacingMeters = null,
            double coverageRadiusMeters = 1.5,
            double? maxBoundarySprinklerGapMeters = null,
            double gridAnchorOffsetXMeters = 0,
            double gridAnchorOffsetYMeters = 0,
            List<Point2d> placementRingOverride = null,
            bool requireMainPipeForCenteredGrid = true,
            bool alignGridToBoundary = false)
        {
            message = null;
            if (doc == null || zone == null)
            {
                message = "Invalid inputs.";
                return false;
            }

            var cfg = RuntimeSettings.Load();
            double spacingUse = spacingMeters ?? cfg.SprinklerSpacingM;
            double gapUse = maxBoundarySprinklerGapMeters ?? cfg.SprinklerToBoundaryDistanceM;

            var db = doc.Database;
            var ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zone);
            var placementRing = (placementRingOverride != null && placementRingOverride.Count >= 3)
                ? placementRingOverride
                : ring;

            SprinklerGridPlacement2d.PlacementResult placement;
            if (useTrunkAnchoredGrid)
            {
                if (!SprinklerTrunkLocator.TryFindTaggedTrunkInZone(db, ring, out ObjectId trunkId, out string trunkErr))
                {
                    message = trunkErr ?? "Could not find tagged main trunk in zone.";
                    return false;
                }

                if (!SprinklerTrunkLocator.TryReadStraightAxisAlignedTrunkInfo(db, trunkId, out bool trunkHorizontal, out double trunkAxis, out string infoErr))
                {
                    message = infoErr ?? "Could not read main trunk axis.";
                    return false;
                }

                if (!SprinklerGridPlacement2d.TryPlaceForZoneRingAnchoredToTrunk(
                        ring,
                        db,
                        spacingMeters: spacingUse,
                        coverageRadiusMeters: coverageRadiusMeters,
                        maxBoundarySprinklerGapMeters: gapUse,
                        trunkHorizontal: trunkHorizontal,
                        trunkAxis: trunkAxis,
                        out placement,
                        out string anchoredErr,
                        gridAnchorOffsetXMeters: gridAnchorOffsetXMeters,
                        gridAnchorOffsetYMeters: gridAnchorOffsetYMeters))
                {
                    message = anchoredErr;
                    return false;
                }
            }
            else
            {
                if (requireMainPipeForCenteredGrid && !TryCheckMainPipeRoutedInZone(db, zone, ring, out string pipeErr))
                {
                    message = pipeErr;
                    return false;
                }

                if (!SprinklerGridPlacement2d.TryPlaceForZoneRing(
                        placementRing,
                        db,
                        spacingMeters: spacingUse,
                        coverageRadiusMeters: coverageRadiusMeters,
                        maxBoundarySprinklerGapMeters: gapUse,
                        out placement,
                        out string err,
                        gridAnchorOffsetXMeters: gridAnchorOffsetXMeters,
                        gridAnchorOffsetYMeters: gridAnchorOffsetYMeters,
                        alignGridToBoundary: alignGridToBoundary))
                {
                    message = err;
                    return false;
                }
            }

            placement.Sprinklers = SprinklerShaftFootprintExclusion.RemovePointsInsideShaftFootprints(db, zone, placement.Sprinklers);

            ObjectId pendentSprinklerBlockId;
            using (var trBlock = db.TransactionManager.StartTransaction())
            {
                if (!PendentSprinklerBlockInsert.TryGetBlockDefinitionId(trBlock, db, out pendentSprinklerBlockId, out string blockLookupErr))
                {
                    message = blockLookupErr;
                    trBlock.Commit();
                    return false;
                }

                trBlock.Commit();
            }

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                ObjectId sprinklerLayerId = SprinklerLayers.EnsureMcdSprinklersLayer(tr, db);
                ObjectId legacyUnifiedZoneLayerId = SprinklerLayers.EnsureZoneLayer(tr, db);

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Clear prior sprinklers for this zone so re-running doesn't duplicate symbols.
                SprinklerXData.EnsureRegApp(tr, db);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
                        continue;
                    // Use LayerId comparison to avoid ent.Layer throwing eInvalidLayer for
                    // entities that have a corrupt/xref-dependent layer reference.
                    ObjectId entLayerId;
                    try { entLayerId = ent.LayerId; }
                    catch { continue; }
                    if (entLayerId != sprinklerLayerId && entLayerId != legacyUnifiedZoneLayerId)
                        continue;
                    if (!SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                        continue;

                    bool isInThisZone =
                        (SprinklerXData.TryGetZoneBoundaryHandle(ent, out var h) &&
                         string.Equals(h, boundaryHandleHex, StringComparison.OrdinalIgnoreCase));

                    // Backward compat / manual edits: also clear sprinkler markers that are geometrically inside the zone,
                    // even if they were created before zone tagging existed or have a different tag.
                    if (!isInThisZone)
                    {
                        try
                        {
                            if (ent is BlockReference br)
                            {
                                var pos = br.Position;
                                isInThisZone = PointInPolygon(ring, new Point2d(pos.X, pos.Y));
                            }
                            else if (ent is Circle c)
                            {
                                isInThisZone = PointInPolygon(ring, new Point2d(c.Center.X, c.Center.Y));
                            }
                        }
                        catch { /* ignore */ }
                    }

                    if (isInThisZone)
                    {
                        ent.UpgradeOpen();
                        try { ent.Erase(); } catch { /* ignore */ }
                    }
                }

                // Single head per grid point (rotated 90°).
                PendentSprinklerBlockInsert.AppendBlocksAtPoints(
                    tr,
                    db,
                    ms,
                    zone,
                    placement.Sprinklers,
                    pendentSprinklerBlockId,
                    sprinklerLayerId,
                    boundaryHandleHex,
                    rotationRadians: 0.0);

                tr.Commit();
            }

            message =
                "Sprinklers applied: " + placement.Sprinklers.Count.ToString() +
                ". " + (placement.Summary ?? string.Empty) +
                (placement.CoverageOk ? "" : " (Coverage check: NOT OK)");
            return true;
        }

        private static bool TryCheckMainPipeRoutedInZone(
            Database db,
            Polyline zone,
            List<Point2d> zoneRing,
            out string err)
        {
            err = null;
            if (db == null || zone == null || zoneRing == null || zoneRing.Count < 3)
            {
                err = "Invalid zone boundary.";
                return false;
            }

            double tolDu = 1.0;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.05, out double t) && t > 0)
                    tolDu = t;
            }
            catch { /* ignore */ }

            double stepDu = 1.0;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.50, out double s) && s > 0)
                    stepDu = s;
            }
            catch { /* ignore */ }

            int foundOnLayer = 0;
            int foundInside = 0;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
                        continue;

                    if (!SprinklerLayers.IsMainPipeLayerName(ent.Layer))
                        continue;

                    // "Main pipe is routed in the zone" = at least one sampled point of a main-pipe entity
                    // is inside (or on) the selected zone boundary.
                    foundOnLayer++;

                    if (ent is Polyline pl)
                    {
                        if (PolylineHasSampleInsideZone(pl, zoneRing, tolDu, stepDu))
                        {
                            foundInside++;
                            break;
                        }
                    }
                    else if (ent is Line ln)
                    {
                        if (LineHasSampleInsideZone(ln, zoneRing, tolDu))
                        {
                            foundInside++;
                            break;
                        }
                    }
                }

                tr.Commit();
            }

            if (foundInside > 0)
                return true;

            if (foundOnLayer == 0)
            {
                err =
                    "No main pipe found. Please run Route main pipe first (SPRINKLERROUTEMAINPIPE), then apply sprinklers.";
                return false;
            }

            err =
                "Main pipe was found, but not inside the selected zone. Please route the main pipe for this zone, then apply sprinklers.";
            return false;
        }

        private static bool PolylineHasSampleInsideZone(Polyline pl, List<Point2d> zoneRing, double tolDu, double stepDu)
        {
            if (pl == null || zoneRing == null || zoneRing.Count < 3)
                return false;

            // Fast checks: vertices + midpoints
            try
            {
                int n = pl.NumberOfVertices;
                for (int i = 0; i < n; i++)
                {
                    var v = pl.GetPoint3dAt(i);
                    if (IsInsideOrOnBoundary(zoneRing, new Point2d(v.X, v.Y), tolDu))
                        return true;

                    int j = (i + 1) % n;
                    if (!pl.Closed && i == n - 1) break;
                    if (j < n)
                    {
                        var v2 = pl.GetPoint3dAt(j);
                        var mid = new Point2d((v.X + v2.X) * 0.5, (v.Y + v2.Y) * 0.5);
                        if (IsInsideOrOnBoundary(zoneRing, mid, tolDu))
                            return true;
                    }
                }
            }
            catch
            {
                // fall through to distance sampling
            }

            // Robust sampling along length (catches "passes through zone with endpoints outside")
            try
            {
                double len = pl.Length;
                if (!(len > 0) || double.IsNaN(len) || double.IsInfinity(len))
                    return false;

                double step = stepDu > 0 ? stepDu : (len / 50.0);
                if (step <= 0) step = 1.0;

                for (double d = 0.0; d <= len; d += step)
                {
                    var p3 = pl.GetPointAtDist(d);
                    if (IsInsideOrOnBoundary(zoneRing, new Point2d(p3.X, p3.Y), tolDu))
                        return true;
                }

                // ensure end sampled
                var end = pl.GetPointAtDist(len);
                return IsInsideOrOnBoundary(zoneRing, new Point2d(end.X, end.Y), tolDu);
            }
            catch
            {
                return false;
            }
        }

        private static bool LineHasSampleInsideZone(Line ln, List<Point2d> zoneRing, double tolDu)
        {
            if (ln == null || zoneRing == null || zoneRing.Count < 3)
                return false;

            var a = ln.StartPoint;
            var b = ln.EndPoint;
            var p0 = new Point2d(a.X, a.Y);
            var p1 = new Point2d(b.X, b.Y);
            var pm = new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);

            return
                IsInsideOrOnBoundary(zoneRing, p0, tolDu) ||
                IsInsideOrOnBoundary(zoneRing, pm, tolDu) ||
                IsInsideOrOnBoundary(zoneRing, p1, tolDu);
        }

        private static bool IsInsideOrOnBoundary(List<Point2d> ring, Point2d p, double tolDu)
        {
            if (PointInPolygon(ring, p))
                return true;

            // Treat boundary as "inside" within tolerance.
            double d2 = MinDistSquaredToRingEdges(ring, p);
            return d2 <= (tolDu * tolDu);
        }

        private static bool PointInPolygon(List<Point2d> ring, Point2d p)
        {
            // Ray casting; boundary handled separately by IsInsideOrOnBoundary.
            bool inside = false;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                bool intersect =
                    ((a.Y > p.Y) != (b.Y > p.Y)) &&
                    (p.X < (b.X - a.X) * (p.Y - a.Y) / ((b.Y - a.Y) == 0 ? 1e-12 : (b.Y - a.Y)) + a.X);
                if (intersect) inside = !inside;
            }

            return inside;
        }

        private static double MinDistSquaredToRingEdges(List<Point2d> ring, Point2d p)
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
    }
}

