using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Agent;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Serves sprinkler heads inside a room by chaining them along the room's own (possibly tilted) grid axes and
    /// tying each chain to the nearest existing zone <strong>branch</strong> pipe (falling back to the zone main).
    /// In-room chains are built in the room's local frame so every segment stays inside a tilted room; the connector
    /// is a short jog from the chain end out to the tapped supply line. No dedicated feeder is dragged from the main.
    /// </summary>
    public static class RoomSubMainBranchRouting
    {
        private const int InsideSamples = 14;

        /// <summary>A supply line segment (world coordinates) that a room chain may tap.</summary>
        public readonly struct SupplySegment
        {
            public readonly Point2d A;
            public readonly Point2d B;
            public SupplySegment(Point2d a, Point2d b) { A = a; B = b; }
            public double Length => A.GetDistanceTo(B);
        }

        /// <summary>Main-layer polyline that is not a trunk cap is a valid main (used by callers when collecting supply).</summary>
        public static bool IsEligibleTapMain(Polyline pl)
        {
            if (pl == null || pl.IsErased) return false;
            if (!SprinklerLayers.IsMainPipeLayerName(pl.Layer)) return false;
            if (SprinklerXData.IsTaggedTrunkCap(pl)) return false;
            return true;
        }

        /// <summary>
        /// Collects the supply lines available to feed rooms in a zone: branch-pipe polylines (primary) and main
        /// polylines (fallback), each broken into segments. Inclusion = tagged to <paramref name="zoneBoundaryHex"/>,
        /// or (for untagged legacy geometry) a majority of vertices inside the zone ring.
        /// </summary>
        public static void CollectZoneSupply(
            Transaction tr,
            BlockTableRecord ms,
            List<Point2d> zoneRing,
            string zoneBoundaryHex,
            out List<SupplySegment> branchSupply,
            out List<SupplySegment> mainSupply)
        {
            branchSupply = new List<SupplySegment>();
            mainSupply = new List<SupplySegment>();
            if (tr == null || ms == null || zoneRing == null || zoneRing.Count < 3)
                return;

            string zh = string.IsNullOrWhiteSpace(zoneBoundaryHex) ? null : zoneBoundaryHex.Trim();

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (pl == null) continue;

                bool isBranch = SprinklerLayers.IsBranchPipeGeometryLayerName(pl.Layer);
                bool isMain = !isBranch && IsEligibleTapMain(pl) && !SprinklerXData.IsTaggedConnector(pl);
                if (!isBranch && !isMain) continue;
                if (!BelongsToZone(pl, zoneRing, zh)) continue;

                var target = isBranch ? branchSupply : mainSupply;
                AppendPolylineSegments(pl, target);
            }
        }

        /// <summary>
        /// Routes room-aligned branch chains for the heads inside the room ring (tagged to
        /// <paramref name="parentZoneBoundaryHex"/>) and connects each chain to the nearest supply line.
        /// </summary>
        /// <param name="onlyTheseHeadIds">When non-null and non-empty, only those heads are branched.</param>
        /// <param name="headsReceivedBranch">Head ids that received a branch chain.</param>
        public static bool TryRouteRoomBranches(
            Transaction tr,
            Database db,
            BlockTableRecord ms,
            double elevation,
            List<Point2d> roomRing,
            List<Point2d> zoneRing,
            string parentZoneBoundaryHex,
            IReadOnlyList<SupplySegment> branchSupply,
            IReadOnlyList<SupplySegment> mainSupply,
            ICollection<ObjectId> onlyTheseHeadIds,
            out int branchPolylinesCreated,
            out List<ObjectId> headsReceivedBranch,
            out string errorMessage)
        {
            branchPolylinesCreated = 0;
            headsReceivedBranch = new List<ObjectId>();
            errorMessage = null;

            if (tr == null || db == null || ms == null
                || roomRing == null || roomRing.Count < 3 || zoneRing == null || zoneRing.Count < 3
                || string.IsNullOrWhiteSpace(parentZoneBoundaryHex))
            {
                errorMessage = "Invalid inputs for room branch routing.";
                return false;
            }
            if (onlyTheseHeadIds != null && onlyTheseHeadIds.Count == 0)
            {
                errorMessage = "No target heads for room branch routing.";
                return false;
            }

            int branchCount = branchSupply?.Count ?? 0;
            int mainCount = mainSupply?.Count ?? 0;
            if (branchCount == 0 && mainCount == 0)
            {
                errorMessage = "No zone branch or main pipe available to feed the room.";
                return false;
            }

            SprinklerXData.EnsureRegApp(tr, db);

            var heads = new List<(Point2d pt, ObjectId id)>();
            if (!TryCollectRoomHeadsForZone(tr, ms, roomRing, parentZoneBoundaryHex, onlyTheseHeadIds, heads, out string collectErr))
            {
                errorMessage = collectErr ?? "No sprinkler heads found in the room for this zone.";
                return false;
            }

            // Build the room's own (possibly tilted) frame; chains are planned in this frame so they align to the
            // room grid and stay inside the room. Connectors to the (world-axis) supply lines are built in world.
            var frame = RoomLocalFrame.FromRing(roomRing);
            var roomRingL = frame.ToLocal(roomRing);

            var headsLocal = new List<(Point2d ptL, ObjectId id)>(heads.Count);
            foreach (var (pt, id) in heads)
                headsLocal.Add((frame.ToLocal(pt), id));

            double clusterTol = ComputeClusterTol(db);
            var groups = ClusterHeadsIntoChains(headsLocal, clusterTol, out bool chainAlongY);
            if (groups.Count == 0)
            {
                errorMessage = "Could not group room heads into chains.";
                return false;
            }

            double branchW = ComputeBranchWidth(db);
            ObjectId branchLayerId = SprinklerLayers.EnsureMcdBranchPipeLayer(tr, db);
            string zoneHex = parentZoneBoundaryHex.Trim();

            // One supply (entry) side shared by all rows: the row-end column nearest the supply.
            double firstTotal = 0, lastTotal = 0;
            foreach (var g in groups)
            {
                if (g.Count == 0) continue;
                firstTotal += NearestSupplyDistance(frame.ToWorld(g[0].ptL), branchSupply, mainSupply);
                lastTotal += NearestSupplyDistance(frame.ToWorld(g[g.Count - 1].ptL), branchSupply, mainSupply);
            }
            bool entryAtFirst = firstTotal <= lastTotal;

            // One chain per row, ordered so the entry (supply-side) head is first, then sorted across the
            // cross axis so the sub-main runs cleanly through the entry heads.
            var rows = new List<List<(Point2d ptL, ObjectId id)>>();
            foreach (var g in groups)
            {
                var ordered = entryAtFirst ? g : Enumerable.Reverse(g).ToList();
                var chain = BuildChain(ordered, roomRingL);
                if (chain.Count >= 1) rows.Add(chain);
            }
            if (rows.Count == 0)
            {
                errorMessage = "No room heads could be chained inside the room.";
                return false;
            }
            Func<Point2d, double> perpOf = p => chainAlongY ? p.X : p.Y;
            rows.Sort((a, b) => perpOf(a[0].ptL).CompareTo(perpOf(b[0].ptL)));

            int created = 0;

            // Preferred layout: a single sub-main through the entry heads + one tap. Only valid when every
            // consecutive entry-head link stays inside the room (convex / tilted-rectangle rooms).
            bool subMainConnected = rows.Count >= 2;
            for (int i = 0; subMainConnected && i + 1 < rows.Count; i++)
                if (!SegmentInsideRing(roomRingL, rows[i][0].ptL, rows[i + 1][0].ptL))
                    subMainConnected = false;

            if (subMainConnected)
            {
                List<Point2d> tapConnector = null;
                foreach (int idx in Enumerable.Range(0, rows.Count)
                             .OrderBy(i => NearestSupplyDistance(frame.ToWorld(rows[i][0].ptL), branchSupply, mainSupply)))
                {
                    if (TryBuildConnector(frame.ToWorld(rows[idx][0].ptL), branchSupply, mainSupply, roomRing, zoneRing, out tapConnector))
                        break;
                    tapConnector = null;
                }

                if (tapConnector != null)
                {
                    // Sub-main: entry head -> entry head, per segment.
                    for (int i = 0; i + 1 < rows.Count; i++)
                        created += DrawSegment(tr, db, ms, frame.ToWorld(rows[i][0].ptL), frame.ToWorld(rows[i + 1][0].ptL), elevation, branchLayerId, branchW, zoneHex);

                    // The single tap from the sub-main to the zone supply.
                    created += DrawPath(tr, db, ms, tapConnector, elevation, branchLayerId, branchW, zoneHex);

                    // Each row hangs off its entry head, per segment (sprinkler-to-sprinkler).
                    foreach (var row in rows)
                    {
                        created += DrawPath(tr, db, ms, row.Select(h => frame.ToWorld(h.ptL)).ToList(), elevation, branchLayerId, branchW, zoneHex);
                        foreach (var (_, id) in row) headsReceivedBranch.Add(id);
                    }

                    branchPolylinesCreated = created;
                    return true;
                }
            }

            // Fallback (single row, or a room shape where the sub-main can't stay inside): connect each row
            // to the supply with its own connector.
            foreach (var row in rows)
            {
                var rowW = row.Select(h => frame.ToWorld(h.ptL)).ToList();
                if (!TryBuildConnector(rowW[0], branchSupply, mainSupply, roomRing, zoneRing, out var connectorW))
                    continue;
                var path = new List<Point2d>(connectorW);
                for (int i = 1; i < rowW.Count; i++) path.Add(rowW[i]);
                int seg = DrawPath(tr, db, ms, path, elevation, branchLayerId, branchW, zoneHex);
                if (seg == 0) continue;
                created += seg;
                foreach (var (_, id) in row) headsReceivedBranch.Add(id);
            }

            branchPolylinesCreated = created;
            if (created == 0)
            {
                errorMessage = "No room branch could be connected to a zone branch or main.";
                return false;
            }
            return true;
        }

        /// <summary>Draws a single pipe run as its own two-point branch polyline (skips zero-length).</summary>
        private static int DrawSegment(Transaction tr, Database db, BlockTableRecord ms, Point2d a, Point2d b, double elevation, ObjectId layerId, double width, string zoneHex)
        {
            if (a.GetDistanceTo(b) <= 1e-7) return 0;
            var seg = CreatePolylineFromVerts(db, new List<Point2d> { a, b }, elevation, layerId, width);
            SprinklerXData.ApplyZoneBoundaryTag(seg, zoneHex);
            ms.AppendEntity(seg);
            tr.AddNewlyCreatedDBObject(seg, true);
            return 1;
        }

        /// <summary>Draws a polyline as one branch polyline per consecutive segment; returns segments drawn.</summary>
        private static int DrawPath(Transaction tr, Database db, BlockTableRecord ms, List<Point2d> pts, double elevation, ObjectId layerId, double width, string zoneHex)
        {
            var p = CollapseCollinear(pts);
            int n = 0;
            for (int i = 0; i + 1 < p.Count; i++)
                n += DrawSegment(tr, db, ms, p[i], p[i + 1], elevation, layerId, width, zoneHex);
            return n;
        }

        // ── Head collection ───────────────────────────────────────────────────────

        private static bool TryCollectRoomHeadsForZone(
            Transaction tr,
            BlockTableRecord ms,
            List<Point2d> roomRing,
            string zoneHex,
            ICollection<ObjectId> onlyTheseHeadIds,
            List<(Point2d pt, ObjectId id)> outHeads,
            out string errorMessage)
        {
            outHeads.Clear();
            errorMessage = null;
            string zh = zoneHex.Trim();
            bool restrict = onlyTheseHeadIds != null && onlyTheseHeadIds.Count > 0;

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                if (restrict && !onlyTheseHeadIds.Contains(id))
                    continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null) continue;

                if (!SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                    continue;
                if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out string h) || string.IsNullOrWhiteSpace(h))
                    continue;
                if (!string.Equals(h.Trim(), zh, StringComparison.OrdinalIgnoreCase))
                    continue;

                Point2d p;
                if (ent is Circle c)
                    p = new Point2d(c.Center.X, c.Center.Y);
                else if (ent is BlockReference br)
                    p = new Point2d(br.Position.X, br.Position.Y);
                else
                    continue;

                if (!IsPointInRing(roomRing, p))
                    continue;
                outHeads.Add((p, id));
            }

            if (outHeads.Count == 0)
            {
                errorMessage =
                    "No pendent sprinkler heads inside the room carry BOUNDARY xdata matching the parent zone. " +
                    "Use \"Place sprinklers for rooms\" first.";
                return false;
            }

            return true;
        }

        // ── Clustering + chains (room-local frame) ─────────────────────────────────

        /// <summary>Clusters heads into rows or columns (whichever yields fewer, longer chains), each ordered along its axis.</summary>
        private static List<List<(Point2d ptL, ObjectId id)>> ClusterHeadsIntoChains(
            List<(Point2d ptL, ObjectId id)> headsLocal,
            double tol,
            out bool chainAlongY)
        {
            var rows = ClusterByKey(headsLocal, h => h.ptL.Y, tol);
            var cols = ClusterByKey(headsLocal, h => h.ptL.X, tol);

            bool useCols = ScoreGrouping(cols) < ScoreGrouping(rows);
            chainAlongY = useCols; // columns chain along Y; rows chain along X
            var chosen = useCols ? cols : rows;

            foreach (var g in chosen)
            {
                if (useCols) g.Sort((a, b) => a.ptL.Y.CompareTo(b.ptL.Y)); // column → order by Y
                else g.Sort((a, b) => a.ptL.X.CompareTo(b.ptL.X));         // row → order by X
            }
            return chosen;
        }

        private static List<List<(Point2d ptL, ObjectId id)>> ClusterByKey(
            List<(Point2d ptL, ObjectId id)> heads,
            Func<(Point2d ptL, ObjectId id), double> key,
            double tol)
        {
            var result = new List<List<(Point2d, ObjectId)>>();
            if (heads == null || heads.Count == 0) return result;
            if (!(tol > 0)) tol = 1.0;

            var sorted = heads.OrderBy(key).ToList();
            List<(Point2d, ObjectId)> current = null;
            double currentKey = 0;
            foreach (var h in sorted)
            {
                double k = key(h);
                if (current == null || Math.Abs(k - currentKey) > tol)
                {
                    current = new List<(Point2d, ObjectId)> { h };
                    result.Add(current);
                    currentKey = k;
                }
                else
                {
                    current.Add(h);
                    currentKey = current.Average(x => key(x)); // running average for stability
                }
            }
            return result;
        }

        private static double ScoreGrouping(List<List<(Point2d ptL, ObjectId id)>> groups)
        {
            if (groups == null || groups.Count == 0) return double.PositiveInfinity;
            double avg = groups.Average(g => (double)g.Count);
            return groups.Count - avg; // fewer groups + longer chains → lower (better)
        }

        private static List<(Point2d ptL, ObjectId id)> BuildChain(
            List<(Point2d ptL, ObjectId id)> orderedGroup,
            List<Point2d> roomRingL)
        {
            var chain = new List<(Point2d ptL, ObjectId id)>();
            if (orderedGroup == null || orderedGroup.Count == 0) return chain;

            chain.Add(orderedGroup[0]);
            for (int i = 1; i < orderedGroup.Count; i++)
            {
                if (!SegmentInsideRing(roomRingL, chain[chain.Count - 1].ptL, orderedGroup[i].ptL))
                    break;
                chain.Add(orderedGroup[i]);
            }
            return chain;
        }

        // ── Connector to the nearest supply (world frame) ──────────────────────────

        private static bool TryBuildConnector(
            Point2d endpoint,
            IReadOnlyList<SupplySegment> branchSupply,
            IReadOnlyList<SupplySegment> mainSupply,
            List<Point2d> roomRing,
            List<Point2d> zoneRing,
            out List<Point2d> connector)
        {
            connector = null;

            // Branches first (preferred), then mains (fallback); within each, nearest segment first.
            var ordered = OrderByDistance(branchSupply, endpoint)
                .Concat(OrderByDistance(mainSupply, endpoint));

            const double tol = 1e-7;
            foreach (var seg in ordered)
            {
                var tap = ClosestPointOnSegment(seg.A, seg.B, endpoint);
                if (tap.GetDistanceTo(endpoint) <= tol)
                {
                    connector = new List<Point2d> { tap, endpoint };
                    return true;
                }

                // Prefer an orthogonal L (clean jog), else a straight connector.
                var corner1 = new Point2d(endpoint.X, tap.Y);
                var path1 = CollapseCollinear(new List<Point2d> { tap, corner1, endpoint });
                if (PathInsideRoomOrZone(path1, roomRing, zoneRing)) { connector = path1; return true; }

                var corner2 = new Point2d(tap.X, endpoint.Y);
                var path2 = CollapseCollinear(new List<Point2d> { tap, corner2, endpoint });
                if (PathInsideRoomOrZone(path2, roomRing, zoneRing)) { connector = path2; return true; }

                var straight = new List<Point2d> { tap, endpoint };
                if (PathInsideRoomOrZone(straight, roomRing, zoneRing)) { connector = straight; return true; }
            }

            return false;
        }

        private static IEnumerable<SupplySegment> OrderByDistance(IReadOnlyList<SupplySegment> segs, Point2d p)
        {
            if (segs == null) return Enumerable.Empty<SupplySegment>();
            return segs.Where(s => s.Length > 1e-9).OrderBy(s => DistancePointToSegment(s, p));
        }

        private static double NearestSupplyDistance(
            Point2d p,
            IReadOnlyList<SupplySegment> branchSupply,
            IReadOnlyList<SupplySegment> mainSupply)
        {
            double best = double.PositiveInfinity;
            if (branchSupply != null)
                foreach (var s in branchSupply) best = Math.Min(best, DistancePointToSegment(s, p));
            if (mainSupply != null)
                foreach (var s in mainSupply) best = Math.Min(best, DistancePointToSegment(s, p));
            return best;
        }

        private static double DistancePointToSegment(SupplySegment s, Point2d p)
            => ClosestPointOnSegment(s.A, s.B, p).GetDistanceTo(p);

        // ── Supply collection helpers ──────────────────────────────────────────────

        private static bool BelongsToZone(Polyline pl, List<Point2d> zoneRing, string zoneHex)
        {
            if (!string.IsNullOrWhiteSpace(zoneHex)
                && SprinklerXData.TryGetZoneBoundaryHandle(pl, out string h)
                && !string.IsNullOrWhiteSpace(h))
            {
                return string.Equals(h.Trim(), zoneHex, StringComparison.OrdinalIgnoreCase);
            }

            // Untagged legacy geometry: include when a majority of its vertices lie inside the zone ring.
            int n = pl.NumberOfVertices;
            if (n < 1) return false;
            int inside = 0, counted = 0;
            for (int i = 0; i < n; i++)
            {
                Point2d v;
                try { v = pl.GetPoint2dAt(i); } catch { continue; }
                counted++;
                if (IsPointInRing(zoneRing, v)) inside++;
            }
            return counted > 0 && inside * 2 >= counted;
        }

        private static void AppendPolylineSegments(Polyline pl, List<SupplySegment> outSegs)
        {
            int n = pl.NumberOfVertices;
            for (int i = 0; i + 1 < n; i++)
            {
                var a = pl.GetPoint2dAt(i);
                var b = pl.GetPoint2dAt(i + 1);
                if (a.GetDistanceTo(b) > 1e-9) outSegs.Add(new SupplySegment(a, b));
            }
            if (pl.Closed && n > 1)
            {
                var a = pl.GetPoint2dAt(n - 1);
                var b = pl.GetPoint2dAt(0);
                if (a.GetDistanceTo(b) > 1e-9) outSegs.Add(new SupplySegment(a, b));
            }
        }

        // ── Geometry utilities ──────────────────────────────────────────────────────

        private static bool IsPointInRing(List<Point2d> ring, Point2d p)
            => FindShaftsInsideBoundary.IsPointInPolygonRing(ring, p);

        private static bool SegmentInsideRing(List<Point2d> ring, Point2d p0, Point2d p1)
        {
            for (int i = 0; i <= InsideSamples; i++)
            {
                double t = i / (double)InsideSamples;
                var p = new Point2d(p0.X + (p1.X - p0.X) * t, p0.Y + (p1.Y - p0.Y) * t);
                if (!FindShaftsInsideBoundary.IsPointInPolygonRing(ring, p))
                    return false;
            }
            return true;
        }

        private static bool PathInsideRoomOrZone(List<Point2d> path, List<Point2d> roomRing, List<Point2d> zoneRing)
        {
            if (path == null || path.Count < 2) return false;
            for (int s = 0; s + 1 < path.Count; s++)
            {
                var p0 = path[s];
                var p1 = path[s + 1];
                for (int i = 0; i <= InsideSamples; i++)
                {
                    double t = i / (double)InsideSamples;
                    var p = new Point2d(p0.X + (p1.X - p0.X) * t, p0.Y + (p1.Y - p0.Y) * t);
                    if (!IsPointInRing(roomRing, p) && !IsPointInRing(zoneRing, p))
                        return false;
                }
            }
            return true;
        }

        private static Point2d ClosestPointOnSegment(Point2d a, Point2d b, Point2d p)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-12) return a;
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return new Point2d(a.X + t * dx, a.Y + t * dy);
        }

        private static List<Point2d> CollapseCollinear(List<Point2d> verts)
        {
            if (verts == null || verts.Count < 2) return verts ?? new List<Point2d>();
            var res = new List<Point2d> { verts[0] };
            for (int i = 1; i < verts.Count; i++)
            {
                if (res[res.Count - 1].GetDistanceTo(verts[i]) > 1e-9)
                    res.Add(verts[i]);
            }
            return res;
        }

        private static double ComputeClusterTol(Database db)
        {
            double coincident = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            double spacingDu = 1.0;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, RuntimeSettings.Load().SprinklerSpacingM, out double s) && s > 0)
                    spacingDu = s;
            }
            catch { /* ignore */ }
            return Math.Max(coincident * 10.0, spacingDu * 0.35);
        }

        private static double ComputeBranchWidth(Database db)
        {
            double mainW = NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
            if (!(mainW > 0)) mainW = 1.0;
            double branchW = NfpaBranchPipeSizing.GetBranchPolylineDisplayWidthDu(db, nominalMm: 25, mainW);
            if (!(branchW > 1e-12)) branchW = Math.Max(mainW * 0.66, 1.0);
            return branchW;
        }

        private static Polyline CreatePolylineFromVerts(
            Database db,
            List<Point2d> verts,
            double elevation,
            ObjectId layerId,
            double width)
        {
            var pl = new Polyline();
            pl.SetDatabaseDefaults(db);
            pl.LayerId = layerId;
            pl.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
            pl.ConstantWidth = width;
            pl.Elevation = elevation;
            pl.Closed = false;
            for (int i = 0; i < verts.Count; i++)
                pl.AddVertexAt(i, verts[i], 0, 0, 0);
            return pl;
        }
    }
}
