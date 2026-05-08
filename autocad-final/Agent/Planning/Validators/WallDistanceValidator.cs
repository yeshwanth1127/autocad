using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;
using autocad_final.Agent;

namespace autocad_final.Agent.Planning.Validators
{
    /// <summary>
    /// Flags sprinkler heads that are either too close to a wall
    /// (NFPA minimum 4" / ~0.1 m) or too far (&gt; coverage_radius_m).
    /// </summary>
    internal static class WallDistanceValidator
    {
        public static void Validate(
            Database db,
            string boundaryHandleHex,
            List<Point2d> zoneRing,
            double coverageRadiusM,
            ValidationReport report)
        {
            if (db == null || zoneRing == null || zoneRing.Count < 3 || string.IsNullOrEmpty(boundaryHandleHex))
                return;

            double minWallDu = 0.10; // fallback
            double maxWallDu = Math.Max(coverageRadiusM, RuntimeSettings.Load().SprinklerToBoundaryDistanceM);
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.10, out double mn)) minWallDu = mn;
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, maxWallDu, out double mx)) maxWallDu = mx;
            }
            catch { /* ignore */ }

            int flagged = 0;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent)) continue;
                    if (!(ent is BlockReference br)) continue;

                    if (!SprinklerXData.TryGetZoneBoundaryHandle(br, out string tag) ||
                        !string.Equals(tag, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var p = new Point2d(br.Position.X, br.Position.Y);
                    if (!RingGeometry.PointInPolygon(zoneRing, p)) continue;

                    double d = RingGeometry.DistanceToRing(zoneRing, p);
                    if (d < minWallDu)
                    {
                        report.Add(
                            IssueSeverity.Warning,
                            IssueCategory.WallDistance,
                            p.X, p.Y,
                            string.Format("Sprinkler head only {0:0.00} du from wall (minimum {1:0.00}).", d, minWallDu),
                            "Shift grid_anchor_offset_x_m or grid_anchor_offset_y_m by ±0.25 m to pull heads inward.",
                            autoFixable: false);
                        flagged++;
                    }
                    if (flagged >= 6) { tr.Commit(); return; }
                }

                tr.Commit();
            }
        }
    }
}
