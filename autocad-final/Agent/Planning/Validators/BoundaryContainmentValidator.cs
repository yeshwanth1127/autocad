using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;

namespace autocad_final.Agent.Planning.Validators
{
    /// <summary>
    /// For every pipe segment tagged to this zone, verify the segment lies entirely
    /// inside the zone ring. Segments that exit the boundary are flagged with the
    /// midpoint of the exit portion as the marker location. Auto-fixable.
    /// </summary>
    internal static class BoundaryContainmentValidator
    {
        public static void Validate(
            Database db,
            string boundaryHandleHex,
            List<Point2d> zoneRing,
            ValidationReport report)
        {
            if (db == null || zoneRing == null || zoneRing.Count < 3 || string.IsNullOrEmpty(boundaryHandleHex))
                return;

            const double exitEpsFraction = 0.01; // 1% of segment length tolerance
            int flagged = 0;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent)) continue;
                    if (!(ent is Polyline pl)) continue;

                    string layer;
                    try { layer = pl.Layer; } catch { continue; }

                    if (pl.Closed)
                        continue;
                    bool isPipe =
                        string.Equals(layer, SprinklerLayers.BranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, SprinklerLayers.McdBranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                        SprinklerLayers.IsMainPipeLayerName(layer);
                    if (!isPipe) continue;

                    if (!SprinklerXData.TryGetZoneBoundaryHandle(pl, out string tag) ||
                        !string.Equals(tag, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                        continue;

                    int nv = pl.NumberOfVertices;
                    for (int i = 0; i + 1 < nv; i++)
                    {
                        var a = pl.GetPoint2dAt(i);
                        var b = pl.GetPoint2dAt(i + 1);
                        double segLen = RingGeometry.Distance(a, b);
                        if (segLen < 1e-7) continue;

                        var inside = RingGeometry.ClipSegmentToRing(a, b, zoneRing);
                        double insideLen = 0;
                        foreach (var iv in inside)
                            insideLen += (iv.t1 - iv.t0) * segLen;

                        double outsideLen = segLen - insideLen;
                        if (outsideLen <= segLen * exitEpsFraction) continue;

                        double midX = 0.5 * (a.X + b.X);
                        double midY = 0.5 * (a.Y + b.Y);
                        report.Add(
                            IssueSeverity.Error,
                            IssueCategory.BoundaryExit,
                            midX, midY,
                            string.Format("Pipe segment on '{0}' exits zone by {1:0.00} drawing units (segment length {2:0.00}).", layer, outsideLen, segLen),
                            "Re-run route_main_pipe or attach_branches with preview=true; reduce strategy aggressiveness or shrink max_boundary_gap_m.",
                            autoFixable: true);

                        flagged++;
                        if (flagged >= 8) { tr.Commit(); return; } // cap per zone to avoid flooding
                    }
                }

                tr.Commit();
            }
        }
    }
}
