using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;

namespace autocad_final.Workflows.Zoning
{
    /// <summary>
    /// Fixes non-orthogonal zone separator lines by snapping them to the nearest axis (X or Y)
    /// about their midpoint, preserving segment length.
    /// Operates on Line entities on <see cref="SprinklerLayers.McdZoneBoundaryLayer"/> (or legacy
    /// <see cref="SprinklerLayers.ZoneGlobalBoundaryLayer"/>) whose
    /// midpoints are inside the selected boundary, then prunes redundant axis cuts so that
    /// each region contains at most one shaft (zones track shafts, not raw separator count).
    /// </summary>
    public static class ManhattanFixedSeparatorFixWorkflow
    {
        private const double NeighborTolMult = 4.0;

        public static bool TryRun(Document doc, Polyline boundary, ObjectId boundaryEntityId, out string message)
            => TryRunCore(doc, boundary, boundaryEntityId, useDynamicTrim: false, out message);

        /// <summary>
        /// Manhattan fixed v2: same behaviour as <see cref="TryRun"/>, but trims separators dynamically
        /// against the floor boundary + final zone regions so overshooting stubs are removed.
        /// </summary>
        public static bool TryRunV2(Document doc, Polyline boundary, ObjectId boundaryEntityId, out string message)
            => TryRunCore(doc, boundary, boundaryEntityId, useDynamicTrim: true, out message);

        private static bool TryRunCore(Document doc, Polyline boundary, ObjectId boundaryEntityId, bool useDynamicTrim, out string message)
        {
            message = null;
            if (doc == null) { message = "No document."; return false; }
            if (boundary == null) { message = "No boundary selected."; return false; }

            var db = doc.Database;
            var ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(boundary);
            if (ring == null || ring.Count < 3) { message = "Selected boundary is invalid."; return false; }

            int considered = 0;
            int snapped = 0;
            int zonesBuilt = 0;
            int emptyZones = 0;
            int candidatesInitial = 0;
            int keptCuts = 0;
            int shaftCount = 0;

            // Snap tolerance: leave near-axis lines alone; snap everything else.
            const double degToRad = Math.PI / 180.0;
            double angleTol = 2.0 * degToRad; // 2°
            double axisTol = Math.Max(BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db), 1e-6) * 10.0;
            if (!(axisTol > 0)) axisTol = 1e-5;
            double eps = Math.Max(BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db), 1e-6);
            if (!(eps > 0)) eps = 1e-6;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                SprinklerXData.EnsureRegApp(tr, db);

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                ObjectId zoneLayerId = SprinklerLayers.EnsureMcdZoneBoundaryLayer(tr, db);

