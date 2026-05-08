using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using autocad_final.Licensing;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;
using autocad_final.UI;
using System.Windows.Forms;

namespace autocad_final.Commands
{
    /// <summary>
    /// SPRINKLERFIXZONEBOUNDARY — straightens zone-boundary separator segments that are
    /// nearly horizontal / vertical. Segments within an angle threshold of an axis are
    /// snapped to that axis, keeping shared vertices between adjacent zones in sync so
    /// the outlines still meet up after snapping. Vertices that coincide with the floor
    /// boundary polyline are pinned and never moved.
    /// </summary>
    public class FixZoneBoundaryCommand
    {
        // Snap rule: choose the closest axis (horizontal vs vertical) for every
        // eligible segment. This cleans up "slanted" boundaries created by zoning.
        // If you ever want a "leave near-diagonal alone" behavior again, reintroduce
        // an angular threshold here.
        private const double MinSegmentMeters = 0.25;

        [CommandMethod("SPRINKLERFIXZONEBOUNDARY", CommandFlags.Modal)]
        public void FixZoneBoundary()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            if (!TrialGuard.EnsureActive(doc.Editor)) return;
            Run(doc);
        }

        public static void Run(Document doc)
        {
            if (doc == null) return;
            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;
            var db = doc.Database;

            // New workflow: user selects the zone polylines first. We compute a virtual "main boundary"
            // by unioning the selected zone regions and taking the largest resulting ring (outer outline).
            if (!TrySelectZones(ed, db, out var zoneIds))
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "No zones selected.", MessageBoxIcon.Warning);
                return;
            }

            try
            {
                if (!TryBuildMainBoundaryFromZones(db, zoneIds, out var mainRing, out string mainErr))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(
                        ed, mainErr ?? "Could not build main boundary from selected zones.", MessageBoxIcon.Warning);
                    return;
                }

                double extentDu = ExtentOf(mainRing);
                double tolDu = 1e-6;
                try { tolDu = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db); if (tolDu <= 0) tolDu = 1e-6; }
                catch { tolDu = 1e-6; }

                // Pin tolerance: how close a vertex must be to the floor edge to be treated as
                // fixed (shared with the floor). Scales with drawing size so tiny drawings and
                // huge drawings both get sensible behavior.
                double pinTolDu = Math.Max(tolDu * 10.0, Math.Max(1e-4, extentDu * 5e-4));

                // Vertex-merge tolerance for dedup across zone polylines (shared corners).
                double mergeTolDu = Math.Max(tolDu * 10.0, Math.Max(1e-4, extentDu * 1e-4));

                // Skip segments shorter than this for snap decisions (drawing-unit threshold).
                double minSegDu = MinSegmentMeters;
                try
                {
                    if (DrawingUnitsHelper.TryAutoGetDrawingScale(db.Insunits, 1.0, extentDu, out double duPerMeter)
                        && duPerMeter > 0)
                        minSegDu = MinSegmentMeters * duPerMeter;
                }
                catch { /* fall back to MinSegmentMeters in drawing units */ }

                int snapsApplied = 0;
                int polylinesModified = 0;

                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    var zones = new List<Polyline>();
                    var zoneIdsToErase = new List<ObjectId>();
                    foreach (ObjectId id in zoneIds)
                    {
                        Entity ent;
                        try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                        catch { continue; }
                        if (ent == null || ent.IsErased) continue;
                        if (!(ent is Polyline pl)) continue;
                        if (!pl.Closed) continue;
                        if (!SprinklerLayers.IsUnifiedZoneDesignLayerName(pl.Layer)) continue;
                        if (!SprinklerXData.TryGetZoneBoundaryHandle(pl, out string h)) continue;
                        // Zone outlines are tagged with their own handle; skip anything else.
                        if (!string.Equals(h, pl.Handle.ToString(), StringComparison.OrdinalIgnoreCase)) continue;

                        zones.Add(pl);
                        zoneIdsToErase.Add(id);
                    }

                    if (zones.Count == 0)
                    {
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "No valid zone outlines were selected.", MessageBoxIcon.Warning);
                        tr.Commit();
                        return;
                    }

                    // Flatten all vertices into a list and group by position (dedup).
                    var vertPl = new List<int>();
                    var vertIdx = new List<int>();
                    var vertPos = new List<Point2d>();
                    for (int zi = 0; zi < zones.Count; zi++)
                    {
                        int nv = zones[zi].NumberOfVertices;
                        for (int k = 0; k < nv; k++)
                        {
                            var v = zones[zi].GetPoint3dAt(k);
                            vertPl.Add(zi);
                            vertIdx.Add(k);
                            vertPos.Add(new Point2d(v.X, v.Y));
                        }
                    }

                    int total = vertPos.Count;
                    var groupOf = new int[total];
                    for (int i = 0; i < total; i++) groupOf[i] = -1;

                    int groupCount = 0;
                    for (int i = 0; i < total; i++)
                    {
                        if (groupOf[i] >= 0) continue;
                        groupOf[i] = groupCount;
                        for (int j = i + 1; j < total; j++)
                        {
                            if (groupOf[j] >= 0) continue;
                            double dx = vertPos[i].X - vertPos[j].X;
                            double dy = vertPos[i].Y - vertPos[j].Y;
                            if (dx * dx + dy * dy <= mergeTolDu * mergeTolDu)
                                groupOf[j] = groupCount;
                        }
                        groupCount++;
                    }

                    // Average original position per group → stable representative.
                    var sumX = new double[groupCount];
                    var sumY = new double[groupCount];
                    var cnt = new int[groupCount];
                    for (int i = 0; i < total; i++)
                    {
                        int g = groupOf[i];
                        sumX[g] += vertPos[i].X;
                        sumY[g] += vertPos[i].Y;
                        cnt[g] += 1;
                    }
                    var origPos = new Point2d[groupCount];
                    for (int g = 0; g < groupCount; g++)
                        origPos[g] = new Point2d(sumX[g] / cnt[g], sumY[g] / cnt[g]);

                    // Pin groups that sit on the floor boundary.
                    var pinned = new bool[groupCount];
                    for (int g = 0; g < groupCount; g++)
                    {
                        if (DistanceToPolygonEdges(mainRing, origPos[g]) <= pinTolDu)
                            pinned[g] = true;
                    }

                    // Map (polyline, vertex) -> flat index for quick lookup.
                    var plStart = new int[zones.Count + 1];
                    plStart[0] = 0;
                    for (int zi = 1; zi <= zones.Count; zi++)
                        plStart[zi] = plStart[zi - 1] + zones[zi - 1].NumberOfVertices;

                    var ufX = new SimpleUnionFind(groupCount); // vertical edges share X
                    var ufY = new SimpleUnionFind(groupCount); // horizontal edges share Y
                    int horizEdges = 0, vertEdges = 0;

                    // Collect edges so we can also snap "matching" separators across different
                    // zone polylines even when they don't share exact vertices.
                    var horizSegs = new List<AxisSeg>(256);
                    var vertSegs = new List<AxisSeg>(256);

                    for (int zi = 0; zi < zones.Count; zi++)
                    {
                        int nv = zones[zi].NumberOfVertices;
                        int baseIdx = plStart[zi];
                        for (int k = 0; k < nv; k++)
                        {
                            int a = baseIdx + k;
                            int b = baseIdx + ((k + 1) % nv);
                            int gA = groupOf[a];
                            int gB = groupOf[b];
                            if (gA == gB) continue;

                            var pA = origPos[gA];
                            var pB = origPos[gB];
                            double dx = pB.X - pA.X;
                            double dy = pB.Y - pA.Y;
                            double len = Math.Sqrt(dx * dx + dy * dy);
                            if (len < minSegDu) continue;

                            // Both endpoints fixed on floor — cannot snap without distorting the floor.
                            if (pinned[gA] && pinned[gB]) continue;

                            // Snap every eligible segment to the nearest axis.
                            // If a segment is more horizontal than vertical, force it horizontal;
                            // otherwise force it vertical.
                            if (Math.Abs(dx) >= Math.Abs(dy))
                            {
                                ufY.Union(gA, gB);
                                horizEdges++;
                                horizSegs.Add(AxisSeg.From(gA, gB, pA, pB, isHorizontal: true, zi));
                            }
                            else
                            {
                                ufX.Union(gA, gB);
                                vertEdges++;
                                vertSegs.Add(AxisSeg.From(gA, gB, pA, pB, isHorizontal: false, zi));
                            }
                        }
                    }

                    // Second pass: if two segments overlap along their span and are already close
                    // (within tolerance), force them into the same axis-cluster too. This is the
                    // key fix for "separator isn't a real shared boundary" cases.
                    // It makes both zone polylines snap to the same X/Y line even if their vertices differ.
                    SnapOverlappingSegments(ufY, horizSegs, mergeTolDu, isHorizontal: true);
                    SnapOverlappingSegments(ufX, vertSegs, mergeTolDu, isHorizontal: false);

                    var newPos = new Point2d[groupCount];
                    for (int g = 0; g < groupCount; g++) newPos[g] = origPos[g];

                    // Assign Y to horizontal clusters.
                    ApplyCluster(ufY, groupCount, origPos, pinned, newPos, snapAxisIsY: true);
                    // Assign X to vertical clusters.
                    ApplyCluster(ufX, groupCount, origPos, pinned, newPos, snapAxisIsY: false);

                    // Write NEW polylines (fixed), then erase the originals.
                    for (int zi = 0; zi < zones.Count; zi++)
                    {
                        var src = zones[zi];
                        int nv = src.NumberOfVertices;
                        int baseIdx = plStart[zi];
                        bool changed = false;

                        // Build a new polyline with the snapped coordinates.
                        var dst = new Polyline(nv);
                        dst.SetDatabaseDefaults(db);
                        dst.LayerId = src.LayerId;
                        dst.Color = src.Color;
                        dst.LinetypeId = src.LinetypeId;
                        try { dst.LinetypeScale = src.LinetypeScale; } catch { /* ignore */ }
                        try { dst.LineWeight = src.LineWeight; } catch { /* ignore */ }
                        try { dst.ConstantWidth = src.ConstantWidth; } catch { /* ignore */ }
                        try { dst.Elevation = src.Elevation; } catch { /* ignore */ }
                        try { dst.Normal = src.Normal; } catch { /* ignore */ }

                        for (int k = 0; k < nv; k++)
                        {
                            int g = groupOf[baseIdx + k];
                            var np = newPos[g];
                            var old = src.GetPoint2dAt(k);
                            if (Math.Abs(old.X - np.X) > 1e-9 || Math.Abs(old.Y - np.Y) > 1e-9)
                            {
                                changed = true;
                            }

                            double bulge = 0.0;
                            try { bulge = src.GetBulgeAt(k); } catch { bulge = 0.0; }
                            dst.AddVertexAt(k, np, bulge, 0, 0);
                        }
                        dst.Closed = true;

                        ms.AppendEntity(dst);
                        tr.AddNewlyCreatedDBObject(dst, true);

                        // Tag zone outline with its own handle (same convention as zoning).
                        try
                        {
                            SprinklerXData.EnsureRegApp(tr, db);
                            SprinklerXData.ApplyZoneBoundaryTag(dst, dst.Handle.ToString());
                        }
                        catch
                        {
                            /* ignore */
                        }

                        if (changed) polylinesModified++;
                    }

                    int erased = 0;
                    int eraseFailed = 0;
                    for (int i = 0; i < zoneIdsToErase.Count; i++)
                    {
                        try
                        {
                            var obj = tr.GetObject(zoneIdsToErase[i], OpenMode.ForWrite, false);
                            if (obj != null && !obj.IsErased)
                            {
                                obj.Erase();
                                erased++;
                            }
                        }
                        catch
                        {
                            eraseFailed++;
                        }
                    }

                    snapsApplied = horizEdges + vertEdges;
                    tr.Commit();

                    if (eraseFailed > 0)
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(
                            ed,
                            "Fix zone boundary: could not erase " + eraseFailed.ToString(CultureInfo.InvariantCulture) +
                            " original zone polylines (layer may be locked).",
                            MessageBoxIcon.Warning);
                    else
                        ed.WriteMessage("\nFix zone boundary: erased " + erased.ToString(CultureInfo.InvariantCulture) + " original zone polylines.\n");
                }

                try { ed.Regen(); } catch { /* ignore */ }

                ed.WriteMessage(string.Format(CultureInfo.InvariantCulture,
                    "\nFix zone boundary: {0} zone outlines adjusted, {1} segments snapped to X/Y.\n",
                    polylinesModified, snapsApplied));
            }
            finally
            {
                // nothing to dispose
            }
        }

        private static bool TrySelectZones(Editor ed, Database db, out List<ObjectId> zoneIds)
        {
            zoneIds = new List<ObjectId>();
            if (ed == null || db == null) return false;

            var pso = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect zone outline polylines: ",
                MessageForRemoval = "\nRemove: "
            };

            // Filter: lightweight polylines only (zone outlines are LWPOLYLINE).
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
            });

            var psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK || psr.Value == null || psr.Value.Count == 0)
                return false;

            foreach (SelectedObject so in psr.Value)
            {
                if (so == null) continue;
                if (so.ObjectId.IsNull) continue;
                zoneIds.Add(so.ObjectId);
            }

            return zoneIds.Count > 0;
        }

        private static bool TryBuildMainBoundaryFromZones(
            Database db,
            List<ObjectId> zoneIds,
            out List<Point2d> mainRing,
            out string errorMessage)
        {
            mainRing = null;
            errorMessage = null;
            if (db == null || zoneIds == null || zoneIds.Count == 0)
            {
                errorMessage = "No zones selected.";
                return false;
            }

            double tolDu = 1e-6;
            try { tolDu = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db); if (!(tolDu > 0)) tolDu = 1e-6; }
            catch { tolDu = 1e-6; }

            Region union = null;
            var createdRegions = new List<Region>();

            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    for (int i = 0; i < zoneIds.Count; i++)
                    {
                        var ent = tr.GetObject(zoneIds[i], OpenMode.ForRead, false) as Entity;
                        if (!(ent is Polyline pl) || !pl.Closed)
                            continue;

                        if (!RegionBooleanIntersection2d.TryCreateBoundaryRegion(pl, tolDu, out Region r, out _))
                            continue;

                        createdRegions.Add(r);
                        if (union == null)
                            union = (Region)r.Clone();
                        else
                            union.BooleanOperation(BooleanOperationType.BoolUnite, r);
                    }
                    tr.Commit();
                }

                if (union == null)
                {
                    errorMessage = "Could not create a Region union from selected zones (invalid/self-intersecting polylines?).";
                    return false;
                }

                if (!RegionBooleanIntersection2d.TryRegionToRings(union, tolDu, out var rings, out string ringErr))
                {
                    errorMessage = ringErr ?? "Could not extract boundary rings from union region.";
                    return false;
                }

                // Choose largest ring by area as the "main boundary" outer outline.
                double bestA = double.NegativeInfinity;
                List<Point2d> best = null;
                for (int i = 0; i < rings.Count; i++)
                {
                    double a = PolygonVerticalHalfPlaneClip2d.AbsArea(rings[i]);
                    if (a > bestA)
                    {
                        bestA = a;
                        best = rings[i];
                    }
                }

                if (best == null || best.Count < 3)
                {
                    errorMessage = "Union region did not yield a valid outer boundary loop.";
                    return false;
                }

                mainRing = best;
                return true;
            }
            catch (System.Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
            finally
            {
                try { union?.Dispose(); } catch { /* ignore */ }
                for (int i = 0; i < createdRegions.Count; i++)
                {
                    try { createdRegions[i]?.Dispose(); } catch { /* ignore */ }
                }
            }
        }

        private static void ApplyCluster(
            SimpleUnionFind uf,
            int groupCount,
            Point2d[] origPos,
            bool[] pinned,
            Point2d[] newPos,
            bool snapAxisIsY)
        {
            var buckets = new Dictionary<int, List<int>>();
            for (int g = 0; g < groupCount; g++)
            {
                int r = uf.Find(g);
                if (!buckets.TryGetValue(r, out var list))
                {
                    list = new List<int>();
                    buckets[r] = list;
                }
                list.Add(g);
            }

            foreach (var kvp in buckets)
            {
                var members = kvp.Value;
                if (members.Count < 2) continue;

                // Prefer the coordinate of a pinned member if one exists; otherwise average.
                double target;
                int anchorPinned = -1;
                for (int i = 0; i < members.Count; i++)
                {
                    if (pinned[members[i]]) { anchorPinned = members[i]; break; }
                }
                if (anchorPinned >= 0)
                {
                    target = snapAxisIsY ? origPos[anchorPinned].Y : origPos[anchorPinned].X;
                }
                else
                {
                    double sum = 0;
                    for (int i = 0; i < members.Count; i++)
                        sum += snapAxisIsY ? origPos[members[i]].Y : origPos[members[i]].X;
                    target = sum / members.Count;
                }

                for (int i = 0; i < members.Count; i++)
                {
                    int m = members[i];
                    if (pinned[m]) continue;
                    if (snapAxisIsY)
                        newPos[m] = new Point2d(newPos[m].X, target);
                    else
                        newPos[m] = new Point2d(target, newPos[m].Y);
                }
            }
        }

        private readonly struct AxisSeg
        {
            public readonly int GA;
            public readonly int GB;
            public readonly double Coord;  // Y for horizontal, X for vertical
            public readonly double Min;
            public readonly double Max;
            public readonly int ZoneIndex;

            private AxisSeg(int gA, int gB, double coord, double min, double max, int zoneIndex)
            {
                GA = gA;
                GB = gB;
                Coord = coord;
                Min = min;
                Max = max;
                ZoneIndex = zoneIndex;
            }

            public static AxisSeg From(int gA, int gB, Point2d pA, Point2d pB, bool isHorizontal, int zoneIndex)
            {
                if (isHorizontal)
                {
                    double y = 0.5 * (pA.Y + pB.Y);
                    double minX = Math.Min(pA.X, pB.X);
                    double maxX = Math.Max(pA.X, pB.X);
                    return new AxisSeg(gA, gB, y, minX, maxX, zoneIndex);
                }
                else
                {
                    double x = 0.5 * (pA.X + pB.X);
                    double minY = Math.Min(pA.Y, pB.Y);
                    double maxY = Math.Max(pA.Y, pB.Y);
                    return new AxisSeg(gA, gB, x, minY, maxY, zoneIndex);
                }
            }
        }

        private static void SnapOverlappingSegments(SimpleUnionFind uf, List<AxisSeg> segs, double tol, bool isHorizontal)
        {
            if (segs == null || segs.Count < 2) return;

            // Bucket by coordinate so we only compare near-parallel lines.
            // A bucket size of 2*tol is intentional: we still do an exact tol check before unioning.
            double bucket = Math.Max(1e-9, tol * 2.0);
            var buckets = new Dictionary<int, List<AxisSeg>>();
            for (int i = 0; i < segs.Count; i++)
            {
                int key = (int)Math.Round(segs[i].Coord / bucket);
                if (!buckets.TryGetValue(key, out var list))
                {
                    list = new List<AxisSeg>();
                    buckets[key] = list;
                }
                list.Add(segs[i]);
            }

            foreach (var kvp in buckets)
            {
                var list = kvp.Value;
                if (list.Count < 2) continue;

                list.Sort((a, b) => a.Min.CompareTo(b.Min));

                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i];
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        var b = list[j];

                        // Past overlap window (sorted by Min).
                        if (b.Min > a.Max + tol) break;

                        // Only unify if they overlap along the span AND are close in coordinate.
                        bool spansOverlap = Math.Min(a.Max, b.Max) >= Math.Max(a.Min, b.Min) - tol;
                        if (!spansOverlap) continue;
                        if (Math.Abs(a.Coord - b.Coord) > tol) continue;

                        // If two different zones have essentially the same separator segment,
                        // force their endpoints into the same axis-cluster.
                        uf.Union(a.GA, b.GA);
                        uf.Union(a.GA, b.GB);
                        uf.Union(a.GB, b.GA);
                        uf.Union(a.GB, b.GB);
                    }
                }
            }
        }

        private static double ExtentOf(IList<Point2d> ring)
        {
            if (ring == null || ring.Count == 0) return 0.0;
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            for (int i = 0; i < ring.Count; i++)
            {
                var p = ring[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            return Math.Max(maxX - minX, maxY - minY);
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

        private static double DistanceToPolygonEdges(IList<Point2d> ring, Point2d p)
        {
            double best = double.PositiveInfinity;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                double d = PointToSegmentDistance(p, a, b);
                if (d < best) best = d;
            }
            return best;
        }

        private static double PointToSegmentDistance(Point2d p, Point2d a, Point2d b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq <= 1e-24) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double cx = a.X + t * dx, cy = a.Y + t * dy;
            return Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
        }

        private sealed class SimpleUnionFind
        {
            private readonly int[] _parent;
            private readonly int[] _rank;
            public SimpleUnionFind(int n)
            {
                _parent = new int[n];
                _rank = new int[n];
                for (int i = 0; i < n; i++) _parent[i] = i;
            }
            public int Find(int x)
            {
                while (_parent[x] != x)
                {
                    _parent[x] = _parent[_parent[x]];
                    x = _parent[x];
                }
                return x;
            }
            public void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra == rb) return;
                if (_rank[ra] < _rank[rb]) _parent[ra] = rb;
                else if (_rank[ra] > _rank[rb]) _parent[rb] = ra;
                else { _parent[rb] = ra; _rank[ra]++; }
            }
        }
    }
}
