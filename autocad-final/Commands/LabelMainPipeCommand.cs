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
    /// <summary>
    /// Labels the main pipe using the same PIPE SCHEDULE table as the branch labels.
    ///
    /// The "main network" is the main spine plus any pipes that run off its endpoints (arms) — those
    /// auxiliary distribution runs are parallel to the main flow (perpendicular to branch columns) and
    /// are sized and labelled as main even when sprinkler columns connect to them. The network is walked as an angle-aware path (works for a
    /// slanted or bent main), and each branch column's sprinkler count is accumulated toward the shaft
    /// connector (the column farthest from the shaft carries only its own heads; each segment closer to
    /// the shaft adds the columns beyond it). Each network segment is then labelled Øxx via
    /// <see cref="NfpaBranchPipeSizing.TryGetMinNominalMmForSprinklerCount"/> — identical to branches.
    /// </summary>
    public class LabelMainPipeCommand
    {
        [CommandMethod("LABELMAINPIPE", CommandFlags.Modal)]
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

                // Snap tolerance from drawing units (5 cm). A hardcoded value breaks when units != mm and
                // merges adjacent columns / pipe joints, corrupting counts and connectivity.
                double snapTol = 50.0;
                try
                {
                    if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.05, out double st) && st > 0)
                        snapTol = st;
                }
                catch { }

                // Collect main spine polylines (yellow main / tagged trunk) inside the zone.
                var spinePolys = new List<List<Point2d>>();
                // Collect branch-layer pipes inside the zone (columns + candidate arms). Connector layer excluded.
                var branchPolys = new List<List<Point2d>>();
                var sprinklers = new List<Point2d>();

                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    Entity ent = null;
                    try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { continue; }
                    if (ent == null) continue;

                    // Sprinkler heads
                    if (SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                    {
                        Point2d hp = default;
                        if (ent is Circle cc) hp = new Point2d(cc.Center.X, cc.Center.Y);
                        else if (ent is BlockReference bref) hp = new Point2d(bref.Position.X, bref.Position.Y);
                        else continue;
                        if (PointInPolygon(zoneRing, hp)) sprinklers.Add(hp);
                        continue;
                    }

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

                    bool isConnector = string.Equals(ln, SprinklerLayers.McdConnectorBranchPipeLayer, StringComparison.OrdinalIgnoreCase);
                    if (isConnector) continue; // connector handled via shaft, not part of labelled main

                    bool isBranch = string.Equals(ln, SprinklerLayers.BranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(ln, SprinklerLayers.McdBranchPipeLayer, StringComparison.OrdinalIgnoreCase);
                    if (isBranch)
                        branchPolys.Add(pts);
                }

                if (spinePolys.Count == 0) { ed.WriteMessage("\nNo main pipe found in zone."); tr.Commit(); return; }

                // Arms leave a main terminal and run along the distribution axis; columns are perpendicular.
                BranchArmClassifier2d.Classify(
                    spinePolys, branchPolys, snapTol,
                    out var armPolys, out var columnPolys, out bool columnsVertical);

                if (columnPolys.Count == 0) { ed.WriteMessage("\nNo branch columns found in zone."); tr.Commit(); return; }

                // Build the conductive main network: spine + arms that attach at a network endpoint.
                var networkSegs = BuildMainNetwork(spinePolys, armPolys, snapTol);

                // Stitch the network into one ordered path (tree diameter = arms + slant; the short
                // connector stub, if any spine reaches the shaft, is naturally excluded).
                var chain = BuildChainPath(networkSegs, spinePolys, snapTol);
                if (chain == null || chain.Count < 2) { ed.WriteMessage("\nCould not resolve a main pipe path."); tr.Commit(); return; }

                // Arc-length parameterisation of the chain.
                var arc = new double[chain.Count];
                arc[0] = 0;
                for (int i = 1; i < chain.Count; i++)
                    arc[i] = arc[i - 1] + chain[i - 1].GetDistanceTo(chain[i]);
                double totalLen = arc[chain.Count - 1];
                if (totalLen < snapTol) { ed.WriteMessage("\nMain pipe path too short to label."); tr.Commit(); return; }

                // Source = where the shaft connector meets the main = nearest chain point to the shaft.
                ProjectOntoChain(chain, arc, shaftPt2d, out _, out double sSrc, out _);

                // Build columns: merge pipes sharing a cross-coordinate, count heads, find tap arc-length.
                var columns = BuildColumns(columnPolys, sprinklers, chain, arc, columnsVertical, snapTol);
                if (columns.Count == 0) { ed.WriteMessage("\nNo branch columns resolved on the main pipe."); tr.Commit(); return; }

                // Accumulate toward the shaft: split columns by the source arc-length, then on each side
                // accumulate from the far end inward so each column's source-side segment carries its
                // own heads plus every column farther from the shaft.
                var sideA = new List<Column>(); // s <= sSrc
                var sideB = new List<Column>(); // s >  sSrc
                foreach (var col in columns)
                {
                    if (col.ArcS <= sSrc) sideA.Add(col); else sideB.Add(col);
                }

                var segments = new List<LabelSeg>();
                // Side A: increasing arc toward the source.
                sideA.Sort((a, b) => a.ArcS.CompareTo(b.ArcS));
                int cumA = 0;
                for (int i = 0; i < sideA.Count; i++)
                {
                    cumA += sideA[i].Count;
                    double s0 = sideA[i].ArcS;
                    double s1 = (i + 1 < sideA.Count) ? sideA[i + 1].ArcS : sSrc;
                    segments.Add(new LabelSeg { S0 = s0, S1 = s1, Load = cumA });
                }
                // Side B: decreasing arc toward the source.
                sideB.Sort((a, b) => b.ArcS.CompareTo(a.ArcS));
                int cumB = 0;
                for (int i = 0; i < sideB.Count; i++)
                {
                    cumB += sideB[i].Count;
                    double s0 = sideB[i].ArcS;
                    double s1 = (i + 1 < sideB.Count) ? sideB[i + 1].ArcS : sSrc;
                    segments.Add(new LabelSeg { S0 = s0, S1 = s1, Load = cumB });
                }

                // Erase existing main pipe schedule labels for this zone before redrawing.
                EraseExistingMainPipeLabels(tr, ms, zoneBoundaryHex);

                double tickLen = 1.0;
                try { if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.20, out double t) && t > 0) tickLen = t; }
                catch { }
                double boundaryW = SprinklerLayers.BoundaryPolylineConstantWidth(db);
                double labelOffsetDu = Math.Max(tickLen * 0.65, boundaryW * 0.08);
                double labelTextHeight = Math.Max(boundaryW * 0.22, tickLen * 0.55);

                double elevation = zonePolyline?.Elevation ?? 0;
                bool tagZone = !string.IsNullOrEmpty(zoneBoundaryHex);
                ObjectId labelLayerId = SprinklerLayers.EnsureMcdLabelLayer(tr, db);
                int labelCount = 0;

                foreach (var seg in segments)
                {
                    if (seg.Load <= 0) continue;
                    double lo = Math.Min(seg.S0, seg.S1);
                    double hi = Math.Max(seg.S0, seg.S1);
                    if (hi - lo < snapTol) continue; // no room to place the label

                    if (!NfpaBranchPipeSizing.TryGetMinNominalMmForSprinklerCount(seg.Load, out int nominalMm))
                        continue;

                    double midS = 0.5 * (lo + hi);
                    PointAtArc(chain, arc, midS, out Point2d mid, out Vector2d dir);

                    double nx = -dir.Y, ny = dir.X;
                    var candA = new Point2d(mid.X + nx * labelOffsetDu, mid.Y + ny * labelOffsetDu);
                    var candB = new Point2d(mid.X - nx * labelOffsetDu, mid.Y - ny * labelOffsetDu);
                    Point2d ins2d;
                    if (PointInPolygon(zoneRing, candA)) ins2d = candA;
                    else if (PointInPolygon(zoneRing, candB)) ins2d = candB;
                    else ins2d = candA;
                    ins2d = PolygonUtils.ClampPointToClosedRing(zoneRing, ins2d, snapTol * 0.5);

                    double rot = Math.Atan2(dir.Y, dir.X);
                    if (rot > Math.PI * 0.5) rot -= Math.PI;
                    else if (rot < -Math.PI * 0.5) rot += Math.PI;

                    var mt = new MText();
                    mt.SetDatabaseDefaults(db);
                    mt.LayerId = labelLayerId;
                    mt.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                    mt.Location = new Point3d(ins2d.X, ins2d.Y, elevation);
                    mt.Attachment = AttachmentPoint.MiddleCenter;
                    mt.TextHeight = labelTextHeight;
                    mt.Contents = "Ø" + nominalMm.ToString();
                    mt.Rotation = rot;
                    SprinklerXData.TagAsMainPipeScheduleLabel(mt);
                    if (tagZone)
                        SprinklerXData.ApplyZoneBoundaryTag(mt, zoneBoundaryHex);
                    ms.AppendEntity(mt);
                    tr.AddNewlyCreatedDBObject(mt, true);
                    labelCount++;
                }

                tr.Commit();
                ed.WriteMessage($"\nLabelled {labelCount} main pipe segment(s) across {columns.Count} column(s)" +
                                (armPolys.Count > 0 ? $" (incl. {armPolys.Count} arm run(s))." : "."));
            }
        }

        private sealed class Column
        {
            public double Cross;   // coordinate perpendicular to the columns (identifies the column)
            public Point2d Tap;    // tap point on the main network
            public double ArcS;    // arc-length of the tap along the chain
            public int Count;      // sprinklers in this column
        }

        private struct LabelSeg
        {
            public double S0;
            public double S1;
            public int Load;
        }

        // ── Main network (spine + arms) ─────────────────────────────────────────────

        private struct Seg { public Point2d A; public Point2d B; }

        /// <summary>
        /// Seeds the network with the spine segments, then iteratively adds arm pipes whose endpoint
        /// coincides with a current network vertex. Arms-of-arms are picked up by iterating.
        /// </summary>
        private static List<Seg> BuildMainNetwork(
            List<List<Point2d>> spinePolys,
            List<List<Point2d>> armPolys,
            double snapTol)
        {
            var segs = new List<Seg>();
            foreach (var poly in spinePolys)
                AddPolySegs(segs, poly);

            var remaining = new List<List<Point2d>>(armPolys);
            bool added = true;
            while (added)
            {
                added = false;
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    var poly = remaining[i];
                    if (PolyTouchesNetwork(poly, segs, snapTol))
                    {
                        AddPolySegs(segs, poly);
                        remaining.RemoveAt(i);
                        added = true;
                    }
                }
            }
            return segs;
        }

        private static void AddPolySegs(List<Seg> segs, List<Point2d> poly)
        {
            for (int i = 0; i + 1 < poly.Count; i++)
            {
                if (poly[i].GetDistanceTo(poly[i + 1]) < 1e-9) continue;
                segs.Add(new Seg { A = poly[i], B = poly[i + 1] });
            }
        }

        private static bool PolyTouchesNetwork(List<Point2d> poly, List<Seg> segs, double snapTol)
        {
            foreach (var seg in segs)
            {
                foreach (var p in poly)
                {
                    if (p.GetDistanceTo(seg.A) <= snapTol || p.GetDistanceTo(seg.B) <= snapTol)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Stitches network segments into a single ordered point path: the tree diameter (longest
        /// terminal-to-terminal run by length), which is the slant + arms. Falls back to the longest
        /// single spine polyline if connectivity cannot be resolved.
        /// </summary>
        private static List<Point2d> BuildChainPath(List<Seg> segs, List<List<Point2d>> spinePolys, double snapTol)
        {
            if (segs == null || segs.Count == 0)
                return LongestSpine(spinePolys);

            // Build node list (merged within snapTol) and weighted adjacency.
            var nodes = new List<Point2d>();
            var adj = new List<List<(int to, double w)>>();

            int NodeIndex(Point2d p)
            {
                for (int i = 0; i < nodes.Count; i++)
                    if (nodes[i].GetDistanceTo(p) <= snapTol) return i;
                nodes.Add(p);
                adj.Add(new List<(int, double)>());
                return nodes.Count - 1;
            }

            foreach (var s in segs)
            {
                int a = NodeIndex(s.A);
                int b = NodeIndex(s.B);
                if (a == b) continue;
                double w = nodes[a].GetDistanceTo(nodes[b]);
                if (!adj[a].Exists(e => e.to == b)) adj[a].Add((b, w));
                if (!adj[b].Exists(e => e.to == a)) adj[b].Add((a, w));
            }

            if (nodes.Count < 2)
                return LongestSpine(spinePolys);

            // Tree diameter via two farthest-node searches.
            int u = FarthestNode(adj, 0, out _, out _);
            int v = FarthestNode(adj, u, out _, out int[] parent);

            var path = new List<Point2d>();
            for (int n = v; n != -1; n = parent[n])
                path.Add(nodes[n]);
            path.Reverse();

            if (path.Count < 2)
                return LongestSpine(spinePolys);
            return path;
        }

        private static int FarthestNode(List<List<(int to, double w)>> adj, int start, out double maxDist, out int[] parent)
        {
            int n = adj.Count;
            var dist = new double[n];
            parent = new int[n];
            var visited = new bool[n];
            for (int i = 0; i < n; i++) { dist[i] = double.MaxValue; parent[i] = -1; }
            dist[start] = 0;

            var stack = new Stack<int>();
            stack.Push(start);
            visited[start] = true;
            int best = start;
            double bestD = 0;
            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                if (dist[cur] > bestD) { bestD = dist[cur]; best = cur; }
                foreach (var (to, w) in adj[cur])
                {
                    if (visited[to]) continue;
                    visited[to] = true;
                    dist[to] = dist[cur] + w;
                    parent[to] = cur;
                    stack.Push(to);
                }
            }
            maxDist = bestD;
            return best;
        }

        private static List<Point2d> LongestSpine(List<List<Point2d>> spinePolys)
        {
            List<Point2d> best = null;
            double bestLen = -1;
            foreach (var poly in spinePolys)
            {
                double len = 0;
                for (int i = 0; i + 1 < poly.Count; i++) len += poly[i].GetDistanceTo(poly[i + 1]);
                if (len > bestLen) { bestLen = len; best = poly; }
            }
            return best;
        }

        // ── Columns ─────────────────────────────────────────────────────────────────

        private static List<Column> BuildColumns(
            List<List<Point2d>> columnPolys,
            List<Point2d> sprinklers,
            List<Point2d> chain,
            double[] arc,
            bool columnsVertical,
            double snapTol)
        {
            var cols = new List<Column>();
            foreach (var poly in columnPolys)
            {
                // Endpoint of the column nearest the main network is the tap.
                Point2d nearest = poly[0];
                double bestD = double.MaxValue;
                foreach (var p in poly)
                {
                    ProjectOntoChain(chain, arc, p, out _, out _, out double d);
                    if (d < bestD) { bestD = d; nearest = p; }
                }

                double cross = columnsVertical ? nearest.X : nearest.Y;

                // Merge with an existing column sharing the cross-coordinate; keep the closest tap.
                Column match = null;
                foreach (var col in cols)
                    if (Math.Abs(col.Cross - cross) <= snapTol) { match = col; break; }

                ProjectOntoChain(chain, arc, nearest, out Point2d tap, out double sArc, out double tapDist);
                if (match == null)
                {
                    cols.Add(new Column { Cross = cross, Tap = tap, ArcS = sArc, Count = 0 });
                }
                else if (tapDist < match.Tap.GetDistanceTo(nearest))
                {
                    match.Tap = tap;
                    match.ArcS = sArc;
                }
            }

            foreach (var col in cols)
                col.Count = CountHeadsInColumn(col.Cross, columnsVertical, sprinklers, snapTol);

            return cols;
        }

        private static int CountHeadsInColumn(double cross, bool columnsVertical, List<Point2d> sprinklers, double snapTol)
        {
            double tol = snapTol > 0 ? snapTol : 1e-6;
            int n = 0;
            foreach (var s in sprinklers)
            {
                double sc = columnsVertical ? s.X : s.Y;
                if (Math.Abs(sc - cross) <= tol) n++;
            }
            return n;
        }

        // ── Chain geometry ──────────────────────────────────────────────────────────

        private static void ProjectOntoChain(
            List<Point2d> chain, double[] arc, Point2d p,
            out Point2d foot, out double sArc, out double dist)
        {
            foot = chain[0];
            sArc = 0;
            dist = double.MaxValue;
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                var a = chain[i];
                var b = chain[i + 1];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double lenSq = dx * dx + dy * dy;
                double t = lenSq < 1e-12 ? 0 : ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
                t = Math.Max(0, Math.Min(1, t));
                var cp = new Point2d(a.X + t * dx, a.Y + t * dy);
                double d = cp.GetDistanceTo(p);
                if (d < dist)
                {
                    dist = d;
                    foot = cp;
                    sArc = arc[i] + t * Math.Sqrt(lenSq);
                }
            }
        }

        private static void PointAtArc(List<Point2d> chain, double[] arc, double s, out Point2d pt, out Vector2d dir)
        {
            double total = arc[arc.Length - 1];
            s = Math.Max(0, Math.Min(total, s));
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                if (s <= arc[i + 1] || i + 2 == chain.Count)
                {
                    double segLen = arc[i + 1] - arc[i];
                    double t = segLen < 1e-12 ? 0 : (s - arc[i]) / segLen;
                    var a = chain[i];
                    var b = chain[i + 1];
                    pt = new Point2d(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));
                    var v = new Vector2d(b.X - a.X, b.Y - a.Y);
                    double l = v.Length;
                    dir = l < 1e-12 ? new Vector2d(1, 0) : new Vector2d(v.X / l, v.Y / l);
                    return;
                }
            }
            pt = chain[chain.Count - 1];
            dir = new Vector2d(1, 0);
        }

        // ── Misc helpers ────────────────────────────────────────────────────────────

        private static List<Point2d> GetEntityPoints(Entity ent)
        {
            if (ent is Polyline pl)
            {
                var pts = new List<Point2d>();
                for (int i = 0; i < pl.NumberOfVertices; i++)
                    pts.Add(new Point2d(pl.GetPoint2dAt(i).X, pl.GetPoint2dAt(i).Y));
                return pts;
            }
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

        private static void EraseExistingMainPipeLabels(Transaction tr, BlockTableRecord ms, string zoneBoundaryHex)
        {
            var toErase = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                MText mt = null;
                try { mt = tr.GetObject(id, OpenMode.ForRead, false) as MText; } catch { continue; }
                if (mt == null) continue;
                if (!string.Equals(mt.Layer, SprinklerLayers.McdLabelLayer, StringComparison.OrdinalIgnoreCase)) continue;
                if (!SprinklerXData.IsTaggedMainPipeScheduleLabel(mt)) continue;
                if (!string.IsNullOrEmpty(zoneBoundaryHex))
                {
                    if (!SprinklerXData.TryGetZoneBoundaryHandle(mt, out string h) ||
                        !string.Equals(h, zoneBoundaryHex, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                toErase.Add(id);
            }
            foreach (var id in toErase)
            {
                try { var e = tr.GetObject(id, OpenMode.ForWrite, false) as Entity; e?.Erase(); } catch { }
            }
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
