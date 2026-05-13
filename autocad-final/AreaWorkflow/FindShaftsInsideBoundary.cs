using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    public static class FindShaftsInsideBoundary
    {
        /// <summary>Shaft block insert (or virtual hint): position, optional 2D extents, and block handle when applicable.</summary>
        public readonly struct ShaftBlockInfo
        {
            public readonly Point3d Position;
            public readonly bool HasExtents;
            public readonly Extents2d Extents2d;
            /// <summary>Insert database handle (hex), or null for session shaft hints.</summary>
            public readonly string BlockHandleHex;

            public ShaftBlockInfo(Point3d position, bool hasExtents, Extents2d extents2d, string blockHandleHex = null)
            {
                Position = position;
                HasExtents = hasExtents;
                Extents2d = extents2d;
                BlockHandleHex = blockHandleHex;
            }
        }

        /// <summary>
        /// Same rule as zoning / <see cref="GetShaftBlocksInsideBoundary"/>: block name contains "shaft",
        /// or layer name contains "shaft", or layer is <see cref="SprinklerLayers.McdShaftsLayer"/>.
        /// </summary>
        public static bool IsShaftBlockInsert(BlockReference br, Transaction tr)
        {
            if (br == null || tr == null) return false;
            string blockName = GetBlockName(br, tr);
            string brLayer = br.Layer ?? string.Empty;
            bool blockMatch = blockName.IndexOf("shaft", StringComparison.OrdinalIgnoreCase) >= 0;
            bool layerMatch = brLayer.IndexOf("shaft", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(brLayer, SprinklerLayers.McdShaftsLayer, StringComparison.OrdinalIgnoreCase);
            return blockMatch || layerMatch;
        }

        /// <summary>
        /// Block inserts whose insertion point lies inside the closed boundary (plan), with optional extents for shaft overlap avoidance.
        /// Also merges any virtual shaft hints registered via <see cref="autocad_final.Agent.ShaftHintStore"/>.
        /// </summary>
        public static List<ShaftBlockInfo> GetShaftBlocksInsideBoundary(Database db, Polyline boundary)
        {
            if (boundary == null)
                return new List<ShaftBlockInfo>();
            return GetShaftBlocksInsidePolygonRing(db, GetPolygon2d(boundary));
        }

        /// <summary>
        /// Same as <see cref="GetShaftBlocksInsideBoundary"/> but for an arbitrary closed 2D ring (plan).
        /// </summary>
        public static List<ShaftBlockInfo> GetShaftBlocksInsidePolygonRing(Database db, IList<Point2d> polygon)
        {
            var blocks = new List<ShaftBlockInfo>();
            if (polygon == null || polygon.Count < 3)
                return blocks;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (!(ent is BlockReference br)) continue;

                    if (!IsShaftBlockInsert(br, tr))
                        continue;

                    var p2 = new Point2d(br.Position.X, br.Position.Y);

                    bool hasExt = false;
                    Extents2d e2 = default;
                    try
                    {
                        var e3 = br.GeometricExtents;
                        e2 = new Extents2d(new Point2d(e3.MinPoint.X, e3.MinPoint.Y), new Point2d(e3.MaxPoint.X, e3.MaxPoint.Y));
                        hasExt = true;
                    }
                    catch
                    {
                        hasExt = false;
                    }

                    bool inZone = IsPointInPolygonRing(polygon, p2);
                    if (!inZone && hasExt)
                    {
                        var mn = e2.MinPoint;
                        var mx = e2.MaxPoint;
                        inZone = IsPointInPolygonRing(polygon, mn)
                              || IsPointInPolygonRing(polygon, mx)
                              || IsPointInPolygonRing(polygon, new Point2d(mx.X, mn.Y))
                              || IsPointInPolygonRing(polygon, new Point2d(mn.X, mx.Y))
                              || ZoneRingCrossesBox(polygon, mn, mx);
                    }
                    if (!inZone)
                        continue;

                    blocks.Add(new ShaftBlockInfo(br.Position, hasExt, e2, br.Handle.ToString()));
                }
                tr.Commit();
            }

            foreach (var hint in autocad_final.Agent.ShaftHintStore.GetAll())
            {
                var p2 = new Point2d(hint.X, hint.Y);
                if (IsPointInPolygonRing(polygon, p2))
                    blocks.Add(new ShaftBlockInfo(hint, false, default));
            }

            return blocks;
        }

        /// <summary>
        /// Positions of <c>shaft</c> block inserts whose insertion point lies inside the closed boundary (plan).
        /// </summary>
        public static List<Point3d> GetShaftPositionsInsideBoundary(Database db, Polyline boundary)
        {
            var blocks = GetShaftBlocksInsideBoundary(db, boundary);
            var points = new List<Point3d>(blocks.Count);
            foreach (var b in blocks)
                points.Add(b.Position);
            return points;
        }

        /// <summary>
        /// Parallel shaft positions and block handles (hex), same order as <see cref="GetShaftBlocksInsideBoundary"/>.
        /// Handle is null for session shaft hints (no block to assign on zone).
        /// </summary>
        public static void GetShaftHandlesAndPositionsInsideBoundary(
            Database db,
            Polyline boundary,
            out List<Point3d> positions,
            out List<string> handlesHex)
        {
            var blocks = GetShaftBlocksInsideBoundary(db, boundary);
            positions = new List<Point3d>(blocks.Count);
            handlesHex = new List<string>(blocks.Count);
            foreach (var b in blocks)
            {
                positions.Add(b.Position);
                handlesHex.Add(b.BlockHandleHex);
            }
        }

        public static void Run(Database db, Polyline boundary, out int count, out string coordinates)
        {
            var points = GetShaftPositionsInsideBoundary(db, boundary);
            count = points.Count;
            coordinates = FormatCoordinates(points);
        }

        /// <summary>
        /// Returns the shaft that should feed this zone, using a three-priority chain:
        /// 1) Explicit xdata assignment on the zone boundary polyline (persists across sessions).
        /// 2) Any shaft block whose insertion point lies inside the zone boundary.
        /// 3) Fallback: returns false so the caller can try global nearest-shaft search.
        /// The returned <see cref="ShaftBlockInfo"/> position is the real block position — even
        /// when the shaft is outside the zone — so the connector can start there.
        /// </summary>
        public static bool TryGetShaftForZone(
            Database db,
            string zoneBoundaryHandle,
            Polyline boundary,
            out ShaftBlockInfo shaft,
            out string errorMessage)
        {
            shaft = default;
            errorMessage = null;

            if (db == null || string.IsNullOrEmpty(zoneBoundaryHandle) || boundary == null)
            {
                errorMessage = "Invalid inputs to TryGetShaftForZone.";
                return false;
            }

            // ── Priority 1: explicit xdata assignment ─────────────────────────────
            using (var tr = db.TransactionManager.StartTransaction())
            {
                // Resolve zone boundary by handle.
                Handle h;
                try { h = new Handle(Convert.ToInt64(zoneBoundaryHandle, 16)); }
                catch { h = default; }

                if (h.Value != 0)
                {
                    ObjectId boundaryId = ObjectId.Null;
                    try { boundaryId = db.GetObjectId(false, h, 0); } catch { /* ignore */ }

                    if (!boundaryId.IsNull)
                    {
                        var boundaryEnt = tr.GetObject(boundaryId, OpenMode.ForRead, false) as Entity;
                        if (boundaryEnt != null && SprinklerXData.TryGetShaftAssignmentHandle(boundaryEnt, out string assignedShaftHandle))
                        {
                            Handle sh;
                            try { sh = new Handle(Convert.ToInt64(assignedShaftHandle, 16)); }
                            catch { sh = default; }

                            if (sh.Value != 0)
                            {
                                ObjectId shaftId = ObjectId.Null;
                                try { shaftId = db.GetObjectId(false, sh, 0); } catch { /* ignore */ }

                                if (!shaftId.IsNull)
                                {
                                    var br = tr.GetObject(shaftId, OpenMode.ForRead, false) as BlockReference;
                                    if (br != null)
                                    {
                                        bool hasExt = false;
                                        Extents2d e2 = default;
                                        try
                                        {
                                            var e3 = br.GeometricExtents;
                                            e2 = new Extents2d(
                                                new Point2d(e3.MinPoint.X, e3.MinPoint.Y),
                                                new Point2d(e3.MaxPoint.X, e3.MaxPoint.Y));
                                            hasExt = true;
                                        }
                                        catch { }

                                        shaft = new ShaftBlockInfo(br.Position, hasExt, e2, br.Handle.ToString());
                                        tr.Commit();
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }

                tr.Commit();
            }

            // ── Priority 2: shaft block inside the zone boundary ──────────────────
            var insideList = GetShaftBlocksInsideBoundary(db, boundary);
            if (insideList.Count > 0)
            {
                // Pick the one nearest the zone centroid.
                var polygon = GetPolygon2d(boundary);
                double cx = 0, cy = 0;
                foreach (var v in polygon) { cx += v.X; cy += v.Y; }
                cx /= polygon.Count; cy /= polygon.Count;

                ShaftBlockInfo best = insideList[0];
                double bestD2 = double.MaxValue;
                foreach (var s in insideList)
                {
                    double dx = s.Position.X - cx, dy = s.Position.Y - cy;
                    double d2 = dx * dx + dy * dy;
                    if (d2 < bestD2) { bestD2 = d2; best = s; }
                }
                shaft = best;
                return true;
            }

            // ── Priority 3: session shaft hints inside the boundary ───────────────
            var polygon2 = GetPolygon2d(boundary);
            foreach (var hint in autocad_final.Agent.ShaftHintStore.GetAll())
            {
                var p2 = new Point2d(hint.X, hint.Y);
                if (IsPointInPolygonRing(polygon2, p2))
                {
                    shaft = new ShaftBlockInfo(hint, false, default);
                    return true;
                }
            }

            errorMessage = "No shaft found for this zone. Use ASSIGNSHAFTOZONE to assign one.";
            return false;
        }

        /// <summary>
        /// All shaft block insertion points (name/layer contains "shaft") plus session <see cref="autocad_final.Agent.ShaftHintStore"/> hints,
        /// deduplicated in 2D — used when no floor polyline is selected yet (auto boundary detection).
        /// </summary>
        public static List<Point2d> CollectGlobalShaftSitePoints2d(Database db)
        {
            var raw = new List<Point3d>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (!(ent is BlockReference br))
                        continue;

                    if (!IsShaftBlockInsert(br, tr))
                        continue;

                    raw.Add(br.Position);
                }

                tr.Commit();
            }

            foreach (var hint in autocad_final.Agent.ShaftHintStore.GetAll())
                raw.Add(hint);

            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            if (tol <= 0)
                tol = 1e-6;

            var result = new List<Point2d>();
            foreach (var p in raw)
            {
                var p2 = new Point2d(p.X, p.Y);
                bool dup = false;
                foreach (var q in result)
                {
                    if (Math.Abs(q.X - p2.X) <= tol && Math.Abs(q.Y - p2.Y) <= tol)
                    {
                        dup = true;
                        break;
                    }
                }

                if (!dup)
                    result.Add(p2);
            }

            return result;
        }

        private static string GetBlockName(BlockReference br, Transaction tr)
        {
            var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
            if (!br.IsDynamicBlock)
                return btr.Name;

            // For dynamic blocks, use the dynamic definition name when available.
            if (!br.DynamicBlockTableRecord.IsNull)
            {
                var dyn = (BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead);
                return dyn.Name;
            }
            return btr.Name;
        }

        private static List<Point2d> GetPolygon2d(Polyline pl)
        {
            var poly = new List<Point2d>(pl.NumberOfVertices);
            for (int i = 0; i < pl.NumberOfVertices; i++)
                poly.Add(pl.GetPoint2dAt(i));
            return poly;
        }

        /// <summary>
        /// Returns true if any edge of <paramref name="ring"/> crosses the axis-aligned box
        /// defined by <paramref name="boxMin"/>/<paramref name="boxMax"/>.
        /// </summary>
        private static bool ZoneRingCrossesBox(IList<Point2d> ring, Point2d boxMin, Point2d boxMax)
        {
            if (ring == null || ring.Count < 2) return false;
            double bx0 = Math.Min(boxMin.X, boxMax.X), bx1 = Math.Max(boxMin.X, boxMax.X);
            double by0 = Math.Min(boxMin.Y, boxMax.Y), by1 = Math.Max(boxMin.Y, boxMax.Y);
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = ring[j]; var b = ring[i];
                double ax = Math.Min(a.X, b.X), bxMax = Math.Max(a.X, b.X);
                double ay = Math.Min(a.Y, b.Y), byMax = Math.Max(a.Y, b.Y);
                if (bxMax < bx0 || ax > bx1 || byMax < by0 || ay > by1) continue;
                // Segment intersects box AABB — do a line-vs-box clip test
                if (SegmentIntersectsBox(a, b, bx0, bx1, by0, by1)) return true;
            }
            return false;
        }

        private static bool SegmentIntersectsBox(Point2d a, Point2d b,
            double bx0, double bx1, double by0, double by1)
        {
            if ((a.X >= bx0 && a.X <= bx1 && a.Y >= by0 && a.Y <= by1) ||
                (b.X >= bx0 && b.X <= bx1 && b.Y >= by0 && b.Y <= by1))
                return true;
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double tmin = 0, tmax = 1;
            if (Math.Abs(dx) > 1e-12)
            {
                double t0 = (bx0 - a.X) / dx, t1 = (bx1 - a.X) / dx;
                if (t0 > t1) { var tmp = t0; t0 = t1; t1 = tmp; }
                tmin = Math.Max(tmin, t0); tmax = Math.Min(tmax, t1);
                if (tmin > tmax) return false;
            }
            else if (a.X < bx0 || a.X > bx1) return false;
            if (Math.Abs(dy) > 1e-12)
            {
                double t0 = (by0 - a.Y) / dy, t1 = (by1 - a.Y) / dy;
                if (t0 > t1) { var tmp = t0; t0 = t1; t1 = tmp; }
                tmin = Math.Max(tmin, t0); tmax = Math.Min(tmax, t1);
                if (tmin > tmax) return false;
            }
            else if (a.Y < by0 || a.Y > by1) return false;
            return tmin <= tmax;
        }

        /// <summary>Planar point-in-polygon (non-self-intersecting rings).</summary>
        public static bool IsPointInPolygonRing(IList<Point2d> poly, Point2d pt)
        {
            int n = poly.Count;
            if (n < 3) return false;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                bool intersect =
                    ((poly[i].Y > pt.Y) != (poly[j].Y > pt.Y)) &&
                    (pt.X < (poly[j].X - poly[i].X) * (pt.Y - poly[i].Y) / ((poly[j].Y - poly[i].Y) + 1e-12) + poly[i].X);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        /// <summary>
        /// Every shaft block insert in model space matching <see cref="IsShaftBlockInsert"/> (no boundary filter).
        /// Used when no floor ring can be resolved for palette scans.
        /// </summary>
        public static List<ShaftBlockInfo> GetAllShaftBlockInsertsInModelSpace(Database db)
        {
            var blocks = new List<ShaftBlockInfo>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (!(ent is BlockReference br)) continue;
                    if (!IsShaftBlockInsert(br, tr)) continue;
                    blocks.Add(new ShaftBlockInfo(br.Position, false, default, br.Handle.ToString()));
                }

                tr.Commit();
            }

            return blocks;
        }

        /// <summary>
        /// Reads a closed model-space polyline vertex ring by handle (hex). Used to scope shaft lists to the floor parcel
        /// stored in xdata when several floors exist in one drawing.
        /// </summary>
        public static bool TryGetClosedPolylineRingByHandle(Database db, string polylineHandleHex, out List<Point2d> ring)
        {
            ring = null;
            if (db == null || string.IsNullOrWhiteSpace(polylineHandleHex))
                return false;
            if (!long.TryParse(polylineHandleHex.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hval))
                return false;
            try
            {
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var h = new Handle(hval);
                    if (!db.TryGetObjectId(h, out ObjectId id) || id.IsNull)
                        return false;
                    Polyline pl;
                    try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch { return false; }
                    if (pl == null || pl.IsErased || !pl.Closed)
                        return false;
                    List<Point2d> r;
                    try { r = GetPolygon2d(pl); }
                    catch { return false; }
                    if (r == null || r.Count < 3)
                        return false;
                    ring = new List<Point2d>(r);
                    tr.Commit();
                }
            }
            catch
            {
                ring = null;
                return false;
            }
            return ring != null && ring.Count >= 3;
        }

        /// <summary>
        /// Among closed polylines on <see cref="SprinklerLayers.McdFloorBoundaryLayer"/>, <see cref="SprinklerLayers.WorkLayer"/>,
        /// or <see cref="SprinklerLayers.BoundaryLayer"/>, picks the smallest-area polygon whose interior contains every point in <paramref name="points"/> (2D).
        /// </summary>
        public static bool TryFindSmallestFloorRingContainingAllPoints(
            Database db,
            Transaction tr,
            IList<Point2d> points,
            out List<Point2d> floorRing)
        {
            floorRing = null;
            if (db == null || tr == null || points == null || points.Count == 0)
                return false;

            List<Point2d> bestRing = null;
            double bestArea = double.MaxValue;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            var polylineClass = RXClass.GetClass(typeof(Polyline));

            foreach (ObjectId id in ms)
            {
                if (!id.ObjectClass.IsDerivedFrom(polylineClass)) continue;
                Polyline pl;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch { continue; }
                if (pl == null || pl.IsErased || !pl.Closed) continue;

                string lay = pl.Layer ?? string.Empty;
                if (!string.Equals(lay, SprinklerLayers.McdFloorBoundaryLayer, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(lay, SprinklerLayers.WorkLayer, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(lay, SprinklerLayers.BoundaryLayer, StringComparison.OrdinalIgnoreCase))
                    continue;

                List<Point2d> ring;
                try { ring = GetPolygon2d(pl); }
                catch { continue; }
                if (ring.Count < 3) continue;

                bool allInside = true;
                for (int i = 0; i < points.Count; i++)
                {
                    if (!IsPointInPolygonRing(ring, points[i]))
                    {
                        allInside = false;
                        break;
                    }
                }

                if (!allInside) continue;

                double area = 0;
                try { area = Math.Abs(pl.Area); } catch { area = double.PositiveInfinity; }
                if (area < bestArea)
                {
                    bestArea = area;
                    bestRing = new List<Point2d>(ring);
                }
            }

            if (bestRing == null)
                return false;
            floorRing = bestRing;
            return true;
        }

        private static string FormatCoordinates(IList<Point3d> points)
        {
            if (points == null || points.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < points.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append("(");
                sb.Append(points[i].X.ToString("F2", CultureInfo.InvariantCulture));
                sb.Append(", ");
                sb.Append(points[i].Y.ToString("F2", CultureInfo.InvariantCulture));
                sb.Append(")");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Formats WCS insertion points for the results table — order must match <see cref="IsShaftBlockInsert"/> scan
        /// after the same left-to-right, bottom-to-top sort used for "Shaft N" labels.
        /// </summary>
        public static string FormatShaftPositionsForTable(IList<Point3d> points) => FormatCoordinates(points);
    }
}

