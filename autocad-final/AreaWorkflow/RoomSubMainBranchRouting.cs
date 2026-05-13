using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// When room heads cannot be fed directly from the zone main, draws an orthogonal L-shaped feeder on the
    /// main-pipe layer (tagged as trunk + zone) from a tap on an existing main into the room, then orthogonal
    /// branch segments from that feeder to each tagged head inside the room outline.
    /// </summary>
    public static class RoomSubMainBranchRouting
    {
        private const int InsideSamples = 14;

        /// <summary>Same rules as interactive room sub-main: main layer polyline, not a trunk cap.</summary>
        public static bool IsEligibleTapMain(Polyline pl)
        {
            if (pl == null || pl.IsErased) return false;
            if (!SprinklerLayers.IsMainPipeLayerName(pl.Layer)) return false;
            if (SprinklerXData.IsTaggedTrunkCap(pl)) return false;
            return true;
        }

        /// <summary>
        /// Uses <paramref name="zoneRing"/> to validate the feeder path; uses <paramref name="roomRing"/> for head picks and in-room branches.
        /// </summary>
        /// <param name="onlyTheseHeadIds">When non-null and non-empty, only those heads are branched (feeder still runs for the room).</param>
        /// <param name="headsReceivedBranch">Head entity ids that received a new branch polyline (may be empty).</param>
        public static bool TryRouteFeederAndBranches(
            Transaction tr,
            Database db,
            BlockTableRecord ms,
            Polyline roomOutline,
            Polyline mainPolyline,
            List<Point2d> roomRing,
            List<Point2d> zoneRing,
            string parentZoneBoundaryHex,
            ICollection<ObjectId> onlyTheseHeadIds,
            out int feederVertexCount,
            out int branchPolylinesCreated,
            out List<ObjectId> headsReceivedBranch,
            out string errorMessage)
        {
            feederVertexCount = 0;
            branchPolylinesCreated = 0;
            headsReceivedBranch = new List<ObjectId>();
            errorMessage = null;

            if (tr == null || db == null || ms == null || roomOutline == null || mainPolyline == null
                || roomRing == null || roomRing.Count < 3 || zoneRing == null || zoneRing.Count < 3
                || string.IsNullOrWhiteSpace(parentZoneBoundaryHex))
            {
                errorMessage = "Invalid inputs for room sub-main routing.";
                return false;
            }

            if (onlyTheseHeadIds != null && onlyTheseHeadIds.Count == 0)
            {
                errorMessage = "No target heads for room sub-main routing.";
                return false;
            }

            SprinklerXData.EnsureRegApp(tr, db);
            var heads = new List<(Point2d pt, ObjectId id)>();
            if (!TryCollectRoomHeadsForZone(tr, ms, roomRing, parentZoneBoundaryHex, onlyTheseHeadIds, heads, out string collectErr))
            {
                errorMessage = collectErr ?? "No sprinkler heads found in the room for this zone.";
                return false;
            }

            Point2d anchor = ApproxRingCentroid(roomRing);
            if (!IsPointInRing(roomRing, anchor))
            {
                errorMessage = "Room outline centroid is outside the room polygon.";
                return false;
            }

            Point3d tap3d = mainPolyline.GetClosestPointTo(new Point3d(anchor.X, anchor.Y, roomOutline.Elevation), extend: false);
            var tap = new Point2d(tap3d.X, tap3d.Y);

            if (!TryBuildOrthogonalLPathInsideRing(zoneRing, tap, anchor, out List<Point2d> feederVerts) || feederVerts == null || feederVerts.Count < 2)
            {
                errorMessage =
                    "Could not build an orthogonal L-shaped feeder from the main tap to the room anchor inside the zone. " +
                    "Adjust the main, room, or zone geometry and retry.";
                return false;
            }

            ObjectId mainLayerId = SprinklerLayers.EnsureMcdMainPipeLayer(tr, db);
            double mainW = ReadPolylineWidthOrDefault(mainPolyline, db);

            var feeder = CreatePolylineFromVerts(db, feederVerts, roomOutline.Elevation, mainLayerId, mainW);
            SprinklerXData.TagAsTrunk(feeder);
            SprinklerXData.ApplyZoneBoundaryTag(feeder, parentZoneBoundaryHex.Trim());
            ms.AppendEntity(feeder);
            tr.AddNewlyCreatedDBObject(feeder, true);
            feederVertexCount = feederVerts.Count;

            ObjectId branchLayerId = SprinklerLayers.EnsureMcdBranchPipeLayer(tr, db);
            double branchW = NfpaBranchPipeSizing.GetBranchPolylineDisplayWidthDu(db, nominalMm: 25, mainW);
            if (!(branchW > 1e-12))
                branchW = Math.Max(mainW * 0.66, 1.0);

            using (var feederRead = (Polyline)tr.GetObject(feeder.ObjectId, OpenMode.ForRead))
            {
                foreach (var (pt, headId) in heads)
                {
                    var headEnt = tr.GetObject(headId, OpenMode.ForRead, false) as Entity;
                    if (headEnt == null) continue;
                    if (!TryGetHeadPoint2d(headEnt, out Point2d headPt))
                        continue;

                    Point3d onFeed3 = feederRead.GetClosestPointTo(new Point3d(headPt.X, headPt.Y, roomOutline.Elevation), extend: false);
                    var attach = new Point2d(onFeed3.X, onFeed3.Y);

                    if (!TryBuildOrthogonalLPathInsideRing(roomRing, attach, headPt, out List<Point2d> brVerts) || brVerts == null || brVerts.Count < 2)
                        continue;

                    var br = CreatePolylineFromVerts(db, brVerts, roomOutline.Elevation, branchLayerId, branchW);
                    SprinklerXData.ApplyZoneBoundaryTag(br, parentZoneBoundaryHex.Trim());
                    ms.AppendEntity(br);
                    tr.AddNewlyCreatedDBObject(br, true);
                    branchPolylinesCreated++;
                    headsReceivedBranch.Add(headId);
                }
            }

            if (branchPolylinesCreated == 0)
            {
                try
                {
                    feeder.UpgradeOpen();
                    feeder.Erase();
                }
                catch { /* ignore */ }

                errorMessage =
                    "Feeder was created but no orthogonal branch could be routed from it to any head inside the room.";
                return false;
            }

            return true;
        }

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

                Point2d p = default;
                if (ent is Circle c)
                    p = new Point2d(c.Center.X, c.Center.Y);
                else if (ent is BlockReference br)
                {
                    if (!SprinklerLayers.IsPendentSprinklerBlock(tr, br))
                        continue;
                    p = new Point2d(br.Position.X, br.Position.Y);
                }
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

        private static bool TryGetHeadPoint2d(Entity ent, out Point2d p)
        {
            p = default;
            if (ent is BlockReference br)
            {
                p = new Point2d(br.Position.X, br.Position.Y);
                return true;
            }
            if (ent is Circle c)
            {
                p = new Point2d(c.Center.X, c.Center.Y);
                return true;
            }
            return false;
        }

        private static Point2d ApproxRingCentroid(List<Point2d> ring)
        {
            double sx = 0, sy = 0;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                sx += ring[i].X;
                sy += ring[i].Y;
            }
            return new Point2d(sx / Math.Max(1, n), sy / Math.Max(1, n));
        }

        private static bool IsPointInRing(List<Point2d> ring, Point2d p)
        {
            return FindShaftsInsideBoundary.IsPointInPolygonRing(ring, p);
        }

        /// <summary>Axis-aligned L-path from A to B with at most one corner; entire path must lie inside the ring.</summary>
        public static bool TryBuildOrthogonalLPathInsideRing(
            List<Point2d> ring,
            Point2d a,
            Point2d b,
            out List<Point2d> vertices)
        {
            vertices = null;
            if (ring == null || ring.Count < 3)
                return false;

            const double tol = 1e-7;
            if (a.GetDistanceTo(b) <= tol)
            {
                vertices = new List<Point2d> { a, b };
                return SegmentInsideRing(ring, a, b);
            }

            var c1 = new Point2d(b.X, a.Y);
            if (SegmentInsideRing(ring, a, c1) && SegmentInsideRing(ring, c1, b))
            {
                vertices = CollinearCollapse(new List<Point2d> { a, c1, b });
                return vertices.Count >= 2;
            }

            var c2 = new Point2d(a.X, b.Y);
            if (SegmentInsideRing(ring, a, c2) && SegmentInsideRing(ring, c2, b))
            {
                vertices = CollinearCollapse(new List<Point2d> { a, c2, b });
                return vertices.Count >= 2;
            }

            return false;
        }

        private static List<Point2d> CollinearCollapse(List<Point2d> v)
        {
            if (v == null || v.Count < 2) return v ?? new List<Point2d>();
            var r = new List<Point2d> { v[0] };
            for (int i = 1; i < v.Count; i++)
            {
                if (r[r.Count - 1].GetDistanceTo(v[i]) > 1e-9)
                    r.Add(v[i]);
            }
            return r;
        }

        private static bool SegmentInsideRing(List<Point2d> ring, Point2d p0, Point2d p1)
        {
            for (int i = 0; i <= InsideSamples; i++)
            {
                double t = i / (double)InsideSamples;
                double x = p0.X + (p1.X - p0.X) * t;
                double y = p0.Y + (p1.Y - p0.Y) * t;
                if (!FindShaftsInsideBoundary.IsPointInPolygonRing(ring, new Point2d(x, y)))
                    return false;
            }
            return true;
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
    }
}
