using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;

namespace autocad_final.Agent.Planning.Validators
{
    /// <summary>
    /// Flags pipe segments (tagged to this zone) that pass through any shaft block
    /// reference on the shaft layer. Uses the shaft's bounding rectangle as the
    /// intersection test polygon.
    /// </summary>
    internal static class ShaftIntersectValidator
    {
        public static void Validate(
            Database db,
            string boundaryHandleHex,
            ValidationReport report)
        {
            if (db == null || string.IsNullOrEmpty(boundaryHandleHex)) return;

            var shaftBoxes = new List<(Point2d min, Point2d max)>();
            var pipes = new List<(Point2d a, Point2d b, string layer)>();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent)) continue;
                    string layer;
                    try { layer = ent.Layer; } catch { continue; }

                    if (ent is BlockReference br)
                    {
                        if (layer != null && layer.IndexOf("shaft", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            try
                            {
                                var b = br.GeometricExtents;
                                shaftBoxes.Add((new Point2d(b.MinPoint.X, b.MinPoint.Y),
                                                new Point2d(b.MaxPoint.X, b.MaxPoint.Y)));
                            }
                            catch { /* ignore unreadable extents */ }
                        }
                    }
                    else if (ent is Polyline pl)
                    {
                        if (pl.Closed)
                            continue;
                        bool isPipe =
                            string.Equals(layer, SprinklerLayers.BranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(layer, SprinklerLayers.McdBranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(layer, SprinklerLayers.McdConnectorBranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                            SprinklerLayers.IsMainPipeLayerName(layer);
                        if (!isPipe) continue;

                        if (!SprinklerXData.TryGetZoneBoundaryHandle(pl, out string tag) ||
                            !string.Equals(tag, boundaryHandleHex, StringComparison.OrdinalIgnoreCase)) continue;

                        int nv = pl.NumberOfVertices;
                        for (int i = 0; i + 1 < nv; i++)
                            pipes.Add((pl.GetPoint2dAt(i), pl.GetPoint2dAt(i + 1), layer));
                    }
                }
                tr.Commit();
            }

            if (shaftBoxes.Count == 0 || pipes.Count == 0) return;

            int flagged = 0;
            foreach (var (a, b, layer) in pipes)
            {
                foreach (var (bmin, bmax) in shaftBoxes)
                {
                    if (SegmentAabbIntersect(a, b, bmin, bmax))
                    {
                        double mx = 0.5 * (a.X + b.X);
                        double my = 0.5 * (a.Y + b.Y);
                        report.Add(
                            IssueSeverity.Error,
                            IssueCategory.ShaftIntersect,
                            mx, my,
                            string.Format("Pipe on '{0}' crosses a shaft block at (~{1:0.00}, {2:0.00}).", layer, mx, my),
                            "Re-route main pipe around shaft; use strategy=\"perimeter\" if shortest_path clips through voids.",
                            autoFixable: false);
                        flagged++;
                        if (flagged >= 6) return;
                        break;
                    }
                }
            }
        }

        private static bool SegmentAabbIntersect(Point2d a, Point2d b, Point2d bmin, Point2d bmax)
        {
            // Quick AABB-overlap reject
            double segMinX = Math.Min(a.X, b.X), segMaxX = Math.Max(a.X, b.X);
            double segMinY = Math.Min(a.Y, b.Y), segMaxY = Math.Max(a.Y, b.Y);
            if (segMaxX < bmin.X || segMinX > bmax.X) return false;
            if (segMaxY < bmin.Y || segMinY > bmax.Y) return false;

            // If either endpoint inside box → intersects.
            if (a.X >= bmin.X && a.X <= bmax.X && a.Y >= bmin.Y && a.Y <= bmax.Y) return true;
            if (b.X >= bmin.X && b.X <= bmax.X && b.Y >= bmin.Y && b.Y <= bmax.Y) return true;

            // Clip segment against each box edge.
            var c1 = new Point2d(bmin.X, bmin.Y);
            var c2 = new Point2d(bmax.X, bmin.Y);
            var c3 = new Point2d(bmax.X, bmax.Y);
            var c4 = new Point2d(bmin.X, bmax.Y);

            return RingGeometry.TrySegmentSegmentIntersect(a, b, c1, c2, out _) ||
                   RingGeometry.TrySegmentSegmentIntersect(a, b, c2, c3, out _) ||
                   RingGeometry.TrySegmentSegmentIntersect(a, b, c3, c4, out _) ||
                   RingGeometry.TrySegmentSegmentIntersect(a, b, c4, c1, out _);
        }
    }
}
