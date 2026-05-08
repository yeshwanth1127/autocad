using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace autocad_final.Commands
{
    /// <summary>
    /// Lets users pick sprinkler heads and draws one straight branch per selected head.
    /// Priority: connect from main pipe; fallback to existing branch only when a valid
    /// straight main connection is not possible.
    /// </summary>
    public class ConnectBranchesManuallyCommand
    {
        [CommandMethod("SPRINKLERCONNECTBRANCHESMANUALLY", CommandFlags.Modal)]
        public void ConnectBranchesManually()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            var pso = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect one or more sprinkler heads to connect: ",
                SingleOnly = false
            };

            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "INSERT,CIRCLE")
            });

            var psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK || psr.Value == null || psr.Value.Count == 0)
                return;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                SprinklerXData.EnsureRegApp(tr, db);

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                ObjectId branchLayerId = SprinklerLayers.EnsureBranchPipeLayer(tr, db);
                if (!TryGetMainCandidates(tr, ms, db, out var mains, out string mainErr))
                {
                    ed.WriteMessage("\n" + (mainErr ?? "No main pipe polylines found.") + "\n");
                    return;
                }
                TryGetBranchCandidates(tr, ms, db, out var branches);

                int created = 0;
                int skippedNonSprinkler = 0;
                int skippedNoSource = 0;
                int skippedAlreadyOnSource = 0;
                int skippedErased = 0;
                int connectedFromMain = 0;
                int connectedFromBranch = 0;

                foreach (SelectedObject so in psr.Value)
                {
                    if (so == null || so.ObjectId.IsNull || so.ObjectId.IsErased)
                    {
                        skippedErased++;
                        continue;
                    }

                    Entity ent = null;
                    try { ent = tr.GetObject(so.ObjectId, OpenMode.ForRead, false) as Entity; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased)
                    {
                        skippedErased++;
                        continue;
                    }
                    if (ent == null)
                    {
                        skippedErased++;
                        continue;
                    }

                    if (!SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                    {
                        skippedNonSprinkler++;
                        continue;
                    }

                    if (!TryGetHeadPoint(ent, out Point3d headPt))
                    {
                        skippedNonSprinkler++;
                        continue;
                    }

                    List<Point2d> zoneRing = null;
                    Polyline zoneBoundary = null;
                    TryResolveZoneForSprinkler(ent, db, tr, out zoneRing, out zoneBoundary);
                    var shaftObstacles = BuildShaftObstaclesForZone(db, zoneBoundary);

                    bool usedMain = false;
                    if (!TryFindNearestAttachPoint(headPt, mains, zoneRing, shaftObstacles, out var nearestMain))
                    {
                        if (!TryFindNearestAttachPoint(headPt, branches, zoneRing, shaftObstacles, out nearestMain))
                        {
                            skippedNoSource++;
                            continue;
                        }
                    }
                    else
                    {
                        usedMain = true;
                    }

                    double segLen = headPt.DistanceTo(nearestMain.AttachPoint);
                    if (segLen <= 1e-6)
                    {
                        skippedAlreadyOnSource++;
                        continue;
                    }

                    double mainRefWidth = nearestMain.Width > 1e-12
                        ? nearestMain.Width
                        : NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
                    double branchWidth = NfpaBranchPipeSizing.GetBranchPolylineDisplayWidthDu(db, nominalMm: 25, mainRefWidth);
                    if (!(branchWidth > 1e-12))
                        branchWidth = Math.Max(mainRefWidth * 0.66, 1.0);

                    var seg = new Polyline();
                    seg.SetDatabaseDefaults(db);
                    seg.LayerId = branchLayerId;
                    seg.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                    seg.ConstantWidth = branchWidth;
                    seg.Elevation = headPt.Z;
                    seg.AddVertexAt(0, new Point2d(nearestMain.AttachPoint.X, nearestMain.AttachPoint.Y), 0, 0, 0);
                    seg.AddVertexAt(1, new Point2d(headPt.X, headPt.Y), 0, 0, 0);
                    seg.Closed = false;

                    if (SprinklerXData.TryGetZoneBoundaryHandle(ent, out string boundaryHandleHex) &&
                        !string.IsNullOrWhiteSpace(boundaryHandleHex))
                    {
                        SprinklerXData.ApplyZoneBoundaryTag(seg, boundaryHandleHex);
                    }

                    ms.AppendEntity(seg);
                    tr.AddNewlyCreatedDBObject(seg, true);
                    created++;
                    if (usedMain) connectedFromMain++;
                    else connectedFromBranch++;
                }

                tr.Commit();

                ed.WriteMessage(
                    "\nManual branch connect complete. " +
                    "Created: " + created +
                    " (from main: " + connectedFromMain + ", from branch fallback: " + connectedFromBranch + ")" +
                    ", skipped non-sprinkler: " + skippedNonSprinkler +
                    ", skipped erased: " + skippedErased +
                    ", skipped no source: " + skippedNoSource +
                    ", skipped already on source: " + skippedAlreadyOnSource + ".\n");
            }
        }

        private sealed class PipeCandidate
        {
            public Polyline Polyline;
            public double Width;
        }

        private static bool TryGetMainCandidates(
            Transaction tr,
            BlockTableRecord ms,
            Database db,
            out List<PipeCandidate> mains,
            out string errorMessage)
        {
            mains = new List<PipeCandidate>();
            errorMessage = null;
            if (tr == null || ms == null || db == null)
            {
                errorMessage = "Invalid drawing context.";
                return false;
            }

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (pl == null) continue;
                if (!SprinklerLayers.IsMainPipeLayerName(pl.Layer))
                    continue;
                if (SprinklerXData.IsTaggedTrunkCap(pl))
                    continue;
                if (SprinklerXData.IsTaggedConnector(pl))
                    continue;

                mains.Add(new PipeCandidate
                {
                    Polyline = pl,
                    Width = ReadPolylineWidthOrDefault(pl, db)
                });
            }

            if (mains.Count == 0)
            {
                errorMessage = "No main pipe found. Route main pipe first.";
                return false;
            }

            return true;
        }

        private static bool TryGetBranchCandidates(
            Transaction tr,
            BlockTableRecord ms,
            Database db,
            out List<PipeCandidate> branches)
        {
            branches = new List<PipeCandidate>();
            if (tr == null || ms == null || db == null)
                return false;

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (pl == null || pl.Closed) continue;

                string layer = pl.Layer ?? string.Empty;
                bool isBranchLayer =
                    string.Equals(layer, SprinklerLayers.BranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(layer, SprinklerLayers.McdBranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(layer, SprinklerLayers.McdConnectorBranchPipeLayer, StringComparison.OrdinalIgnoreCase);
                if (!isBranchLayer)
                    continue;

                branches.Add(new PipeCandidate
                {
                    Polyline = pl,
                    Width = ReadPolylineWidthOrDefault(pl, db)
                });
            }

            return branches.Count > 0;
        }

        private static double ReadPolylineWidthOrDefault(Polyline pl, Database db)
        {
            double w = 0;
            try { w = pl.ConstantWidth; } catch { w = 0; }
            if (w > 1e-12) return w;

            try
            {
                int n = pl.NumberOfVertices;
                int limit = pl.Closed ? n : Math.Max(0, n - 1);
                for (int i = 0; i < limit; i++)
                {
                    double sw = 0, ew = 0;
                    try { sw = pl.GetStartWidthAt(i); } catch { /* ignore */ }
                    try { ew = pl.GetEndWidthAt(i); } catch { /* ignore */ }
                    w = Math.Max(w, Math.Max(sw, ew));
                }
            }
            catch { /* ignore */ }

            if (w > 1e-12) return w;
            return NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
        }

        private static bool TryGetHeadPoint(Entity ent, out Point3d point)
        {
            point = default;
            if (ent is BlockReference br)
            {
                point = br.Position;
                return true;
            }
            if (ent is Circle c)
            {
                point = c.Center;
                return true;
            }
            return false;
        }

        private sealed class NearestPipePoint
        {
            public Point3d AttachPoint;
            public double Distance;
            public double Width;
        }

        private static bool TryFindNearestAttachPoint(
            Point3d headPt,
            List<PipeCandidate> pipes,
            List<Point2d> zoneRing,
            IList<(Point2d min, Point2d max)> shaftObstacles,
            out NearestPipePoint nearest)
        {
            nearest = null;
            if (pipes == null || pipes.Count == 0)
                return false;

            double best = double.MaxValue;
            Point3d bestPt = default;
            double bestW = 0;

            for (int i = 0; i < pipes.Count; i++)
            {
                var pl = pipes[i].Polyline;
                if (pl == null || pl.IsErased) continue;

                Point3d cp;
                try { cp = pl.GetClosestPointTo(headPt, extend: false); }
                catch { continue; }

                if (!ConnectionInsideZone(cp, headPt, zoneRing))
                    continue;
                if (SegmentIntersectsAnyBox(new Point2d(cp.X, cp.Y), new Point2d(headPt.X, headPt.Y), shaftObstacles))
                    continue;

                double d = headPt.DistanceTo(cp);
                if (d < best)
                {
                    best = d;
                    bestPt = cp;
                    bestW = pipes[i].Width;
                }
            }

            if (best == double.MaxValue)
                return false;

            nearest = new NearestPipePoint
            {
                AttachPoint = bestPt,
                Distance = best,
                Width = bestW
            };
            return true;
        }

        private static void TryResolveZoneForSprinkler(
            Entity sprinklerEnt,
            Database db,
            Transaction tr,
            out List<Point2d> zoneRing,
            out Polyline zoneBoundary)
        {
            zoneRing = null;
            zoneBoundary = null;
            if (sprinklerEnt == null || db == null || tr == null)
                return;
            if (!SprinklerXData.TryGetZoneBoundaryHandle(sprinklerEnt, out string boundaryHandleHex) ||
                string.IsNullOrWhiteSpace(boundaryHandleHex))
                return;

            ObjectId boundaryId = ObjectId.Null;
            try
            {
                var h = new Handle(Convert.ToInt64(boundaryHandleHex, 16));
                boundaryId = db.GetObjectId(false, h, 0);
            }
            catch { boundaryId = ObjectId.Null; }
            if (boundaryId.IsNull || boundaryId.IsErased)
                return;

            Polyline boundary = null;
            try { boundary = tr.GetObject(boundaryId, OpenMode.ForRead, false) as Polyline; }
            catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { boundary = null; }
            if (boundary == null || !boundary.Closed)
                return;
            zoneBoundary = boundary;

            try { zoneRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(boundary); }
            catch { zoneRing = null; }
            if (zoneRing == null || zoneRing.Count < 3)
                zoneRing = null;
        }

        private static List<(Point2d min, Point2d max)> BuildShaftObstaclesForZone(Database db, Polyline zoneBoundary)
        {
            if (db == null || zoneBoundary == null)
                return null;

            double clearanceDu = 0.05;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.08, out double sc) && sc > 0)
                    clearanceDu = sc;
            }
            catch { /* ignore */ }

            try
            {
                return BranchPipeShaftDetour2d.BuildShaftObstacles(db, zoneBoundary, clearanceDu);
            }
            catch
            {
                return null;
            }
        }

        private static bool ConnectionInsideZone(Point3d from, Point3d to, List<Point2d> zoneRing)
        {
            if (zoneRing == null || zoneRing.Count < 3)
                return true;

            const int samples = 10;
            for (int i = 0; i <= samples; i++)
            {
                double t = i / (double)samples;
                double x = from.X + (to.X - from.X) * t;
                double y = from.Y + (to.Y - from.Y) * t;
                if (!PointInPolygon(zoneRing, new Point2d(x, y)))
                    return false;
            }
            return true;
        }

        private static bool PointInPolygon(IList<Point2d> ring, Point2d p)
        {
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

        private static bool SegmentIntersectsAnyBox(
            Point2d a,
            Point2d b,
            IList<(Point2d min, Point2d max)> boxes)
        {
            if (boxes == null || boxes.Count == 0)
                return false;

            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                double xmin = Math.Min(box.min.X, box.max.X);
                double xmax = Math.Max(box.min.X, box.max.X);
                double ymin = Math.Min(box.min.Y, box.max.Y);
                double ymax = Math.Max(box.min.Y, box.max.Y);
                if (SegmentIntersectsAabb(a, b, xmin, xmax, ymin, ymax))
                    return true;
            }
            return false;
        }

        private static bool SegmentIntersectsAabb(
            Point2d a,
            Point2d b,
            double xmin,
            double xmax,
            double ymin,
            double ymax)
        {
            if (PointInAabb(a, xmin, xmax, ymin, ymax) || PointInAabb(b, xmin, xmax, ymin, ymax))
                return true;

            var c0 = new Point2d(xmin, ymin);
            var c1 = new Point2d(xmax, ymin);
            var c2 = new Point2d(xmax, ymax);
            var c3 = new Point2d(xmin, ymax);

            return SegmentsIntersect(a, b, c0, c1) ||
                   SegmentsIntersect(a, b, c1, c2) ||
                   SegmentsIntersect(a, b, c2, c3) ||
                   SegmentsIntersect(a, b, c3, c0);
        }

        private static bool PointInAabb(Point2d p, double xmin, double xmax, double ymin, double ymax)
            => p.X >= xmin && p.X <= xmax && p.Y >= ymin && p.Y <= ymax;

        private static bool SegmentsIntersect(Point2d a, Point2d b, Point2d c, Point2d d)
        {
            double o1 = Orientation(a, b, c);
            double o2 = Orientation(a, b, d);
            double o3 = Orientation(c, d, a);
            double o4 = Orientation(c, d, b);

            if ((o1 > 0 && o2 < 0 || o1 < 0 && o2 > 0) &&
                (o3 > 0 && o4 < 0 || o3 < 0 && o4 > 0))
                return true;

            const double eps = 1e-9;
            if (Math.Abs(o1) <= eps && OnSegment(a, b, c)) return true;
            if (Math.Abs(o2) <= eps && OnSegment(a, b, d)) return true;
            if (Math.Abs(o3) <= eps && OnSegment(c, d, a)) return true;
            if (Math.Abs(o4) <= eps && OnSegment(c, d, b)) return true;
            return false;
        }

        private static double Orientation(Point2d a, Point2d b, Point2d c)
            => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        private static bool OnSegment(Point2d a, Point2d b, Point2d p)
        {
            const double eps = 1e-9;
            return p.X >= Math.Min(a.X, b.X) - eps &&
                   p.X <= Math.Max(a.X, b.X) + eps &&
                   p.Y >= Math.Min(a.Y, b.Y) - eps &&
                   p.Y <= Math.Max(a.Y, b.Y) + eps;
        }
    }
}
