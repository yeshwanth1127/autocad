using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;

namespace autocad_final.Commands
{
    public class RouteBranchesFinalCommand
    {
        [CommandMethod("ROUTEBRANCHESFINAL", CommandFlags.Modal)]
        public void Execute()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var db = doc.Database;
            var ed = doc.Editor;

            ed.WriteMessage("\nSelect a shaft entity: ");
            PromptEntityOptions peo = new PromptEntityOptions("\nSelect shaft:");
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nNo shaft selected.");
                return;
            }

            using (var docLock = doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                SprinklerXData.EnsureRegApp(tr, db);

                Entity shaftEnt = null;
                try
                {
                    shaftEnt = tr.GetObject(per.ObjectId, OpenMode.ForRead, false) as Entity;
                }
                catch { }

                if (shaftEnt == null)
                {
                    ed.WriteMessage("\nCould not open shaft entity.");
                    return;
                }

                Point3d shaftCenter = GetEntityCenter(shaftEnt);
                var shaftPt2d = new Point2d(shaftCenter.X, shaftCenter.Y);

                if (!FindZoneContainingPoint(tr, db, shaftPt2d, out Polyline zoneBoundary, out string zoneBoundaryHex))
                {
                    ed.WriteMessage("\nShaft is not inside any zone boundary.");
                    tr.Commit();
                    return;
                }

                if (!GetZoneRing(zoneBoundary, out List<Point2d> zoneRing))
                {
                    ed.WriteMessage("\nCould not extract zone ring.");
                    tr.Commit();
                    return;
                }

                if (!FindMainPipeInZone(tr, db, zoneRing, out List<Point2d> mainPipePts))
                {
                    ed.WriteMessage("\nNo main pipe found in this zone.");
                    tr.Commit();
                    return;
                }

                if (!CollectSprinklersInZone(tr, db, zoneBoundary, zoneRing, zoneBoundaryHex, out List<Point2d> sprinklers))
                {
                    ed.WriteMessage("\nNo sprinklers found in this zone.");
                    tr.Commit();
                    return;
                }

                var ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                bool success = RouteBranchesToSprinklers(tr, db, ms, mainPipePts, sprinklers, zoneRing, zoneBoundaryHex);

                if (success)
                    ed.WriteMessage("\nBranches routed successfully.");
                else
                    ed.WriteMessage("\nFailed to route some branches.");

                tr.Commit();
            }
        }

        private static Point3d GetEntityCenter(Entity ent)
        {
            if (ent is Circle c)
                return c.Center;
            if (ent is BlockReference br)
                return br.Position;
            if (ent is Polyline pl && pl.Bounds.HasValue)
            {
                var b = pl.Bounds.Value;
                return new Point3d(
                    (b.MinPoint.X + b.MaxPoint.X) * 0.5,
                    (b.MinPoint.Y + b.MaxPoint.Y) * 0.5,
                    (b.MinPoint.Z + b.MaxPoint.Z) * 0.5);
            }
            return Point3d.Origin;
        }

        private static bool FindZoneContainingPoint(Transaction tr, Database db, Point2d pt, out Polyline zoneBoundary, out string zoneBoundaryHex)
        {
            zoneBoundary = null;
            zoneBoundaryHex = null;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch { continue; }
                if (pl == null) continue;

                if (!SprinklerLayers.IsUnifiedZoneDesignLayerName(pl.Layer) && !SprinklerLayers.IsMcdZoneOutlineLayerName(pl.Layer)) continue;

                var ring = PolylineToPoint2dList(pl);
                if (ring.Count < 3) continue;

                if (PolygonUtils.PointInPolygon(ring, pt))
                {
                    zoneBoundary = pl;
                    if (SprinklerXData.TryGetZoneBoundaryHandle(pl, out string h))
                        zoneBoundaryHex = h;
                    return true;
                }
            }

            return false;
        }

        private static bool GetZoneRing(Polyline zoneBoundary, out List<Point2d> ring)
        {
            ring = PolylineToPoint2dList(zoneBoundary);
            return ring.Count >= 3;
        }

        private static bool FindMainPipeInZone(Transaction tr, Database db, List<Point2d> zoneRing, out List<Point2d> mainPipePts)
        {
            mainPipePts = null;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch { continue; }
                if (pl == null) continue;

                if (!SprinklerLayers.IsMainPipeLayerName(pl.Layer)) continue;
                if (SprinklerXData.IsTaggedTrunkCap(pl)) continue;

                var pts = PolylineToPoint2dList(pl);
                if (pts.Count < 2) continue;

                // Check if main pipe overlaps zone
                int inside = 0;
                foreach (var p in pts)
                    if (PolygonUtils.PointInPolygon(zoneRing, p))
                        inside++;

                if (inside >= 2)
                {
                    mainPipePts = pts;
                    return true;
                }
            }

            return false;
        }

        private static bool CollectSprinklersInZone(Transaction tr, Database db, Polyline zoneBoundary, List<Point2d> zoneRing, string zoneBoundaryHex, out List<Point2d> sprinklers)
        {
            sprinklers = new List<Point2d>();

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch { continue; }
                if (ent == null) continue;

                if (!SprinklerLayers.IsSprinklerHeadEntity(tr, ent)) continue;

                Point2d p = default;
                if (ent is Circle c)
                    p = new Point2d(c.Center.X, c.Center.Y);
                else if (ent is BlockReference br)
                    p = new Point2d(br.Position.X, br.Position.Y);
                else
                    continue;

                // Must be inside the zone ring
                if (!PolygonUtils.PointInPolygon(zoneRing, p)) continue;

                sprinklers.Add(p);
            }

            return sprinklers.Count > 0;
        }

        private static bool RouteBranchesToSprinklers(Transaction tr, Database db, BlockTableRecord ms, List<Point2d> mainPipePts, List<Point2d> sprinklers, List<Point2d> zoneRing, string zoneBoundaryHex)
        {
            ObjectId branchLayerId = SprinklerLayers.EnsureMcdBranchPipeLayer(tr, db);
            double mainW = EstimateMainPipeWidth(db);
            double branchW = Math.Max(mainW * 0.66, 1.0);
            double elevation = 0;

            // Determine trunk orientation
            bool trunkHorizontal = DetermineTrunkOrientation(mainPipePts);
            bool verticalFirst = trunkHorizontal; // vertical-first if trunk is horizontal

            int successCount = 0;

            if (sprinklers.Count == 0)
                return true;

            // Find closest sprinkler to main pipe (first attachment point)
            double minDistToMain = double.MaxValue;
            int firstSprinklerIdx = 0;
            for (int si = 0; si < sprinklers.Count; si++)
            {
                var spr = sprinklers[si];
                for (int pi = 0; pi + 1 < mainPipePts.Count; pi++)
                {
                    var a = mainPipePts[pi];
                    var b = mainPipePts[pi + 1];
                    var closest = ClosestPointOnSegment(a, b, spr);
                    double d = closest.GetDistanceTo(spr);
                    if (d < minDistToMain)
                    {
                        minDistToMain = d;
                        firstSprinklerIdx = si;
                    }
                }
            }

            // Connect first sprinkler to main pipe
            var firstSpr = sprinklers[firstSprinklerIdx];
            double minDist = double.MaxValue;
            Point2d attachPt = firstSpr;
            for (int i = 0; i + 1 < mainPipePts.Count; i++)
            {
                var a = mainPipePts[i];
                var b = mainPipePts[i + 1];
                var closest = ClosestPointOnSegment(a, b, firstSpr);
                double d = closest.GetDistanceTo(firstSpr);
                if (d < minDist)
                {
                    minDist = d;
                    attachPt = closest;
                }
            }

            if (BuildOrthogonalPath(attachPt, firstSpr, zoneRing, verticalFirst, out List<Point2d> pathVerts))
            {
                var branch = CreateBranchPolyline(db, pathVerts, elevation, branchLayerId, branchW);
                if (branch != null)
                {
                    SprinklerXData.ApplyZoneBoundaryTag(branch, zoneBoundaryHex?.Trim() ?? "");
                    ms.AppendEntity(branch);
                    tr.AddNewlyCreatedDBObject(branch, true);
                    successCount++;
                }
            }

            // Connect remaining sprinklers: prefer direct main pipe, fallback to chaining
            Point2d prevSprinkler = firstSpr;
            for (int si = 0; si < sprinklers.Count; si++)
            {
                if (si == firstSprinklerIdx) continue;

                var currentSpr = sprinklers[si];
                Point2d connectFrom = prevSprinkler;

                Point2d mainAttach = ClosestPointOnSegment(mainPipePts[0], mainPipePts[mainPipePts.Count - 1], currentSpr);
                if (BuildOrthogonalPath(mainAttach, currentSpr, zoneRing, verticalFirst, out List<Point2d> pathToMain))
                {
                    connectFrom = mainAttach;
                }

                if (!BuildOrthogonalPath(connectFrom, currentSpr, zoneRing, verticalFirst, out List<Point2d> pathVts))
                    continue;

                var branch = CreateBranchPolyline(db, pathVts, elevation, branchLayerId, branchW);
                if (branch != null)
                {
                    SprinklerXData.ApplyZoneBoundaryTag(branch, zoneBoundaryHex?.Trim() ?? "");
                    ms.AppendEntity(branch);
                    tr.AddNewlyCreatedDBObject(branch, true);
                    successCount++;
                }

                prevSprinkler = currentSpr;
            }

            return successCount == sprinklers.Count;
        }

        private static bool DetermineTrunkOrientation(List<Point2d> pts)
        {
            if (pts == null || pts.Count < 2)
                return true;

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }

            double spanX = maxX - minX;
            double spanY = maxY - minY;
            return spanY >= spanX; // true = vertical trunk
        }

        private static Point2d ClosestPointOnSegment(Point2d a, Point2d b, Point2d p)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;

            if (lenSq < 1e-12)
                return a;

            double t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq));
            return new Point2d(a.X + t * dx, a.Y + t * dy);
        }

        private static bool BuildOrthogonalPath(Point2d attach, Point2d target, List<Point2d> zoneRing, bool verticalFirst, out List<Point2d> path)
        {
            path = null;
            const double tol = 1e-7;

            if (attach.GetDistanceTo(target) <= tol)
            {
                path = new List<Point2d> { attach, target };
                return true;
            }

            // Try corner 1: go in preferred direction first
            Point2d corner1 = verticalFirst ? new Point2d(attach.X, target.Y) : new Point2d(target.X, attach.Y);
            if (SegmentInsideRing(zoneRing, attach, corner1) && SegmentInsideRing(zoneRing, corner1, target))
            {
                path = CollapseCollinear(new List<Point2d> { attach, corner1, target });
                if (path.Count >= 2)
                    return true;
            }

            // Try corner 2: go in alternate direction
            Point2d corner2 = verticalFirst ? new Point2d(target.X, attach.Y) : new Point2d(attach.X, target.Y);
            if (SegmentInsideRing(zoneRing, attach, corner2) && SegmentInsideRing(zoneRing, corner2, target))
            {
                path = CollapseCollinear(new List<Point2d> { attach, corner2, target });
                if (path.Count >= 2)
                    return true;
            }

            // Fallback: direct line (may clip zone boundary)
            path = new List<Point2d> { attach, target };
            return true;
        }

        private static bool SegmentInsideRing(List<Point2d> ring, Point2d p0, Point2d p1)
        {
            const int samples = 14;
            for (int i = 0; i <= samples; i++)
            {
                double t = i / (double)samples;
                var p = new Point2d(p0.X + (p1.X - p0.X) * t, p0.Y + (p1.Y - p0.Y) * t);
                if (!PolygonUtils.PointInPolygon(ring, p))
                    return false;
            }
            return true;
        }

        private static List<Point2d> CollapseCollinear(List<Point2d> verts)
        {
            if (verts == null || verts.Count < 2)
                return verts ?? new List<Point2d>();

            var result = new List<Point2d> { verts[0] };
            for (int i = 1; i < verts.Count; i++)
            {
                if (result[result.Count - 1].GetDistanceTo(verts[i]) > 1e-9)
                    result.Add(verts[i]);
            }
            return result;
        }

        private static Polyline CreateBranchPolyline(Database db, List<Point2d> verts, double elevation, ObjectId layerId, double width)
        {
            if (verts == null || verts.Count < 2)
                return null;

            var pl = new Polyline();
            pl.SetDatabaseDefaults(db);
            pl.LayerId = layerId;
            pl.ConstantWidth = width;
            pl.Elevation = elevation;
            pl.Closed = false;

            for (int i = 0; i < verts.Count; i++)
                pl.AddVertexAt(i, verts[i], 0, 0, 0);

            return pl;
        }

        private static double EstimateMainPipeWidth(Database db)
        {
            // Default main pipe width
            return NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
        }

        private static List<Point2d> PolylineToPoint2dList(Polyline pl)
        {
            var pts = new List<Point2d>();
            for (int i = 0; i < pl.NumberOfVertices; i++)
                pts.Add(new Point2d(pl.GetPoint2dAt(i).X, pl.GetPoint2dAt(i).Y));
            return pts;
        }
    }
}
