using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;
using PolylineEntity = Autodesk.AutoCAD.DatabaseServices.Polyline;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Shared helpers for sprinkler zoning: <see cref="DedupeShaftSites"/> runs before any engine, and
    /// <see cref="AppendZoneOutlinePolylines"/> writes green dashed zone outlines from rings produced by
    /// <see cref="GridNearestShaftZoning2d"/> or <see cref="EqualAreaAxisStripZonesInPolygon2d"/>. The type name is historical (no Voronoi math here).
    /// </summary>
    public static class ShaftVoronoiZonesOnFloorPolyline
    {

        /// <summary>
        /// Zone ring vertices within this distance of the floor boundary edge are snapped onto it.
        /// Expressed in metres; converted to drawing units at runtime.
        /// Kept in line with <see cref="ShaftMidlineStripZonesInPolygon2d.SnapSearchMeters"/> so "Zone boundary + threshold"
        /// expectations match the post-outline glue-to-floor pass (multi-metre gaps were wrongly left by the old 5 cm cap).
        /// </summary>
        public const double FloorBoundarySnapThresholdMeters = ShaftMidlineStripZonesInPolygon2d.SnapSearchMeters;

        /// <summary>
        /// Collapses shaft insertion points that coincide within tolerance (duplicate inserts).
        /// </summary>
        public static List<Point2d> DedupeShaftSites(IList<Point3d> shafts, double tolerance)
        {
            var sites = new List<Point2d>();
            foreach (var p in shafts)
            {
                var q = new Point2d(p.X, p.Y);
                bool dup = false;
                foreach (var e in sites)
                {
                    if (e.GetDistanceTo(q) <= tolerance)
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup)
                    sites.Add(q);
            }
            return sites;
        }

        /// <summary>
        /// Same deduplication as <see cref="DedupeShaftSites"/>, but keeps the shaft block handle (hex) for each
        /// retained site — the first insert seen at that location wins.
        /// </summary>
        public static void DedupeShaftSitesWithHandles(
            IList<Point3d> shaftPositions,
            IList<string> shaftHandlesHex,
            double tolerance,
            out List<Point2d> sites,
            out List<string> shaftHandlesPerSite)
        {
            sites = new List<Point2d>();
            shaftHandlesPerSite = new List<string>();
            if (shaftPositions == null)
                return;

            int n = shaftPositions.Count;
            for (int i = 0; i < n; i++)
            {
                var p = shaftPositions[i];
                string hx = (shaftHandlesHex != null && i < shaftHandlesHex.Count) ? shaftHandlesHex[i] : null;
                var q = new Point2d(p.X, p.Y);
                bool dup = false;
                for (int j = 0; j < sites.Count; j++)
                {
                    if (sites[j].GetDistanceTo(q) <= tolerance)
                    {
                        dup = true;
                        break;
                    }
                }

                if (!dup)
                {
                    sites.Add(q);
                    shaftHandlesPerSite.Add(hx);
                }
            }
        }

        /// <summary>
        /// Appends closed dashed polylines for each zone ring to model space, plus MText labels (Zone 1 …) on <see cref="SprinklerLayers.ZoneLabelLayer"/>.
        /// By default outlines go on <see cref="SprinklerLayers.ZoneLayer"/>; set <paramref name="zoneOutlinesOnFloorBoundaryLayer"/> so outlines use <see cref="SprinklerLayers.WorkLayer"/> (floor boundary).
        /// </summary>
        /// <param name="boundary">Floor boundary used for elevation/plane and visible line width.</param>
        /// <param name="zoneTable">Names and areas (one per ring); if null, names default to Zone 1…N and areas are computed from rings.</param>
        /// <param name="zoneOutlinesOnFloorBoundaryLayer">When true, zone polylines are placed on the floor boundary work layer (ByLayer color).</param>
        /// <param name="createdZonePolylineHandles">When non-null, each new zone outline polyline handle (hex) is appended in zone order.</param>
        /// <param name="useMcdZoneBoundaryLayer">When true, zone outline polylines use <see cref="SprinklerLayers.McdZoneBoundaryLayer"/> (create if missing).</param>
        /// <param name="parentFloorBoundaryHandleHex">When set, stored on each zone outline as <see cref="SprinklerXData.ApplyParentFloorBoundaryTag"/>; when null and <paramref name="boundary"/> has a database id, uses <paramref name="boundary"/>'s handle.</param>
        public static void AppendZoneOutlinePolylines(
            Document doc,
            List<List<Point2d>> zoneRings,
            Polyline boundary,
            IList<ZoneTableEntry> zoneTable = null,
            bool zoneOutlinesOnFloorBoundaryLayer = false,
            List<string> createdZonePolylineHandles = null,
            bool useMcdZoneBoundaryLayer = false,
            string parentFloorBoundaryHandleHex = null)
        {
            if (zoneRings == null || zoneRings.Count == 0)
                return;

            var db = doc.Database;
            var ed = doc.Editor;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                ObjectId layerId;
                if (zoneOutlinesOnFloorBoundaryLayer)
                    layerId = SprinklerLayers.EnsureWorkLayer(tr, db);
                else if (useMcdZoneBoundaryLayer)
                    layerId = SprinklerLayers.EnsureMcdZoneBoundaryLayer(tr, db);
                else
                    layerId = SprinklerLayers.EnsureZoneLayer(tr, db);
                ObjectId labelLayerId = SprinklerLayers.EnsureZoneLabelLayer(tr, db);
                ObjectId ltId = SprinklerLayers.EnsureLinetypePresent(tr, db, "DASHED", ed);
                if (ltId.IsNull)
                    ltId = SprinklerLayers.EnsureLinetypePresent(tr, db, "HIDDEN", ed);
                if (ltId.IsNull)
                    ltId = SprinklerLayers.EnsureLinetypePresent(tr, db, "ACAD_ISO02W100", ed);
                if (ltId.IsNull)
                    ltId = SprinklerLayers.EnsureLinetypePresent(tr, db, "Continuous", ed);

                double width = SprinklerLayers.ZoneOutlinePolylineWidth(db);
                double textHeight = 2.5;
                if (boundary != null)
                {
                    try
                    {
                        var ext = boundary.GeometricExtents;
                        width = SprinklerLayers.ZoneOutlinePolylineWidth(db, ext);
                        textHeight = SprinklerLayers.ZoneLabelTextHeight(db, ext);
                    }
                    catch
                    {
                        /* ignore */
                    }
                }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Pre-compute floor ring and snap threshold once for all zone rings.
                var floorRing = boundary != null
                    ? PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(boundary)
                    : new List<Point2d>();
                double snapThresholdDu = 0;
                if (floorRing.Count >= 2)
                {
                    if (DrawingUnitsHelper.TryMetersToDrawingLength(
                            db.Insunits, FloorBoundarySnapThresholdMeters, out double snapDu) && snapDu > 0)
                    {
                        snapThresholdDu = snapDu;
                    }
                    else if (boundary != null)
                    {
                        // INSUNITS unset: still snap using scale inferred from floor extent (same idea as grid spacing).
                        try
                        {
                            var ext = boundary.GeometricExtents;
                            double extentDu = Math.Max(
                                Math.Abs(ext.MaxPoint.X - ext.MinPoint.X),
                                Math.Abs(ext.MaxPoint.Y - ext.MinPoint.Y));
                            if (DrawingUnitsHelper.TryAutoGetDrawingScale(
                                    db.Insunits, spacingMeters: 1.0, extentHint: extentDu, out double duPerMeter)
                                && duPerMeter > 0)
                            {
                                snapThresholdDu = FloorBoundarySnapThresholdMeters * duPerMeter;
                            }
                        }
                        catch
                        {
                            /* ignore */
                        }
                    }
                }

                int zonesDrawn = 0;
                for (int zi = 0; zi < zoneRings.Count; zi++)
                {
                    var ring = zoneRings[zi];
                    if (ring == null || ring.Count < 3)
                    {
                        ed.WriteMessage(
                            "\n[autocad-final] Zone " + (zi + 1).ToString(CultureInfo.InvariantCulture) +
                            " skipped: invalid ring before snap (" + (ring?.Count ?? 0).ToString(CultureInfo.InvariantCulture) + " vertices).\n");
                        continue;
                    }

                    var ringBeforeSnap = new List<Point2d>(ring);

                    // Snap vertices that are within the threshold of the floor boundary onto it.
                    if (snapThresholdDu > 0)
                    {
                        ring = SnapRingToFloorBoundary(ring, floorRing, snapThresholdDu, out var snapSegEnd);
                        ring = RefineZoneRingFollowFloorBoundary(ring, snapSegEnd, floorRing, snapThresholdDu);
                    }

                    if (ring == null || ring.Count < 3)
                    {
                        ed.WriteMessage(
                            "\n[autocad-final] Zone " + (zi + 1).ToString(CultureInfo.InvariantCulture) +
                            " snap collapsed the ring; using pre-snap geometry.\n");
                        ring = ringBeforeSnap;
                    }

                    var pl = new PolylineEntity();
                    pl.SetDatabaseDefaults(db);
                    pl.LayerId = layerId;
                    pl.Color = zoneOutlinesOnFloorBoundaryLayer || useMcdZoneBoundaryLayer
                        ? Color.FromColorIndex(ColorMethod.ByLayer, 256)
                        : Color.FromColorIndex(ColorMethod.ByAci, SprinklerLayers.ZoneLayerAciGreen);
                    pl.ConstantWidth = width;
                    if (!ltId.IsNull)
                        pl.LinetypeId = ltId;

                    if (boundary != null)
                    {
                        pl.Elevation = boundary.Elevation;
                        pl.Normal = boundary.Normal;
                    }

                    for (int k = 0; k < ring.Count; k++)
                        pl.AddVertexAt(k, ring[k], 0, 0, 0);
                    pl.Closed = true;

                    ms.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    try
                    {
                        SprinklerXData.EnsureRegApp(tr, db);
                        SprinklerXData.ApplyZoneBoundaryTag(pl, pl.Handle.ToString());
                    }
                    catch
                    {
                        /* ignore */
                    }

                    try
                    {
                        string parentHex = parentFloorBoundaryHandleHex;
                        if (string.IsNullOrEmpty(parentHex) && boundary != null && !boundary.ObjectId.IsNull && !boundary.IsErased)
                            parentHex = boundary.Handle.ToString();
                        if (!string.IsNullOrEmpty(parentHex))
                            SprinklerXData.ApplyParentFloorBoundaryTag(pl, parentHex);
                    }
                    catch
                    {
                        /* ignore */
                    }

                    createdZonePolylineHandles?.Add(pl.Handle.ToString());

                    string name = zoneTable != null && zi < zoneTable.Count && !string.IsNullOrWhiteSpace(zoneTable[zi].Name)
                        ? zoneTable[zi].Name
                        : "Zone " + (zi + 1).ToString(CultureInfo.InvariantCulture);
                    double aDu = zoneTable != null && zi < zoneTable.Count
                        ? zoneTable[zi].AreaDrawingUnits
                        : PolygonVerticalHalfPlaneClip2d.AbsArea(ring);
                    double? aM2 = zoneTable != null && zi < zoneTable.Count
                        ? zoneTable[zi].AreaM2
                        : null;

                    string line2 = aM2.HasValue
                        ? aM2.Value.ToString("F2", CultureInfo.InvariantCulture) + " m²"
                        : aDu.ToString("F2", CultureInfo.InvariantCulture) + " sq. units";

                    Point2d c = PickInteriorLabelPoint(ring);
                    var ins = new Point3d(c.X, c.Y, boundary != null ? boundary.Elevation : 0);
                    var mt = new MText();
                    mt.SetDatabaseDefaults(db);
                    mt.LayerId = labelLayerId;
                    mt.Color = Color.FromColorIndex(ColorMethod.ByAci, SprinklerLayers.ZoneLabelAciWhite);
                    mt.TextHeight = textHeight;
                    mt.Contents = name + "\\P" + line2;
                    mt.Attachment = AttachmentPoint.MiddleCenter;
                    mt.Location = ins;
                    if (boundary != null)
                        mt.Normal = boundary.Normal;
                    mt.Width = 0;

                    ms.AppendEntity(mt);
                    tr.AddNewlyCreatedDBObject(mt, true);
                    zonesDrawn++;
                }

                if (zonesDrawn != zoneRings.Count)
                {
                    ed.WriteMessage(
                        "\n[autocad-final] Zone outline count mismatch: drew " +
                        zonesDrawn.ToString(CultureInfo.InvariantCulture) + " of " +
                        zoneRings.Count.ToString(CultureInfo.InvariantCulture) +
                        " zone rings. Check command line for per-zone errors.\n");
                }

                tr.Commit();
            }

            try
            {
                ed.Regen();
            }
            catch
            {
                /* ignore */
            }
        }

        private static Point2d PickInteriorLabelPoint(List<Point2d> ring)
        {
            if (ring == null || ring.Count < 3)
                return new Point2d(0, 0);

            GetExtents(ring, out double minX, out double minY, out double maxX, out double maxY);
            double dx = maxX - minX, dy = maxY - minY;
            double extent = Math.Max(Math.Abs(dx), Math.Abs(dy));
            double eps = 1e-9 * Math.Max(extent, 1.0);

            // 1) True polygon centroid (area-weighted). Can still fall outside for concave rings.
            if (TryPolygonAreaCentroid(ring, eps, out var areaCentroid) && PointInPolygon(ring, areaCentroid))
                return areaCentroid;

            // 2) BBox center.
            var bboxCenter = new Point2d(0.5 * (minX + maxX), 0.5 * (minY + maxY));
            if (PointInPolygon(ring, bboxCenter))
                return bboxCenter;

            // 3) Polylabel-lite search: maximize distance to edges among interior grid samples.
            // Coarse-to-fine refinement around best point.
            Point2d best = ring[0];
            double bestScore = -1;

            double step = Math.Max(extent / 10.0, 1.0);
            int refinements = 3;
            for (int pass = 0; pass < refinements; pass++)
            {
                double startX = pass == 0 ? minX : Math.Max(minX, best.X - 2.0 * step);
                double endX   = pass == 0 ? maxX : Math.Min(maxX, best.X + 2.0 * step);
                double startY = pass == 0 ? minY : Math.Max(minY, best.Y - 2.0 * step);
                double endY   = pass == 0 ? maxY : Math.Min(maxY, best.Y + 2.0 * step);

                for (double x = startX; x <= endX; x += step)
                {
                    for (double y = startY; y <= endY; y += step)
                    {
                        var p = new Point2d(x, y);
                        if (!PointInPolygon(ring, p)) continue;

                        double d = MinDistanceToEdges(ring, p);
                        if (d > bestScore)
                        {
                            bestScore = d;
                            best = p;
                        }
                    }
                }

                step = Math.Max(step * 0.5, 0.25);
            }

            // If we found any interior sample, use it; otherwise fall back to first vertex.
            return bestScore > 0 ? best : ring[0];
        }

        private static void GetExtents(List<Point2d> ring, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = double.MaxValue; minY = double.MaxValue;
            maxX = double.MinValue; maxY = double.MinValue;
            for (int i = 0; i < ring.Count; i++)
            {
                var p = ring[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            if (minX == double.MaxValue) { minX = minY = maxX = maxY = 0; }
        }

        // Ray casting point-in-polygon (non-self-intersecting ring).
        private static bool PointInPolygon(List<Point2d> poly, Point2d p)
        {
            bool c = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = poly[i];
                var b = poly[j];
                bool cond = ((a.Y > p.Y) != (b.Y > p.Y));
                if (!cond) continue;
                double xInt = (b.X - a.X) * (p.Y - a.Y) / ((b.Y - a.Y) == 0 ? 1e-12 : (b.Y - a.Y)) + a.X;
                if (p.X < xInt)
                    c = !c;
            }
            return c;
        }

        private static bool TryPolygonAreaCentroid(List<Point2d> ring, double eps, out Point2d centroid)
        {
            centroid = new Point2d(0, 0);
            double a = 0;
            double cx = 0, cy = 0;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double x0 = ring[j].X, y0 = ring[j].Y;
                double x1 = ring[i].X, y1 = ring[i].Y;
                double cross = x0 * y1 - x1 * y0;
                a += cross;
                cx += (x0 + x1) * cross;
                cy += (y0 + y1) * cross;
            }
            if (Math.Abs(a) <= eps) return false;
            double inv = 1.0 / (3.0 * a);
            centroid = new Point2d(cx * inv, cy * inv);
            return true;
        }

        private static double MinDistanceToEdges(List<Point2d> ring, Point2d p)
        {
            double best = double.MaxValue;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double d = DistancePointToSegment(p, ring[j], ring[i]);
                if (d < best) best = d;
            }
            return best == double.MaxValue ? 0 : best;
        }

        private static double DistancePointToSegment(Point2d p, Point2d a, Point2d b)
        {
            double vx = b.X - a.X, vy = b.Y - a.Y;
            double wx = p.X - a.X, wy = p.Y - a.Y;
            double c1 = vx * wx + vy * wy;
            if (c1 <= 0) return Math.Sqrt(wx * wx + wy * wy);
            double c2 = vx * vx + vy * vy;
            if (c2 <= 0) return Math.Sqrt(wx * wx + wy * wy);
            double t = c1 / c2;
            if (t >= 1)
            {
                double dx = p.X - b.X, dy = p.Y - b.Y;
                return Math.Sqrt(dx * dx + dy * dy);
            }
            double projX = a.X + t * vx;
            double projY = a.Y + t * vy;
            double px = p.X - projX, py = p.Y - projY;
            return Math.Sqrt(px * px + py * py);
        }

        private static Point2d NearestPointOnSegment(Point2d p, Point2d a, Point2d b)
        {
            double vx = b.X - a.X, vy = b.Y - a.Y;
            double c2 = vx * vx + vy * vy;
            if (c2 <= 0) return a;
            double t = ((p.X - a.X) * vx + (p.Y - a.Y) * vy) / c2;
            t = Math.Max(0.0, Math.Min(1.0, t));
            return new Point2d(a.X + t * vx, a.Y + t * vy);
        }

        /// <summary>
        /// Extracts the 2-D vertex ring from a closed AutoCAD polyline.
        /// </summary>
        private static List<Point2d> GetPolylineRing2d(Polyline poly)
        {
            var ring = new List<Point2d>();
            if (poly == null) return ring;
            int n = poly.NumberOfVertices;
            for (int i = 0; i < n; i++)
                ring.Add(poly.GetPoint2dAt(i));
            return ring;
        }

        /// <summary>
        /// Returns a copy of <paramref name="ring"/> where vertices that lie within
        /// <paramref name="threshold"/> of the floor boundary are projected onto it.
        /// When several floor edges are within tolerance (common near corners), picks the edge that best
        /// aligns with the local zone wall direction (chord across the vertex) so one edge is not
        /// mistaken for a shorter perpendicular distance to the wrong wall — which caused kinked/slanted zone lines.
        /// </summary>
        private static List<Point2d> SnapRingToFloorBoundary(
            List<Point2d> ring,
            List<Point2d> floorRing,
            double threshold,
            out List<int> floorSegEnd)
        {
            int fn = floorRing.Count;
            var result = new List<Point2d>(ring.Count);
            floorSegEnd = new List<int>(ring.Count);

            for (int vi = 0; vi < ring.Count; vi++)
            {
                var pt = ring[vi];
                var prev = ring[(vi - 1 + ring.Count) % ring.Count];
                var next = ring[(vi + 1) % ring.Count];
                double cdx = next.X - prev.X, cdy = next.Y - prev.Y;
                double chordLen = Math.Sqrt(cdx * cdx + cdy * cdy);

                Point2d outPt = pt;
                int winSeg = -1;

                const double chordEps = 1e-9;
                if (chordLen > chordEps)
                {
                    bool have = false;
                    double bestAlign = -2.0;
                    double bestD = double.MaxValue;
                    Point2d candSnap = pt;
                    int candSeg = -1;
                    for (int i = 0, j = fn - 1; i < fn; j = i++)
                    {
                        var a = floorRing[j];
                        var b = floorRing[i];
                        double d = DistancePointToSegment(pt, a, b);
                        if (d > threshold)
                            continue;

                        double sdx = b.X - a.X, sdy = b.Y - a.Y;
                        double segLen = Math.Sqrt(sdx * sdx + sdy * sdy);
                        double align = 0;
                        if (segLen > chordEps)
                            align = Math.Abs((cdx * sdx + cdy * sdy) / (chordLen * segLen));

                        Point2d snap = NearestPointOnSegment(pt, a, b);
                        if (!have || align > bestAlign + 1e-9 ||
                            (Math.Abs(align - bestAlign) <= 1e-9 && d < bestD))
                        {
                            have = true;
                            bestAlign = align;
                            bestD = d;
                            candSnap = snap;
                            candSeg = i;
                        }
                    }
                    if (have)
                    {
                        outPt = candSnap;
                        winSeg = candSeg;
                    }
                }
                else
                {
                    double bestD = double.MaxValue;
                    Point2d candSnap = pt;
                    int candSeg = -1;
                    for (int i = 0, j = fn - 1; i < fn; j = i++)
                    {
                        double d = DistancePointToSegment(pt, floorRing[j], floorRing[i]);
                        if (d > threshold)
                            continue;
                        if (d < bestD)
                        {
                            bestD = d;
                            candSnap = NearestPointOnSegment(pt, floorRing[j], floorRing[i]);
                            candSeg = i;
                        }
                    }
                    if (bestD < double.MaxValue)
                    {
                        outPt = candSnap;
                        winSeg = candSeg;
                    }
                }

                result.Add(outPt);
                floorSegEnd.Add(winSeg);
            }

            return result;
        }

        /// <summary>
        /// Uses segment indices from <see cref="SnapRingToFloorBoundary"/>, aligns mid-vertices with neighbors on the
        /// same wall, inserts floor corners between different segments, then re-projects every vertex onto its wall.
        /// </summary>
        private static List<Point2d> RefineZoneRingFollowFloorBoundary(
            List<Point2d> ring,
            List<int> snapSegEnd,
            List<Point2d> floorRing,
            double threshold)
        {
            if (ring == null || ring.Count < 3 || floorRing == null || floorRing.Count < 2)
                return ring;

            int nIn = ring.Count;
            int fn = floorRing.Count;
            double voteTol = threshold * 2.5;
            double dedupeEps = Math.Max(threshold * 1e-4, 1e-7);

            var segIdx = new List<int>(ring.Count);
            for (int i = 0; i < ring.Count; i++)
            {
                int s = (snapSegEnd != null && i < snapSegEnd.Count) ? snapSegEnd[i] : -1;
                if (s < 0)
                    s = PickFloorSegmentEndNearest(ring[i], floorRing, voteTol);
                segIdx.Add(s);
            }

            for (int pass = 0; pass < 3; pass++)
            {
                for (int i = 0; i < ring.Count; i++)
                {
                    int sp = segIdx[(i - 1 + ring.Count) % ring.Count];
                    int sn = segIdx[(i + 1) % ring.Count];
                    if (sp < 0 || sn < 0 || sp != sn)
                        continue;

                    int s = sp;
                    int j = (s - 1 + fn) % fn;
                    double d = DistancePointToSegment(ring[i], floorRing[j], floorRing[s]);
                    if (d > voteTol)
                        continue;

                    ring[i] = NearestPointOnSegment(ring[i], floorRing[j], floorRing[s]);
                    segIdx[i] = s;
                }
            }

            var merged = new List<Point2d>(ring.Count + floorRing.Count);
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                int sa = segIdx[i];
                int sb = segIdx[(i + 1) % n];

                merged.Add(a);

                if (sa < 0 || sb < 0 || sa == sb)
                    continue;

                var corners = FloorCornersAlongShorterFloorBoundary(floorRing, sa, sb);
                foreach (var c in corners)
                {
                    if (c.GetDistanceTo(a) <= dedupeEps || c.GetDistanceTo(b) <= dedupeEps)
                        continue;
                    merged.Add(c);
                }
            }

            DedupeConsecutivePoints(merged, dedupeEps);
            if (merged.Count < 3)
                return ring;

            // Lock every vertex onto its assigned floor segment (exact boundary level).
            var mergedSeg = new List<int>(merged.Count);
            for (int i = 0; i < merged.Count; i++)
            {
                int s = PickFloorSegmentEndNearest(merged[i], floorRing, voteTol);
                if (s >= 0)
                {
                    int j = (s - 1 + fn) % fn;
                    merged[i] = NearestPointOnSegment(merged[i], floorRing[j], floorRing[s]);
                }
                mergedSeg.Add(s);
            }

            // Second corner pass after re-projection (segment assignment may have changed).
            var finalRing = new List<Point2d>(merged.Count + floorRing.Count);
            int mn = merged.Count;
            for (int i = 0; i < mn; i++)
            {
                var a = merged[i];
                var b = merged[(i + 1) % mn];
                int sa = mergedSeg[i];
                int sb = mergedSeg[(i + 1) % mn];

                finalRing.Add(a);

                if (sa < 0 || sb < 0 || sa == sb)
                    continue;

                foreach (var c in FloorCornersAlongShorterFloorBoundary(floorRing, sa, sb))
                {
                    if (c.GetDistanceTo(a) <= dedupeEps || c.GetDistanceTo(b) <= dedupeEps)
                        continue;
                    finalRing.Add(c);
                }
            }

            DedupeConsecutivePoints(finalRing, dedupeEps);
            if (finalRing.Count < 3)
                return ring;

            return finalRing;
        }

        /// <returns>Segment end index of the closest floor edge within tolerance, or -1.</returns>
        private static int PickFloorSegmentEndNearest(Point2d pt, List<Point2d> floorRing, double tol)
        {
            int fn = floorRing.Count;
            int bestI = -1;
            double bestD = double.MaxValue;
            for (int i = 0, j = fn - 1; i < fn; j = i++)
            {
                double d = DistancePointToSegment(pt, floorRing[j], floorRing[i]);
                if (d > tol || !(d < bestD))
                    continue;
                bestD = d;
                bestI = i;
            }
            return bestI;
        }

        /// <summary>Intermediate floor vertices when walking the shorter way from segment sa to sb.</summary>
        private static List<Point2d> FloorCornersAlongShorterFloorBoundary(List<Point2d> fr, int sa, int sb)
        {
            int fn = fr.Count;
            var empty = new List<Point2d>();
            if (sa < 0 || sb < 0 || sa == sb)
                return empty;

            int df = ForwardSegHops(sa, sb, fn);
            int db = BackwardSegHops(sa, sb, fn);
            if (df >= fn + 2 || db >= fn + 2)
                return empty;
            bool useFwd = df <= db;

            var r = new List<Point2d>(Math.Min(fn, useFwd ? df : db));
            if (useFwd)
            {
                int c = sa;
                while (c != sb)
                {
                    r.Add(fr[c]);
                    c = (c + 1) % fn;
                    if (r.Count > fn + 2)
                        break;
                }
            }
            else
            {
                int c = sa;
                while (c != sb)
                {
                    c = (c - 1 + fn) % fn;
                    r.Add(fr[c]);
                    if (r.Count > fn + 2)
                        break;
                }
            }
            return r;
        }

        private static int ForwardSegHops(int sa, int sb, int fn)
        {
            int d = 0;
            int c = sa;
            while (c != sb)
            {
                c = (c + 1) % fn;
                d++;
                if (d > fn + 1)
                    return int.MaxValue;
            }
            return d;
        }

        private static int BackwardSegHops(int sa, int sb, int fn)
        {
            int d = 0;
            int c = sa;
            while (c != sb)
            {
                c = (c - 1 + fn) % fn;
                d++;
                if (d > fn + 1)
                    return int.MaxValue;
            }
            return d;
        }

        private static void DedupeConsecutivePoints(List<Point2d> pts, double eps)
        {
            if (pts == null || pts.Count < 2)
                return;
            for (int k = 1; k < pts.Count;)
            {
                if (pts[k].GetDistanceTo(pts[k - 1]) <= eps)
                    pts.RemoveAt(k);
                else
                    k++;
            }
            if (pts.Count >= 2 &&
                pts[0].GetDistanceTo(pts[pts.Count - 1]) <= eps)
                pts.RemoveAt(pts.Count - 1);
        }
    }
}
