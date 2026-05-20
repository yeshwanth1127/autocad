using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;

namespace autocad_final.Commands
{
    public class LabelBranchesCommand
    {
        [CommandMethod("LABELBRANCHES", CommandFlags.Modal)]
        public void Execute()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var db = doc.Database;
            var ed = doc.Editor;

            var peo = new PromptEntityOptions("\nSelect a shaft entity to identify zone: ");
            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                SprinklerXData.EnsureRegApp(tr, db);

                Entity shaftEnt = null;
                try { shaftEnt = tr.GetObject(per.ObjectId, OpenMode.ForRead, false) as Entity; }
                catch { }
                if (shaftEnt == null) { ed.WriteMessage("\nCould not open shaft entity."); return; }

                Point3d shaftCenter;
                if (shaftEnt is Circle c) shaftCenter = c.Center;
                else if (shaftEnt is BlockReference br) shaftCenter = br.Position;
                else { ed.WriteMessage("\nUnsupported shaft entity type."); return; }

                var shaftPt2d = new Point2d(shaftCenter.X, shaftCenter.Y);

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Find zone containing shaft
                List<Point2d> zoneRing = null;
                string zoneBoundaryHex = null;
                Polyline zonePolyline = null;
                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    Polyline pl = null;
                    try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; } catch { continue; }
                    if (pl == null) continue;
                    if (!SprinklerLayers.IsUnifiedZoneDesignLayerName(pl.Layer) && !SprinklerLayers.IsMcdZoneOutlineLayerName(pl.Layer)) continue;
                    var ring = PolylineToRing(pl);
                    if (ring.Count < 3) continue;
                    if (!PointInPolygon(ring, shaftPt2d)) continue;
                    zoneRing = ring;
                    zonePolyline = pl;
                    SprinklerXData.TryGetZoneBoundaryHandle(pl, out zoneBoundaryHex);
                    break;
                }

                if (zoneRing == null) { ed.WriteMessage("\nShaft is not inside any zone."); tr.Commit(); return; }

                // Collect main pipe points
                List<Point2d> mainPipePts = null;
                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    Polyline pl = null;
                    try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; } catch { continue; }
                    if (pl == null) continue;
                    string ln = (pl.Layer ?? "").ToLower();
                    if (!ln.Contains("main pipe") && !ln.Contains("pipe main") && !ln.Contains("mcd - main")) continue;
                    if (SprinklerXData.IsTaggedTrunkCap(pl)) continue;
                    var pts = PolylineToRing(pl);
                    if (pts.Count < 2) continue;
                    bool hasInside = false;
                    foreach (var p in pts) if (PointInPolygon(zoneRing, p)) { hasInside = true; break; }
                    if (!hasInside) continue;
                    mainPipePts = pts;
                    break;
                }

                if (mainPipePts == null) { ed.WriteMessage("\nNo main pipe found in zone."); tr.Commit(); return; }

                // Collect branch polylines in zone
                var branches = new List<(ObjectId id, Polyline pl, List<Point2d> pts)>();
                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    Polyline pl = null;
                    try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; } catch { continue; }
                    if (pl == null) continue;
                    string ln = pl.Layer ?? "";
                    bool isBranch = string.Equals(ln, SprinklerLayers.BranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(ln, SprinklerLayers.McdBranchPipeLayer, StringComparison.OrdinalIgnoreCase);
                    if (!isBranch) continue;
                    var pts = PolylineToRing(pl);
                    if (pts.Count < 2) continue;
                    bool hasInside = false;
                    foreach (var p in pts) if (PointInPolygon(zoneRing, p)) { hasInside = true; break; }
                    if (!hasInside) continue;
                    branches.Add((id, pl, pts));
                }

                if (branches.Count == 0) { ed.WriteMessage("\nNo branch pipes found in zone."); tr.Commit(); return; }

                // Collect sprinklers in zone
                var sprinklers = new List<Point2d>();
                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    Entity ent = null;
                    try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { continue; }
                    if (ent == null) continue;
                    if (!SprinklerLayers.IsSprinklerHeadEntity(tr, ent)) continue;
                    Point2d p = default;
                    if (ent is Circle cc) p = new Point2d(cc.Center.X, cc.Center.Y);
                    else if (ent is BlockReference bref) p = new Point2d(bref.Position.X, bref.Position.Y);
                    else continue;
                    if (!PointInPolygon(zoneRing, p)) continue;
                    sprinklers.Add(p);
                }

                // Compute snap tolerance from drawing units (5 cm physical = endpoints that should be coincident).
                // A hardcoded value fails when drawing units ≠ mm: 5.0 in a meters drawing = 5 m,
                // which connects sprinklers across columns and inflates downstream counts to 150+.
                double snapTol = 50.0; // default: 50 mm (drawing units = mm)
                try
                {
                    if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.05, out double st) && st > 0)
                        snapTol = st;
                }
                catch { }

                // Build endpoint graph: branch index → list of connected branch indices
                var graph = new Dictionary<int, List<int>>();
                for (int i = 0; i < branches.Count; i++) graph[i] = new List<int>();

                for (int i = 0; i < branches.Count; i++)
                {
                    var ptsI = branches[i].pts;
                    for (int j = i + 1; j < branches.Count; j++)
                    {
                        var ptsJ = branches[j].pts;
                        if (EndpointsNear(ptsI, ptsJ, snapTol)) { graph[i].Add(j); graph[j].Add(i); }
                    }
                }

                // Find root branches (touching main pipe)
                var rootBranches = new List<int>();
                for (int i = 0; i < branches.Count; i++)
                {
                    var pts = branches[i].pts;
                    bool touchesMain = false;
                    foreach (var mp in mainPipePts)
                    {
                        if (pts[0].GetDistanceTo(mp) <= snapTol || pts[pts.Count - 1].GetDistanceTo(mp) <= snapTol)
                        { touchesMain = true; break; }
                    }
                    if (!touchesMain)
                    {
                        Point2d c1 = ClosestOnPolyline(mainPipePts, pts[0]);
                        Point2d c2 = ClosestOnPolyline(mainPipePts, pts[pts.Count - 1]);
                        if (c1.GetDistanceTo(pts[0]) <= snapTol || c2.GetDistanceTo(pts[pts.Count - 1]) <= snapTol)
                            touchesMain = true;
                    }
                    if (touchesMain) rootBranches.Add(i);
                }

                // DFS to compute downstream sprinkler count per branch segment
                var downstreamCounts = ComputeDownstreamCounts(branches, graph, rootBranches, snapTol, sprinklers);

                // Erase existing branch labels tagged to this zone
                ObjectId labelLayerId = SprinklerLayers.EnsureBranchLabelLayer(tr, db);
                if (!string.IsNullOrEmpty(zoneBoundaryHex))
                {
                    var toErase = new List<ObjectId>();
                    foreach (ObjectId id in ms)
                    {
                        if (id.IsErased) continue;
                        MText mt = null;
                        try { mt = tr.GetObject(id, OpenMode.ForRead, false) as MText; } catch { continue; }
                        if (mt == null) continue;
                        if (!string.Equals(mt.Layer, SprinklerLayers.BranchLabelLayer, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!SprinklerXData.IsTaggedBranchPipeScheduleLabel(mt)) continue;
                        if (!SprinklerXData.TryGetZoneBoundaryHandle(mt, out string h) ||
                            !string.Equals(h, zoneBoundaryHex, StringComparison.OrdinalIgnoreCase)) continue;
                        toErase.Add(id);
                    }
                    foreach (var id in toErase)
                    {
                        try { var e = tr.GetObject(id, OpenMode.ForWrite, false) as Entity; e?.Erase(); } catch { }
                    }
                }

                // Compute label sizing from drawing units
                double tickLen = 1.0;
                try { if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.20, out double t) && t > 0) tickLen = t; }
                catch { }
                double boundaryW = SprinklerLayers.BoundaryPolylineConstantWidth(db);
                double labelOffsetDu = Math.Max(tickLen * 0.65, boundaryW * 0.08);
                double labelTextHeight = Math.Max(boundaryW * 0.22, tickLen * 0.55);

                double elevation = zonePolyline?.Elevation ?? 0;
                bool tagZone = !string.IsNullOrEmpty(zoneBoundaryHex);
                int labelCount = 0;

                for (int i = 0; i < branches.Count; i++)
                {
                    int count = downstreamCounts[i];
                    if (count <= 0) count = 1;

                    if (!NfpaBranchPipeSizing.TryGetMinNominalMmForSprinklerCount(count, out int nominalMm))
                        continue;

                    var pts = branches[i].pts;

                    // Label each segment of the branch polyline separately
                    for (int si = 0; si + 1 < pts.Count; si++)
                    {
                        var segA = pts[si];
                        var segB = pts[si + 1];
                        double dx = segB.X - segA.X;
                        double dy = segB.Y - segA.Y;
                        double len = Math.Sqrt(dx * dx + dy * dy);
                        if (len < 1e-6) continue;

                        var mid = new Point2d((segA.X + segB.X) * 0.5, (segA.Y + segB.Y) * 0.5);

                        // Offset perpendicular: horizontal seg → above (+Y), vertical seg → right (+X)
                        bool segVertical = Math.Abs(dy) >= Math.Abs(dx);
                        double px = segVertical ? 1.0 : 0.0;
                        double py = segVertical ? 0.0 : 1.0;

                        var ins2d = new Point2d(mid.X + px * labelOffsetDu, mid.Y + py * labelOffsetDu);
                        ins2d = PolygonUtils.ClampPointToClosedRing(zoneRing, ins2d, snapTol * 0.5);

                        var mt = new MText();
                        mt.SetDatabaseDefaults(db);
                        mt.LayerId = labelLayerId;
                        mt.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                        mt.Location = new Point3d(ins2d.X, ins2d.Y, elevation);
                        mt.Attachment = AttachmentPoint.MiddleCenter;
                        mt.TextHeight = labelTextHeight;
                        mt.Contents = "Ø" + nominalMm.ToString();
                        double rot = Math.Atan2(dy, dx);
                        if (rot > Math.PI * 0.5) rot -= Math.PI;
                        else if (rot < -Math.PI * 0.5) rot += Math.PI;
                        mt.Rotation = rot;
                        SprinklerXData.TagAsBranchPipeScheduleLabel(mt);
                        if (tagZone)
                            SprinklerXData.ApplyZoneBoundaryTag(mt, zoneBoundaryHex);
                        ms.AppendEntity(mt);
                        tr.AddNewlyCreatedDBObject(mt, true);
                        labelCount++;
                    }
                }

                tr.Commit();
                ed.WriteMessage($"\nLabelled {labelCount} branch segment(s).");
            }
        }

        /// <summary>
        /// For each branch polyline, compute the number of sprinkler-heads reachable from its
        /// outgoing end (the end pointing away from the main pipe).
        /// <para>
        /// This mirrors <c>PlanSprinklersServedOnBranchSegmentFromTip(i, m) = m - i</c> used in
        /// <c>AttachBranchesCommand</c>: the root segment (closest to main pipe) carries all m heads;
        /// each step toward the tip reduces the count by 1; the tip segment carries 1.
        /// </para>
        /// </summary>
        private static int[] ComputeDownstreamCounts(
            List<(ObjectId id, Polyline pl, List<Point2d> pts)> branches,
            Dictionary<int, List<int>> graph,
            List<int> roots,
            double snapTol,
            List<Point2d> sprinklers)
        {
            var result = new int[branches.Count];
            var visited = new HashSet<int>();

            // incomingPt: the endpoint of this polyline that faces the parent / main pipe.
            // local = 1 if a sprinkler sits at the OUTGOING endpoint (away from main pipe), 0 if
            //         this is an intermediate corner (L-shape bend with no head).
            // result[idx] = local + sum(result[children]) — equivalent to PlanSprinklersServedOnBranchSegmentFromTip.
            void Dfs(int idx, int parentIdx, Point2d incomingPt)
            {
                if (visited.Contains(idx)) return;
                visited.Add(idx);

                var pts = branches[idx].pts;
                bool pt0IsIncoming = incomingPt != Point2d.Origin &&
                                     pts[0].GetDistanceTo(incomingPt) <= snapTol;
                Point2d outgoingPt = pt0IsIncoming ? pts[pts.Count - 1] : pts[0];

                // Each step adds exactly 1 if a sprinkler head sits at the outgoing endpoint —
                // same unit as the m-i formula in AttachBranchesCommand.
                int local = sprinklers.Exists(s => outgoingPt.GetDistanceTo(s) <= snapTol) ? 1 : 0;

                int downstream = 0;
                foreach (int nb in graph[idx])
                {
                    if (nb == parentIdx) continue;
                    Dfs(nb, idx, outgoingPt);
                    downstream += result[nb];
                }

                result[idx] = local + downstream;
            }

            foreach (int root in roots)
            {
                var pts = branches[root].pts;
                // Incoming side of the root = the end NOT at a sprinkler (it attaches to the main pipe).
                Point2d rootIncoming = sprinklers.Exists(s => pts[0].GetDistanceTo(s) <= snapTol)
                    ? pts[pts.Count - 1]
                    : pts[0];
                Dfs(root, -1, rootIncoming);
            }

            // Orphaned branches not reachable from any detected root — walk from first endpoint.
            for (int i = 0; i < branches.Count; i++)
                if (!visited.Contains(i))
                    Dfs(i, -1, branches[i].pts[0]);

            return result;
        }

        private static bool EndpointsNear(List<Point2d> a, List<Point2d> b, double tol)
        {
            Point2d a0 = a[0], a1 = a[a.Count - 1];
            Point2d b0 = b[0], b1 = b[b.Count - 1];
            return a0.GetDistanceTo(b0) <= tol || a0.GetDistanceTo(b1) <= tol ||
                   a1.GetDistanceTo(b0) <= tol || a1.GetDistanceTo(b1) <= tol;
        }

        private static Point2d ClosestOnPolyline(List<Point2d> pts, Point2d p)
        {
            double minD = double.MaxValue;
            Point2d best = p;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var cp = ClosestOnSegment(pts[i], pts[i + 1], p);
                double d = cp.GetDistanceTo(p);
                if (d < minD) { minD = d; best = cp; }
            }
            return best;
        }

        private static Point2d ClosestOnSegment(Point2d a, Point2d b, Point2d p)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-12) return a;
            double t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq));
            return new Point2d(a.X + t * dx, a.Y + t * dy);
        }

        private static List<Point2d> PolylineToRing(Polyline pl)
        {
            var pts = new List<Point2d>();
            for (int i = 0; i < pl.NumberOfVertices; i++)
                pts.Add(new Point2d(pl.GetPoint2dAt(i).X, pl.GetPoint2dAt(i).Y));
            return pts;
        }

        private static bool PointInPolygon(IList<Point2d> ring, Point2d p)
        {
            bool inside = false;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = ring[i]; var b = ring[j];
                bool intersect = ((a.Y > p.Y) != (b.Y > p.Y)) &&
                    (p.X < (b.X - a.X) * (p.Y - a.Y) / ((b.Y - a.Y) == 0 ? 1e-12 : (b.Y - a.Y)) + a.X);
                if (intersect) inside = !inside;
            }
            return inside;
        }
    }
}