                // Remove prior generated zone polygons (closed polylines tagged with their own handle).
                foreach (ObjectId id in ms)
                {
                    Entity e;
                    try { e = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { continue; }
                    if (e == null || e.IsErased) continue;
                    if (!(e is Polyline pl) || !pl.Closed) continue;
                    if (!SprinklerLayers.IsZoneGlobalBoundaryOrMcdZoneOutlineLayerName(pl.Layer))
                        continue;
                    if (!SprinklerXData.TryGetZoneBoundaryHandle(pl, out string h)) continue;
                    if (!string.Equals(h, pl.Handle.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
                    try { pl.UpgradeOpen(); pl.Erase(); } catch { /* ignore */ }
                }

                // 1) Collect axis candidates from Line entities (non-mutating); then erase them.
                var splitX = new List<double>();
                var splitY = new List<double>();
                var linesToErase = new List<ObjectId>();

                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
                        continue;
                    if (!SprinklerLayers.IsZoneGlobalBoundaryOrMcdZoneOutlineLayerName(ent.Layer))
                        continue;
                    if (!(ent is Line ln))
                        continue;

                    var sp = ln.StartPoint;
                    var ep = ln.EndPoint;
                    var mid = new Point2d((sp.X + ep.X) * 0.5, (sp.Y + ep.Y) * 0.5);

                    if (!PolygonUtils.PointInPolygon(ring, mid))
                        continue;
                    // Only treat true interior separators as fix candidates.
                    // Keep the original floor boundary (outer edges) intact.
                    if (PolygonUtils.IsPointOnRingEdge(ring, mid, Math.Max(eps, 1e-6) * 2.0))
                        continue;

                    linesToErase.Add(id);

                    double dx = ep.X - sp.X;
                    double dy = ep.Y - sp.Y;
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    if (len <= 1e-9)
                        continue;

                    considered++;

                    double ang = Math.Atan2(dy, dx);
                    double a = Math.Abs(ang);
                    while (a > Math.PI) a -= Math.PI;
                    if (a > Math.PI * 0.5) a = Math.PI - a; // fold into [0,pi/2]

                    double toHoriz = a;
                    double toVert = Math.Abs((Math.PI * 0.5) - a);
                    if (toHoriz <= angleTol || toVert <= angleTol)
                    {
                        if (Math.Abs(dx) <= Math.Abs(dy))
                            AddAxis(splitX, mid.X, axisTol);
                        else
                            AddAxis(splitY, mid.Y, axisTol);
                        continue;
                    }

                    bool snapVertical = toVert < toHoriz;
                    double sx = (sp.X + ep.X) * 0.5;
                    double sy = (sp.Y + ep.Y) * 0.5;

                    if (snapVertical)
                    {
                        if (TryClipAxisLineToBoundary(ring, vertical: true, axisCoord: sx, out var _, out var __))
                        {
                            snapped++;
                            AddAxis(splitX, sx, axisTol);
                        }
                    }
                    else
                    {
                        if (TryClipAxisLineToBoundary(ring, vertical: false, axisCoord: sy, out var _, out var __))
                        {
                            snapped++;
                            AddAxis(splitY, sy, axisTol);
                        }
                    }
                }

                candidatesInitial = splitX.Count + splitY.Count;

                // Erase all separator Line entities (midpoint inside this boundary).
                for (int li = 0; li < linesToErase.Count; li++)
                {
                    try
                    {
                        var id = linesToErase[li];
                        if (id.IsErased) continue;
                        var o = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (o is Line l)
                        {
                            l.UpgradeOpen();
                            l.Erase();
                        }
                    }
                    catch { /* ignore */ }
                }

                // Shaft positions (Manhattan / assignment context).
                var shaftBlockList = FindShaftsInsideBoundary.GetShaftBlocksInsideBoundary(db, boundary);
                shaftCount = shaftBlockList.Count;
                var shaftPts = new List<Point2d>(shaftCount);
                for (int si = 0; si < shaftCount; si++)
                {
                    var p = shaftBlockList[si].Position;
                    shaftPts.Add(new Point2d(p.X, p.Y));
                }

                // 2) Greedy reverse-prune: remove cuts while no region has more than one shaft.
                GreedyPruneAxisCuts(splitX, splitY, ring, shaftPts, axisTol, eps);

                keptCuts = splitX.Count + splitY.Count;

                // Build final regions once; used for separator trimming and later for polyline emit.
                var polys = SliceWithCuts(ring, splitX, splitY, eps);

                // 3) Draw fresh axis Line entities for kept cuts (trimmed to actual zone edges).
                splitX.Sort();
                splitY.Sort();
                for (int i = 0; i < splitX.Count; i++)
                {
                    var edgeRanges = useDynamicTrim
                        ? CollectAxisSeparatorRangesDynamic(polys, axis: splitX[i], vertical: true, tol: Math.Max(axisTol, eps) * 2.0, eps: eps)
                        : CollectAxisEdgeRanges(polys, axis: splitX[i], vertical: true, tol: Math.Max(axisTol, eps) * 2.0);

                    for (int r = 0; r < edgeRanges.Count; r++)
                    {
                        var seg = edgeRanges[r];
                        var a = new Point2d(splitX[i], seg.Min);
                        var b = new Point2d(splitX[i], seg.Max);
                        var line = new Line(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0));
                        line.LayerId = zoneLayerId;
                        ms.AppendEntity(line);
                        tr.AddNewlyCreatedDBObject(line, true);
                        SprinklerXData.ApplyZoneBoundaryTag(line, line.Handle.ToString());
                    }
                }
                for (int i = 0; i < splitY.Count; i++)
                {
                    var edgeRanges = useDynamicTrim
                        ? CollectAxisSeparatorRangesDynamic(polys, axis: splitY[i], vertical: false, tol: Math.Max(axisTol, eps) * 2.0, eps: eps)
                        : CollectAxisEdgeRanges(polys, axis: splitY[i], vertical: false, tol: Math.Max(axisTol, eps) * 2.0);

                    for (int r = 0; r < edgeRanges.Count; r++)
                    {
                        var seg = edgeRanges[r];
                        var a = new Point2d(seg.Min, splitY[i]);
                        var b = new Point2d(seg.Max, splitY[i]);
                        var line = new Line(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0));
                        line.LayerId = zoneLayerId;
                        ms.AppendEntity(line);
                        tr.AddNewlyCreatedDBObject(line, true);
                        SprinklerXData.ApplyZoneBoundaryTag(line, line.Handle.ToString());
                    }
                }

