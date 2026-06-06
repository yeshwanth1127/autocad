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

                var spinePolys = new List<List<Point2d>>();
                var branchPolys = new List<List<Point2d>>();

                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    Entity ent = null;
                    try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { continue; }
                    if (ent == null) continue;

                    var pts = GetEntityPoints(ent);
                    if (pts == null || pts.Count < 2) continue;
                    bool hasInside = false;
                    foreach (var p in pts) if (PointInPolygon(zoneRing, p)) { hasInside = true; break; }
                    if (!hasInside) continue;

                    string ln = ent.Layer ?? "";
                    string lnLower = ln.ToLower();

                    bool isMain = (lnLower.Contains("main pipe") || lnLower.Contains("pipe main") || lnLower.Contains("mcd - main")
                                   || SprinklerXData.IsTaggedTrunk(ent))
                                  && !SprinklerXData.IsTaggedTrunkCap(ent);
                    if (isMain)
                    {
                        spinePolys.Add(pts);
                        continue;
                    }

                    bool isBranch = string.Equals(ln, SprinklerLayers.BranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(ln, SprinklerLayers.McdBranchPipeLayer, StringComparison.OrdinalIgnoreCase);
                    if (isBranch)
                        branchPolys.Add(pts);
                }

                if (spinePolys.Count == 0) { ed.WriteMessage("\nNo main pipe found in zone."); tr.Commit(); return; }
                if (branchPolys.Count == 0) { ed.WriteMessage("\nNo branch pipes found in zone."); tr.Commit(); return; }

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

                // End-arm sub-mains (distribution runs off main terminals) are labelled by Label main pipe.
                BranchArmClassifier2d.Classify(
                    spinePolys, branchPolys, snapTol,
                    out var armPolys, out var columnPolys, out bool verticalBranches);

                if (columnPolys.Count == 0) { ed.WriteMessage("\nNo branch columns found in zone."); tr.Commit(); return; }

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

                foreach (var pts in columnPolys)
                {
                    for (int si = 0; si + 1 < pts.Count; si++)
                    {
                        var segA = pts[si];
                        var segB = pts[si + 1];
                        double dx = segB.X - segA.X;
                        double dy = segB.Y - segA.Y;
                        double len = Math.Sqrt(dx * dx + dy * dy);
                        if (len < 1e-6) continue;

                        Point2d mainTap = BranchArmClassifier2d.ClosestOnMainNetwork(spinePolys, armPolys, segA);
                        int count = CountSprinklersServedOnSegmentFromFarEnd(
                            segA, segB, mainTap, verticalBranches, sprinklers, snapTol);
                        if (count <= 0) count = 1;

                        if (!NfpaBranchPipeSizing.TryGetMinNominalMmForSprinklerCount(count, out int nominalMm))
                            continue;

                        var mid = new Point2d((segA.X + segB.X) * 0.5, (segA.Y + segB.Y) * 0.5);

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
                ed.WriteMessage($"\nLabelled {labelCount} branch segment(s)" +
                                (armPolys.Count > 0 ? $" ({armPolys.Count} end-arm run(s) skipped — use Label main pipe)." : "."));
            }
        }

        /// <summary>
        /// Sprinklers served on this segment when counting from the farthest head in the row/column
        /// back toward the main (last segment = 1, each step toward main adds one).
        /// </summary>
        private static int CountSprinklersServedOnSegmentFromFarEnd(
            Point2d segA,
            Point2d segB,
            Point2d mainTap,
            bool verticalBranches,
            List<Point2d> sprinklers,
            double snapTol)
        {
            if (sprinklers == null || sprinklers.Count == 0)
                return 0;

            double tol = snapTol > 0 ? snapTol : 1e-6;
            double mainAxis = verticalBranches ? mainTap.Y : mainTap.X;

            double runA = DistanceAlongBranchRun(segA, mainTap, verticalBranches);
            double runB = DistanceAlongBranchRun(segB, mainTap, verticalBranches);
            Point2d outboard = runA >= runB ? segA : segB;

            double cross = verticalBranches ? outboard.X : outboard.Y;
            double outboardSide = (verticalBranches ? outboard.Y : outboard.X) - mainAxis;

            var onRun = new List<(double dist, Point2d p)>();
            for (int i = 0; i < sprinklers.Count; i++)
            {
                var s = sprinklers[i];
                double sRun = verticalBranches ? s.Y : s.X;
                double sCross = verticalBranches ? s.X : s.Y;
                if (Math.Abs(sCross - cross) > tol)
                    continue;

                double sSide = sRun - mainAxis;
                if (Math.Abs(sSide) <= tol && Math.Abs(outboardSide) > tol)
                    continue;
                if (outboardSide > tol && sSide < -tol)
                    continue;
                if (outboardSide < -tol && sSide > tol)
                    continue;

                onRun.Add((Math.Abs(sSide), s));
            }

            if (onRun.Count == 0)
                return 0;

            onRun.Sort((a, b) => a.dist.CompareTo(b.dist));
            int m = onRun.Count;

            int rankFromMain = RankSprinklerNearestEndpoint(outboard, onRun, tol);
            if (rankFromMain <= 0)
                rankFromMain = 1;

            return m - rankFromMain + 1;
        }

        private static double DistanceAlongBranchRun(Point2d p, Point2d mainTap, bool verticalBranches)
        {
            return Math.Abs((verticalBranches ? p.Y : p.X) - (verticalBranches ? mainTap.Y : mainTap.X));
        }

        private static int RankSprinklerNearestEndpoint(
            Point2d endpoint,
            List<(double dist, Point2d p)> sortedFromMain,
            double tol)
        {
            int bestRank = 0;
            double bestD = double.MaxValue;
            for (int i = 0; i < sortedFromMain.Count; i++)
            {
                double d = endpoint.GetDistanceTo(sortedFromMain[i].p);
                if (d < bestD)
                {
                    bestD = d;
                    bestRank = i + 1;
                }
            }

            if (bestD > tol * 4.0)
                return 0;
            return bestRank;
        }

        private static List<Point2d> GetEntityPoints(Entity ent)
        {
            if (ent is Polyline pl)
                return PolylineToRing(pl);
            if (ent is Line ln)
            {
                return new List<Point2d>
                {
                    new Point2d(ln.StartPoint.X, ln.StartPoint.Y),
                    new Point2d(ln.EndPoint.X, ln.EndPoint.Y),
                };
            }
            return null;
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
