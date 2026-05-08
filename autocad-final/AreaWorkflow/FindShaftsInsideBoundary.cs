using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    public static class FindShaftsInsideBoundary
    {        public readonly struct ShaftBlockInfo
        {
            public readonly Point3d Position;
            public readonly bool HasExtents;
            public readonly Extents2d Extents2d;

            public ShaftBlockInfo(Point3d position, bool hasExtents, Extents2d extents2d)
            {
                Position = position;
                HasExtents = hasExtents;
                Extents2d = extents2d;
            }
        }

        /// <summary>
        /// Block inserts whose insertion point lies inside the closed boundary (plan), with optional extents for shaft overlap avoidance.
        /// Also merges any virtual shaft hints registered via <see cref="autocad_final.Agent.ShaftHintStore"/>.
        /// </summary>
        public static List<ShaftBlockInfo> GetShaftBlocksInsideBoundary(Database db, Polyline boundary)
        {
            var blocks = new List<ShaftBlockInfo>();
            var polygon = GetPolygon2d(boundary);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (!(ent is BlockReference br)) continue;

                    string blockName = GetBlockName(br, tr);
                    string brLayer   = br.Layer ?? string.Empty;
                    bool blockMatch  = blockName.IndexOf("shaft", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    bool layerMatch  = brLayer.IndexOf("shaft", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || string.Equals(brLayer, SprinklerLayers.McdShaftsLayer, System.StringComparison.OrdinalIgnoreCase);
                    if (!blockMatch && !layerMatch)
                        continue;

                    var p2 = new Point2d(br.Position.X, br.Position.Y);
                    if (!IsPointInPolygonRing(polygon, p2))
                        continue;

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

                    blocks.Add(new ShaftBlockInfo(br.Position, hasExt, e2));
                }
                tr.Commit();
            }

            // Merge session shaft hints (virtual shafts registered when drawing has no named shaft blocks).
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

                                        shaft = new ShaftBlockInfo(br.Position, hasExt, e2);
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

                    string blockName = GetBlockName(br, tr);
                    string brLayer = br.Layer ?? string.Empty;
                    bool blockMatch = blockName.IndexOf("shaft", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool layerMatch = brLayer.IndexOf("shaft", StringComparison.OrdinalIgnoreCase) >= 0
                        || string.Equals(brLayer, SprinklerLayers.McdShaftsLayer, StringComparison.OrdinalIgnoreCase);
                    if (!blockMatch && !layerMatch)
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
    }
}