                // 4) Build zone polygons by slicing the floor with pruned cuts.
                double minArea = Math.Max(eps * eps * 100.0, 1e-3);
                for (int i = 0; i < polys.Count; i++)
                {
                    var p = polys[i];
                    if (p == null || p.Count < 3) continue;
                    double area = PolygonVerticalHalfPlaneClip2d.AbsArea(p);
                    if (area < minArea) continue;

                    int sIn = CountShaftsInRegion(p, shaftPts);
                    if (sIn == 0) emptyZones++;

                    var pl = new Polyline(p.Count);
                    for (int k = 0; k < p.Count; k++)
                        pl.AddVertexAt(k, p[k], 0, 0, 0);
                    pl.Closed = true;
                    pl.LayerId = zoneLayerId;
                    ms.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);

                    SprinklerXData.ApplyZoneBoundaryTag(pl, pl.Handle.ToString());
                    zonesBuilt++;
                }

                tr.Commit();
            }

            message =
                (useDynamicTrim ? "Manhattan fixed 2: candidates=" : "Manhattan fixed: candidates=") +
                ", kept=" + keptCuts +
                ", shafts=" + shaftCount +
                ", zones built=" + zonesBuilt +
                (emptyZones > 0 ? (", empty regions=" + emptyZones) : string.Empty) +
                ". (Snapped " + snapped + " / " + considered + " non-orthogonal lines.)";
            return true;
        }

        private static void AddAxis(List<double> list, double v, double tol)
        {
            for (int i = 0; i < list.Count; i++)
                if (Math.Abs(list[i] - v) <= tol) return;
            list.Add(v);
        }

        private static List<List<Point2d>> SliceWithCuts(
            IList<Point2d> ring,
            List<double> splitX,
            List<double> splitY,
            double eps)
        {
            var polys = new List<List<Point2d>> { new List<Point2d>(ring) };
            var sortedX = new List<double>(splitX);
            var sortedY = new List<double>(splitY);
            sortedX.Sort();
            sortedY.Sort();
            for (int si = 0; si < sortedX.Count; si++)
                polys = SplitAll(polys, vertical: true, axis: sortedX[si], eps: eps);
            for (int si = 0; si < sortedY.Count; si++)
                polys = SplitAll(polys, vertical: false, axis: sortedY[si], eps: eps);
            return polys;
        }

        private readonly struct Range1d
        {
            public readonly double Min;
            public readonly double Max;
            public Range1d(double min, double max)
            {
                Min = min;
                Max = max;
            }
        }

        /// <summary>
        /// Collects and unions all polygon-edge segments that lie on the axis line.
        /// This trims separators to only where the final zone boundaries actually use that axis.
        /// For vertical (x=axis), returns [minY,maxY] ranges; for horizontal (y=axis), returns [minX,maxX] ranges.
        /// </summary>
        private static List<Range1d> CollectAxisEdgeRanges(
            List<List<Point2d>> regions,
            double axis,
            bool vertical,
            double tol)
        {
            var ranges = new List<Range1d>();
            if (regions == null || regions.Count == 0) return ranges;

            for (int ri = 0; ri < regions.Count; ri++)
            {
                var poly = regions[ri];
                if (poly == null || poly.Count < 2) continue;
                int n = poly.Count;
                for (int i = 0; i < n; i++)
                {
                    var p = poly[i];
                    var q = poly[(i + 1) % n];
                    if (vertical)
                    {
                        if (Math.Abs(p.X - axis) <= tol && Math.Abs(q.X - axis) <= tol)
                        {
                            double minY = Math.Min(p.Y, q.Y);
                            double maxY = Math.Max(p.Y, q.Y);
                            if (maxY > minY + tol * 0.1)
                                ranges.Add(new Range1d(minY, maxY));
                        }
                    }
                    else
                    {
                        if (Math.Abs(p.Y - axis) <= tol && Math.Abs(q.Y - axis) <= tol)
                        {
                            double minX = Math.Min(p.X, q.X);
                            double maxX = Math.Max(p.X, q.X);
                            if (maxX > minX + tol * 0.1)
                                ranges.Add(new Range1d(minX, maxX));
                        }
                    }
                }
            }

            return UnionRanges(ranges, tol);
        }

        /// <summary>
        /// Dynamic separator trimming:
        /// 1) Intersect the axis line with region polygons to build candidate inside-intervals.
        /// 2) Keep only intervals where the axis separates two different regions (zone-to-zone boundary).
        /// </summary>
        private static List<Range1d> CollectAxisSeparatorRangesDynamic(
            List<List<Point2d>> regions,
            double axis,
            bool vertical,
            double tol,
            double eps)
        {
            var candidates = new List<Range1d>();
            if (regions == null || regions.Count == 0)
                return candidates;

            double delta = Math.Max(Math.Max(tol, eps), 1e-6) * 2.0;

            for (int ri = 0; ri < regions.Count; ri++)
            {
                var poly = regions[ri];
                if (poly == null || poly.Count < 3) continue;

                // 1) Add collinear edge ranges (edges that already lie on the axis).
                AddCollinearAxisEdgeRanges(poly, axis, vertical, tol, candidates);

                // 2) Add ranges from intersection pairing.
                var hits = CollectAxisIntersections(poly, axis, vertical, tol);
                if (hits.Count < 2) continue;
                hits.Sort();
                hits = DedupSorted(hits, tol);

                for (int i = 0; i + 1 < hits.Count; i += 2)
                {
                    double t0 = hits[i];
                    double t1 = hits[i + 1];
                    if (t1 <= t0 + tol * 0.1)
                        continue;
                    candidates.Add(new Range1d(t0, t1));
                }
            }

            var merged = UnionRanges(candidates, tol);
            if (merged.Count == 0)
                return merged;

            // Filter: keep only true separators (different regions on either side).
            var kept = new List<Range1d>(merged.Count);
            for (int i = 0; i < merged.Count; i++)
            {
                var r = merged[i];
                double tm = 0.5 * (r.Min + r.Max);

                Point2d a = vertical ? new Point2d(axis - delta, tm) : new Point2d(tm, axis - delta);
                Point2d b = vertical ? new Point2d(axis + delta, tm) : new Point2d(tm, axis + delta);

                int ra = FindContainingRegionIndex(regions, a, tol);
                int rb = FindContainingRegionIndex(regions, b, tol);
                if (ra >= 0 && rb >= 0 && ra != rb)
                    kept.Add(r);
            }

            return UnionRanges(kept, tol);
        }

        private static void AddCollinearAxisEdgeRanges(
            List<Point2d> poly,
            double axis,
            bool vertical,
            double tol,
            List<Range1d> ranges)
        {
            if (poly == null || poly.Count < 2) return;
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                var p = poly[i];
                var q = poly[(i + 1) % n];
                if (vertical)
                {
                    if (Math.Abs(p.X - axis) <= tol && Math.Abs(q.X - axis) <= tol)
                    {
                        double min = Math.Min(p.Y, q.Y);
                        double max = Math.Max(p.Y, q.Y);
                        if (max > min + tol * 0.1)
                            ranges.Add(new Range1d(min, max));
                    }
                }
                else
                {
                    if (Math.Abs(p.Y - axis) <= tol && Math.Abs(q.Y - axis) <= tol)
                    {
                        double min = Math.Min(p.X, q.X);
                        double max = Math.Max(p.X, q.X);
                        if (max > min + tol * 0.1)
                            ranges.Add(new Range1d(min, max));
                    }
                }
            }
        }

        private static List<double> CollectAxisIntersections(List<Point2d> poly, double axis, bool vertical, double tol)
        {
            var hits = new List<double>(16);
            if (poly == null || poly.Count < 2) return hits;
            int n = poly.Count;

            for (int i = 0; i < n; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % n];

                if (vertical)
                {
                    // Skip collinear edges; handled separately.
                    if (Math.Abs(a.X - axis) <= tol && Math.Abs(b.X - axis) <= tol)
                        continue;

                    double ax = a.X, bx = b.X;
                    double minx = Math.Min(ax, bx), maxx = Math.Max(ax, bx);
                    if (axis < minx - tol || axis > maxx + tol)
                        continue;

                    double dx = bx - ax;
                    if (Math.Abs(dx) <= 1e-12)
                        continue;

                    double t = (axis - ax) / dx;
                    if (t < -1e-9 || t > 1 + 1e-9)
                        continue;

                    double y = a.Y + t * (b.Y - a.Y);
                    hits.Add(y);
                }
                else
                {
                    if (Math.Abs(a.Y - axis) <= tol && Math.Abs(b.Y - axis) <= tol)
                        continue;

                    double ay = a.Y, by = b.Y;
                    double miny = Math.Min(ay, by), maxy = Math.Max(ay, by);
                    if (axis < miny - tol || axis > maxy + tol)
                        continue;

                    double dy = by - ay;
                    if (Math.Abs(dy) <= 1e-12)
                        continue;

                    double t = (axis - ay) / dy;
                    if (t < -1e-9 || t > 1 + 1e-9)
                        continue;

                    double x = a.X + t * (b.X - a.X);
                    hits.Add(x);
                }
            }

            return hits;
        }

        private static List<double> DedupSorted(List<double> sorted, double tol)
        {
            if (sorted == null || sorted.Count < 2) return sorted ?? new List<double>();
            var outList = new List<double>(sorted.Count);
            double last = sorted[0];
            outList.Add(last);
            for (int i = 1; i < sorted.Count; i++)
            {
                if (Math.Abs(sorted[i] - last) > tol)
                {
                    last = sorted[i];
                    outList.Add(last);
                }
            }
            return outList;
        }

        private static int FindContainingRegionIndex(List<List<Point2d>> regions, Point2d p, double tol)
        {
            if (regions == null) return -1;
            for (int i = 0; i < regions.Count; i++)
            {
                var poly = regions[i];
                if (poly == null || poly.Count < 3) continue;
                if (PolygonUtils.PointInPolygon(poly, p))
                    return i;
                if (PolygonUtils.IsPointOnRingEdge(poly, p, Math.Max(tol, 1e-6)))
                    return i;
            }
            return -1;
        }

        private static List<Range1d> UnionRanges(List<Range1d> ranges, double tol)
        {
            if (ranges == null || ranges.Count == 0) return new List<Range1d>();
            ranges.Sort((a, b) => a.Min.CompareTo(b.Min));
            var merged = new List<Range1d>(ranges.Count);

            double curMin = ranges[0].Min;
            double curMax = ranges[0].Max;
            for (int i = 1; i < ranges.Count; i++)
            {
                var r = ranges[i];
                if (r.Min <= curMax + tol)
                {
                    if (r.Max > curMax) curMax = r.Max;
                }
                else
                {
                    merged.Add(new Range1d(curMin, curMax));
                    curMin = r.Min;
                    curMax = r.Max;
                }
            }
            merged.Add(new Range1d(curMin, curMax));
            return merged;
        }

        // (Intersection-range helper removed; we now trim separators by actual polygon edges on the axis.)

        private static int CountShaftsInRegion(List<Point2d> region, List<Point2d> shaftPts)
        {
            if (region == null || region.Count < 3 || shaftPts == null) return 0;
            int c = 0;
            for (int i = 0; i < shaftPts.Count; i++)
            {
                if (PolygonUtils.PointInPolygon(region, shaftPts[i])) c++;
            }
            return c;
        }

        private static int MaxShaftsPerRegion(List<List<Point2d>> regions, List<Point2d> shaftPts)
        {
            if (regions == null || regions.Count == 0 || shaftPts == null) return 0;
            int max = 0;
            for (int r = 0; r < regions.Count; r++)
            {
                var reg = regions[r];
                if (reg == null || reg.Count < 3) continue;
                int c = CountShaftsInRegion(reg, shaftPts);
                if (c > max) max = c;
            }
            return max;
        }

        private struct CutRemoval
        {
            public bool IsX;
            public int Index;
            public int NeighborCount;
            public double Span;
        }

        private static List<CutRemoval> BuildRemovalOrder(
            List<double> splitX,
            List<double> splitY,
            IList<Point2d> ring,
            double axisTol)
        {
            double dTol = NeighborTolMult * axisTol;
            var list = new List<CutRemoval>();

            for (int i = 0; i < splitX.Count; i++)
            {
                int neigh = 0;
                for (int j = 0; j < splitX.Count; j++)
                    if (i != j && Math.Abs(splitX[i] - splitX[j]) <= dTol) neigh++;
                double span = 0;
                if (TryClipAxisLineToBoundary(ring, true, splitX[i], out var a, out var b))
                    span = a.GetDistanceTo(b);
                list.Add(new CutRemoval { IsX = true, Index = i, NeighborCount = neigh, Span = span });
            }

            for (int i = 0; i < splitY.Count; i++)
            {
                int neigh = 0;
                for (int j = 0; j < splitY.Count; j++)
                    if (i != j && Math.Abs(splitY[i] - splitY[j]) <= dTol) neigh++;
                double span = 0;
                if (TryClipAxisLineToBoundary(ring, false, splitY[i], out var a, out var b))
                    span = a.GetDistanceTo(b);
                list.Add(new CutRemoval { IsX = false, Index = i, NeighborCount = neigh, Span = span });
            }

            list.Sort((a, b) =>
            {
                int c = b.NeighborCount.CompareTo(a.NeighborCount);
                if (c != 0) return c;
                return a.Span.CompareTo(b.Span);
            });

            return list;
        }

        private static void GreedyPruneAxisCuts(
            List<double> splitX,
            List<double> splitY,
            List<Point2d> ring,
            List<Point2d> shaftPts,
            double axisTol,
            double eps)
        {
            if (splitX.Count + splitY.Count == 0) return;

            bool changed = true;
            while (changed)
            {
                changed = false;
                var order = BuildRemovalOrder(splitX, splitY, ring, axisTol);
                for (int oi = 0; oi < order.Count; oi++)
                {
                    var cr = order[oi];
                    if (cr.IsX)
                    {
                        if (cr.Index < 0 || cr.Index >= splitX.Count) continue;
                    }
                    else
                    {
                        if (cr.Index < 0 || cr.Index >= splitY.Count) continue;
                    }

                    var tx = new List<double>(splitX);
                    var ty = new List<double>(splitY);
                    if (cr.IsX) tx.RemoveAt(cr.Index);
                    else ty.RemoveAt(cr.Index);

                    var tRegions = SliceWithCuts(ring, tx, ty, eps);
                    if (MaxShaftsPerRegion(tRegions, shaftPts) <= 1)
                    {
                        if (cr.IsX) splitX.RemoveAt(cr.Index);
                        else splitY.RemoveAt(cr.Index);
                        changed = true;
                        break;
                    }
                }
            }
        }

        private static List<List<Point2d>> SplitAll(List<List<Point2d>> polys, bool vertical, double axis, double eps)
        {
            var outList = new List<List<Point2d>>(polys.Count + 4);
            double minArea = Math.Max(eps * eps * 100.0, 1e-3);
            for (int i = 0; i < polys.Count; i++)
            {
                var poly = polys[i];
                if (poly == null || poly.Count < 3) continue;

                List<Point2d> a = vertical
                    ? PolygonVerticalHalfPlaneClip2d.ClipKeepXLessOrEqual(poly, axis, eps)
                    : PolygonHorizontalHalfPlaneClip2d.ClipKeepYLessOrEqual(poly, axis, eps);
                List<Point2d> b = vertical
                    ? PolygonVerticalHalfPlaneClip2d.ClipKeepXGreaterOrEqual(poly, axis, eps)
                    : PolygonHorizontalHalfPlaneClip2d.ClipKeepYGreaterOrEqual(poly, axis, eps);

                double aa = PolygonVerticalHalfPlaneClip2d.AbsArea(a);
                double bb = PolygonVerticalHalfPlaneClip2d.AbsArea(b);

                if (aa < minArea || bb < minArea)
                {
                    outList.Add(poly);
                }
                else
                {
                    outList.Add(a);
                    outList.Add(b);
                }
            }
            return outList;
        }

        /// <summary>
        /// Intersects an infinite vertical (x = axisCoord) or horizontal (y = axisCoord) line with the polygon ring,
        /// returns the two extreme intersection points along the line (trim segment). Returns false if fewer than 2 hits.
        /// </summary>
        private static bool TryClipAxisLineToBoundary(
            IList<Point2d> ring,
            bool vertical,
            double axisCoord,
            out Point2d a,
            out Point2d b)
        {
            a = default;
            b = default;
            if (ring == null || ring.Count < 3)
                return false;

            const double epp = 1e-9;
            var hits = new List<Point2d>(8);
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var p = ring[i];
                var q = ring[(i + 1) % n];

                if (vertical)
                {
                    double x1 = p.X, x2 = q.X;
                    double y1 = p.Y, y2 = q.Y;
                    double minx = Math.Min(x1, x2), maxx = Math.Max(x1, x2);
                    if (axisCoord < minx - epp || axisCoord > maxx + epp)
                        continue;
                    double ddx = x2 - x1;
                    if (Math.Abs(ddx) <= epp)
                    {
                        continue;
                    }
                    double t = (axisCoord - x1) / ddx;
                    if (t < -epp || t > 1 + epp) continue;
                    double y = y1 + t * (y2 - y1);
                    hits.Add(new Point2d(axisCoord, y));
                }
                else
                {
                    double y1 = p.Y, y2 = q.Y;
                    double x1 = p.X, x2 = q.X;
                    double miny = Math.Min(y1, y2), maxy = Math.Max(y1, y2);
                    if (axisCoord < miny - epp || axisCoord > maxy + epp)
                        continue;
                    double ddy = y2 - y1;
                    if (Math.Abs(ddy) <= epp)
                    {
                        continue;
                    }
                    double t = (axisCoord - y1) / ddy;
                    if (t < -epp || t > 1 + epp) continue;
                    double x = x1 + t * (x2 - x1);
                    hits.Add(new Point2d(x, axisCoord));
                }
            }

            for (int i = 0; i < hits.Count; i++)
            {
                for (int j = hits.Count - 1; j > i; j--)
                {
                    if (Math.Abs(hits[i].X - hits[j].X) <= 1e-6 && Math.Abs(hits[i].Y - hits[j].Y) <= 1e-6)
                        hits.RemoveAt(j);
                }
            }

            if (hits.Count < 2)
                return false;

            int minIdx = 0, maxIdx = 0;
            for (int i = 1; i < hits.Count; i++)
            {
                if (vertical)
                {
                    if (hits[i].Y < hits[minIdx].Y) minIdx = i;
                    if (hits[i].Y > hits[maxIdx].Y) maxIdx = i;
                }
                else
                {
                    if (hits[i].X < hits[minIdx].X) minIdx = i;
                    if (hits[i].X > hits[maxIdx].X) maxIdx = i;
                }
            }

            a = hits[minIdx];
            b = hits[maxIdx];
            return true;
        }
    }
}
