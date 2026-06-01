using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using autocad_final.Agent;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;

namespace autocad_final.Commands
{
    /// <summary>
    /// ROUTEBRANCHES2: Select a shaft, detect its zone, detect mains + sprinklers in that zone,
    /// then route strictly world-X/Y orthogonal branches by rows/columns with a fallback spine.
    /// </summary>
    public class RouteBranches2Command
    {
        [CommandMethod("ROUTEBRANCHES2", CommandFlags.Modal)]
        public void Execute()
        {
            RouteBranches2();
        }

        public void RouteBranches2()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var db = doc.Database;
            var ed = doc.Editor;

            var peo = new PromptEntityOptions("\nSelect shaft: ");
            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nNo shaft selected.");
                return;
            }

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                SprinklerXData.EnsureRegApp(tr, db);

                if (!TryGetEntityCenter(tr, per.ObjectId, out var shaftCenter2d))
                {
                    ed.WriteMessage("\nCould not read shaft entity center.");
                    tr.Commit();
                    return;
                }

                if (!TryFindContainingZone(tr, db, shaftCenter2d, out var zoneBoundary, out var zoneRing, out var zoneBoundaryHex, out var zoneErr))
                {
                    ed.WriteMessage("\n" + (zoneErr ?? "Shaft is not inside any zone boundary."));
                    tr.Commit();
                    return;
                }

                if (!TryCollectSprinklersInZone(tr, db, zoneRing, out var sprinklers))
                {
                    ed.WriteMessage("\nNo sprinklers found in this zone.");
                    tr.Commit();
                    return;
                }

                if (!TryCollectMainSegmentsInZone(tr, db, zoneRing, out var mainSegments))
                {
                    ed.WriteMessage("\nNo main pipe found in this zone.");
                    tr.Commit();
                    return;
                }

                // Plan routes (pure geometry), then draw.
                var tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
                double spacingDu = TryGetSprinklerSpacingDu(db, fallback: 1.0);
                double clusterTol = Math.Max(tol * 10.0, spacingDu * 0.35);

                var plan = RouteBranches2d.Plan(
                    zoneRing: zoneRing,
                    sprinklers: sprinklers,
                    mains: mainSegments,
                    clusterTol: clusterTol);

                if (plan == null || plan.Paths == null || plan.Paths.Count == 0)
                {
                    ed.WriteMessage("\nNo branches could be planned for this zone.");
                    tr.Commit();
                    return;
                }

                // Draw polylines.
                var ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                ObjectId branchLayerId = SprinklerLayers.EnsureMcdBranchPipeLayer(tr, db);
                double mainW = NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
                if (!(mainW > 0)) mainW = 1.0;
                double branchW = Math.Max(mainW * 0.66, 1.0);

                int drawn = 0;
                foreach (var path in plan.Paths)
                {
                    if (path == null || path.Count < 2) continue;
                    if (!RouteBranches2d.IsAxisAlignedPath(path)) continue;

                    var pl = new Polyline();
                    pl.SetDatabaseDefaults(db);
                    pl.LayerId = branchLayerId;
                    pl.ConstantWidth = branchW;
                    pl.Elevation = zoneBoundary?.Elevation ?? 0.0;
                    pl.Closed = false;

                    for (int i = 0; i < path.Count; i++)
                        pl.AddVertexAt(i, path[i], 0, 0, 0);

                    if (!string.IsNullOrWhiteSpace(zoneBoundaryHex))
                        SprinklerXData.ApplyZoneBoundaryTag(pl, zoneBoundaryHex.Trim());

                    ms.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    drawn++;
                }

