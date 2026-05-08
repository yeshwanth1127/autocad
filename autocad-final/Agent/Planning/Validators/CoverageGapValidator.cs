using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;

namespace autocad_final.Agent.Planning.Validators
{
    /// <summary>
    /// Samples a regular grid inside the zone and flags points that are not within
    /// <paramref name="coverageRadiusM"/> of any sprinkler tagged to the zone.
    /// Returns up to N gap centres to keep the response compact.
    /// </summary>
    internal static class CoverageGapValidator
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

            double radiusDu;
            try
            {
                if (!DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, coverageRadiusM, out radiusDu) || radiusDu <= 0)
                    return;
            }
            catch { return; }

            var heads = new List<Point2d>();
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

                    heads.Add(new Point2d(br.Position.X, br.Position.Y));
                }
                tr.Commit();
            }

            if (heads.Count == 0) return;

            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i < zoneRing.Count; i++)
            {
                var p = zoneRing[i];
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            }

            double step = radiusDu;
            int maxSamples = 1500;
            int cols = Math.Max(1, (int)Math.Ceiling((maxX - minX) / step));
            int rows = Math.Max(1, (int)Math.Ceiling((maxY - minY) / step));
            if (cols * rows > maxSamples)
            {
                double f = Math.Sqrt((double)(cols * rows) / maxSamples);
                step *= f;
                cols = Math.Max(1, (int)Math.Ceiling((maxX - minX) / step));
                rows = Math.Max(1, (int)Math.Ceiling((maxY - minY) / step));
            }

            double r2 = radiusDu * radiusDu;
            int gaps = 0;
            int flagged = 0;
            for (int r = 0; r <= rows; r++)
            {
                double y = minY + r * step;
                if (y > maxY) break;
                for (int c = 0; c <= cols; c++)
                {
                    double x = minX + c * step;
                    if (x > maxX) break;
                    var p = new Point2d(x, y);
                    if (!RingGeometry.PointInPolygon(zoneRing, p)) continue;

                    bool covered = false;
                    for (int h = 0; h < heads.Count; h++)
                    {
                        double dx = heads[h].X - x;
                        double dy = heads[h].Y - y;
                        if (dx * dx + dy * dy <= r2) { covered = true; break; }
                    }
                    if (covered) continue;

                    gaps++;
                    if (flagged < 5)
                    {
                        report.Add(
                            IssueSeverity.Warning,
                            IssueCategory.CoverageGap,
                            x, y,
                            string.Format("Uncovered interior point at ({0:0.00}, {1:0.00}) — no head within {2:0.00} du.", x, y, radiusDu),
                            "Reduce spacing_m by 0.5, or shift grid_anchor_offset_x_m / grid_anchor_offset_y_m by ±0.5 m.",
                            autoFixable: false);
                        flagged++;
                    }
                }
            }
            if (gaps > flagged)
            {
                report.Add(
                    IssueSeverity.Info,
                    IssueCategory.CoverageGap,
                    0, 0,
                    string.Format("Additional {0} uncovered grid points not individually listed.", gaps - flagged),
                    null,
                    autoFixable: false);
            }
        }
    }
}