                ed.WriteMessage($"\nRoute branches 2: drew {drawn} branch polylines.");
                tr.Commit();
            }
        }

        private static bool TryGetEntityCenter(Transaction tr, ObjectId id, out Point2d center)
        {
            center = default;
            if (tr == null || id.IsNull) return false;

            Entity ent = null;
            try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { }
            if (ent == null) return false;

            if (ent is Circle c)
            {
                center = new Point2d(c.Center.X, c.Center.Y);
                return true;
            }
            if (ent is BlockReference br)
            {
                center = new Point2d(br.Position.X, br.Position.Y);
                return true;
            }
            if (ent is Polyline pl && pl.Bounds.HasValue)
            {
                var b = pl.Bounds.Value;
                center = new Point2d((b.MinPoint.X + b.MaxPoint.X) * 0.5, (b.MinPoint.Y + b.MaxPoint.Y) * 0.5);
                return true;
            }

            // Fallback: geometric extents for most entities.
            try
            {
                var e = ent.GeometricExtents;
                center = new Point2d((e.MinPoint.X + e.MaxPoint.X) * 0.5, (e.MinPoint.Y + e.MaxPoint.Y) * 0.5);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryFindContainingZone(
            Transaction tr,
            Database db,
            Point2d pt,
            out Polyline zoneBoundary,
            out List<Point2d> zoneRing,
            out string zoneBoundaryHex,
            out string errorMessage)
        {
            zoneBoundary = null;
            zoneRing = null;
            zoneBoundaryHex = null;
            errorMessage = null;

            if (tr == null || db == null)
            {
                errorMessage = "No active database.";
                return false;
            }

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            Polyline best = null;
            List<Point2d> bestRing = null;
            double bestArea = double.PositiveInfinity;

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; } catch { continue; }
                if (pl == null || !pl.Closed) continue;

                bool isZoneLayer = SprinklerLayers.IsUnifiedZoneDesignLayerName(pl.Layer) || SprinklerLayers.IsMcdZoneOutlineLayerName(pl.Layer);
                bool selfTagged =
                    SprinklerXData.TryGetZoneBoundaryHandle(pl, out var h) &&
                    !string.IsNullOrWhiteSpace(h) &&
                    string.Equals(h.Trim(), pl.Handle.ToString(), StringComparison.OrdinalIgnoreCase);
                if (!isZoneLayer && !selfTagged) continue;

                var ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(pl);
                if (ring == null || ring.Count < 3) continue;
                if (!PolygonUtils.PointInPolygon(ring, pt)) continue;

                double area;
                try { area = Math.Abs(pl.Area); } catch { area = double.PositiveInfinity; }
                if (!(area > 0)) area = double.PositiveInfinity;
                if (area < bestArea)
                {
                    best = pl;
                    bestRing = ring;
                    bestArea = area;
                }
            }

            if (best == null || bestRing == null)
            {
                errorMessage = "Shaft is not inside any zone boundary.";
                return false;
            }

            zoneBoundary = best;
            zoneRing = bestRing;
            if (SprinklerXData.TryGetZoneBoundaryHandle(best, out var z))
                zoneBoundaryHex = z;
            if (string.IsNullOrWhiteSpace(zoneBoundaryHex))
                zoneBoundaryHex = best.Handle.ToString();

            return true;
        }

        private static bool TryCollectSprinklersInZone(
            Transaction tr,
            Database db,
            List<Point2d> zoneRing,
            out List<Point2d> sprinklers)
        {
            sprinklers = new List<Point2d>();
            if (tr == null || db == null || zoneRing == null || zoneRing.Count < 3) return false;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { continue; }
                if (ent == null) continue;

                if (!SprinklerLayers.IsSprinklerHeadEntity(tr, ent)) continue;

                Point2d p;
                if (ent is Circle c) p = new Point2d(c.Center.X, c.Center.Y);
                else if (ent is BlockReference br) p = new Point2d(br.Position.X, br.Position.Y);
                else continue;

                if (!PolygonUtils.PointInPolygon(zoneRing, p)) continue; // strict inside
                sprinklers.Add(p);
            }

            return sprinklers.Count > 0;
        }

        private static bool TryCollectMainSegmentsInZone(
            Transaction tr,
            Database db,
            List<Point2d> zoneRing,
            out List<RouteBranches2d.Segment2d> segments)
        {
            segments = new List<RouteBranches2d.Segment2d>();
            if (tr == null || db == null || zoneRing == null || zoneRing.Count < 3) return false;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { continue; }
                if (ent == null) continue;

                if (!SprinklerLayers.IsMainPipeLayerName(ent.Layer)) continue;
                if (SprinklerXData.IsTaggedTrunkCap(ent)) continue;
                if (SprinklerXData.IsTaggedConnector(ent)) continue;

                if (!IsEntitySubstantiallyInsideZone(ent, zoneRing, minOverlapFraction: 0.60))
                    continue;

                if (ent is Line line)
                {
                    var a = new Point2d(line.StartPoint.X, line.StartPoint.Y);
                    var b = new Point2d(line.EndPoint.X, line.EndPoint.Y);
                    if (a.GetDistanceTo(b) > 1e-6)
                        segments.Add(new RouteBranches2d.Segment2d(a, b));
                }
                else if (ent is Polyline pl)
                {
                    for (int i = 0; i < pl.NumberOfVertices - 1; i++)
                    {
                        var a = pl.GetPoint2dAt(i);
                        var b = pl.GetPoint2dAt(i + 1);
                        if (a.GetDistanceTo(b) > 1e-6)
                            segments.Add(new RouteBranches2d.Segment2d(a, b));
                    }
                    if (pl.Closed && pl.NumberOfVertices > 1)
                    {
                        var a = pl.GetPoint2dAt(pl.NumberOfVertices - 1);
                        var b = pl.GetPoint2dAt(0);
                        if (a.GetDistanceTo(b) > 1e-6)
                            segments.Add(new RouteBranches2d.Segment2d(a, b));
                    }
                }
            }

            // Remove degenerate.
            segments = segments.Where(s => s.Length > 1e-6).ToList();
            return segments.Count > 0;
        }

        private static bool IsEntitySubstantiallyInsideZone(Entity ent, List<Point2d> zoneRing, double minOverlapFraction)
        {
            if (ent == null || zoneRing == null || zoneRing.Count < 3) return false;
            if (!(minOverlapFraction > 0 && minOverlapFraction <= 1)) minOverlapFraction = 0.60;

            var samples = new List<(Point2d a, Point2d b)>();
            if (ent is Line line)
            {
                samples.Add((new Point2d(line.StartPoint.X, line.StartPoint.Y), new Point2d(line.EndPoint.X, line.EndPoint.Y)));
            }
            else if (ent is Polyline pl)
            {
                for (int i = 0; i < pl.NumberOfVertices - 1; i++)
                    samples.Add((pl.GetPoint2dAt(i), pl.GetPoint2dAt(i + 1)));
                if (pl.Closed && pl.NumberOfVertices > 1)
                    samples.Add((pl.GetPoint2dAt(pl.NumberOfVertices - 1), pl.GetPoint2dAt(0)));
            }
            else
            {
                return false;
            }

            double totalLen = 0.0;
            double insideLen = 0.0;
            const int segSamples = 18;

            foreach (var (a, b) in samples)
            {
                double len = a.GetDistanceTo(b);
                if (!(len > 1e-9)) continue;
                totalLen += len;

                int insideCount = 0;
                for (int i = 0; i <= segSamples; i++)
                {
                    double t = i / (double)segSamples;
                    var p = new Point2d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                    if (PolygonUtils.PointInPolygon(zoneRing, p))
                        insideCount++;
                }
                insideLen += len * (insideCount / (double)(segSamples + 1));
            }

            if (!(totalLen > 0)) return false;
            return (insideLen / totalLen) >= minOverlapFraction;
        }

        private static double TryGetSprinklerSpacingDu(Database db, double fallback)
        {
            if (db == null) return fallback;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, RuntimeSettings.Load().SprinklerSpacingM, out double s) && s > 0)
                    return s;
            }
            catch { /* ignore */ }
            return fallback;
        }
    }
}

