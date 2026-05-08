using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;

namespace autocad_final.Commands
{
    /// <summary>
    /// Automatic floor zoning chain shared by create_sprinkler_zones and SPRINKLERZONEAREA2 (palette: Zone boundary + threshold).
    /// </summary>
    public static class SprinklerFloorZoningCascade
    {
        public const int DefaultShaftVoronoiLloydPasses = 16;

        /// <summary>
        /// Erases prior zone outlines inside the floor, then tries: equal-area bisection → Voronoi+Lloyd →
        /// equal-area strips → shaft midline (with snap retries) → grid → grid+cap.
        /// </summary>
        /// <returns>True when at least one zone outline was produced.</returns>
        public static bool TryRun(
            Document doc,
            Polyline floor,
            ObjectId floorEntityId,
            bool echoMessages,
            out PolygonMetrics metrics,
            out string modeUsed,
            out List<string> createdZoneBoundaryHandles,
            out List<string> fallbacksAttempted,
            out int priorZoneEntitiesErased)
        {
            metrics = null;
            modeUsed = null;
            createdZoneBoundaryHandles = new List<string>();
            fallbacksAttempted = new List<string>();
            priorZoneEntitiesErased = 0;

            if (doc == null || floor == null)
                return false;

            var db = doc.Database;

            var floorRing = new List<Point2d>();
            try
            {
                int fv = floor.NumberOfVertices;
                for (int k = 0; k < fv; k++)
                {
                    var v = floor.GetPoint3dAt(k);
                    floorRing.Add(new Point2d(v.X, v.Y));
                }
            }
            catch { floorRing = null; }

            if (floorRing != null && floorRing.Count >= 3)
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    SprinklerXData.EnsureRegApp(tr, db);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    priorZoneEntitiesErased = SprinklerZoneAutomationCleanup.ClearPriorZoneOutlinesInsideFloor(
                        tr, ms, floorRing, floorEntityId);
                    tr.Commit();
                }
            }

            const int shaftVoronoiLloydPasses = DefaultShaftVoronoiLloydPasses;
            bool preferOrthogonal = IsEasyDividableOrthogonalFloor(floorRing);

            {
                var h = new List<string>();
                PolygonMetrics m;
                ZoneAreaCommand.TryRunWithBoundary(
                    doc, floor, ZoneAreaCommand.ZoningMode.EqualAreaBisection,
                    echoMessages, shaftMidlineSnapSearchMeters: 0, h, out m);
                if (ZoneAreaCommand.OutlinesWereDrawn(m))
                {
                    metrics = m;
                    modeUsed = "equal_area_bisection";
                    createdZoneBoundaryHandles.AddRange(h);
                }
                else
                    fallbacksAttempted.Add("equal_area_bisection: " + (m?.ZoningSummary ?? "no outlines"));
            }

            if (!ZoneAreaCommand.OutlinesWereDrawn(metrics))
            {
                var h = new List<string>();
                PolygonMetrics m;
                if (preferOrthogonal)
                {
                    ZoneAreaCommand.TryRunWithBoundary(
                        doc, floor, ZoneAreaCommand.ZoningMode.EqualAreaStrips,
                        echoMessages, 0, h, out m);
                    if (ZoneAreaCommand.OutlinesWereDrawn(m))
                    {
                        metrics = m;
                        modeUsed = "equal_area_strips";
                        createdZoneBoundaryHandles.Clear();
                        createdZoneBoundaryHandles.AddRange(h);
                    }
                    else
                        fallbacksAttempted.Add("equal_area_strips (preferred): " + (m?.ZoningSummary ?? "no outlines"));
                }
                else
                {
                    ZoneAreaCommand.TryRunWithBoundary(
                        doc, floor, ZoneAreaCommand.ZoningMode.Grid,
                        echoMessages, shaftMidlineSnapSearchMeters: 0, h,
                        gridLloydIterations: shaftVoronoiLloydPasses, out m);
                    if (ZoneAreaCommand.OutlinesWereDrawn(m))
                    {
                        metrics = m;
                        modeUsed = "shaft_voronoi_lloyd";
                        createdZoneBoundaryHandles.Clear();
                        createdZoneBoundaryHandles.AddRange(h);
                    }
                    else
                        fallbacksAttempted.Add("shaft_voronoi_lloyd: " + (m?.ZoningSummary ?? "no outlines"));
                }
            }

            if (!ZoneAreaCommand.OutlinesWereDrawn(metrics))
            {
                var h = new List<string>();
                PolygonMetrics m;
                if (!preferOrthogonal)
                {
                    ZoneAreaCommand.TryRunWithBoundary(
                        doc, floor, ZoneAreaCommand.ZoningMode.EqualAreaStrips,
                        echoMessages, 0, h, out m);
                    if (ZoneAreaCommand.OutlinesWereDrawn(m))
                    {
                        metrics = m;
                        modeUsed = "equal_area_strips";
                        createdZoneBoundaryHandles.Clear();
                        createdZoneBoundaryHandles.AddRange(h);
                    }
                    else
                        fallbacksAttempted.Add("equal_area_strips: " + (m?.ZoningSummary ?? "no outlines"));
                }
            }

            double[] snapRetriesM = { 0, 10, 15 };
            if (!ZoneAreaCommand.OutlinesWereDrawn(metrics))
            {
                foreach (double snapM in snapRetriesM)
                {
                    var h = new List<string>();
                    PolygonMetrics m;
                    double snapArg = snapM > 0 ? snapM : 0;
                    ZoneAreaCommand.TryRunWithBoundary(
                        doc, floor, ZoneAreaCommand.ZoningMode.ShaftMidlineStrips,
                        echoMessages, snapArg, h, out m);

                    if (ZoneAreaCommand.OutlinesWereDrawn(m))
                    {
                        metrics = m;
                        modeUsed = snapArg <= 0 ? "shaft_midline" : "shaft_midline_snap_" + snapM.ToString(CultureInfo.InvariantCulture);
                        createdZoneBoundaryHandles.Clear();
                        createdZoneBoundaryHandles.AddRange(h);
                        break;
                    }

                    fallbacksAttempted.Add(
                        "shaft_midline (snap " + (snapArg <= 0 ? "default_m" : snapM.ToString(CultureInfo.InvariantCulture) + " m") + "): " +
                        (m?.ZoningSummary ?? "no outlines"));
                }
            }

            if (!ZoneAreaCommand.OutlinesWereDrawn(metrics))
            {
                var h = new List<string>();
                PolygonMetrics m;
                ZoneAreaCommand.TryRunWithBoundary(
                    doc, floor, ZoneAreaCommand.ZoningMode.Grid,
                    echoMessages, 0, h, out m);
                if (ZoneAreaCommand.OutlinesWereDrawn(m))
                {
                    metrics = m;
                    modeUsed = "grid_nearest_shaft";
                    createdZoneBoundaryHandles.Clear();
                    createdZoneBoundaryHandles.AddRange(h);
                }
                else
                    fallbacksAttempted.Add("grid: " + (m?.ZoningSummary ?? "no outlines"));
            }

            if (!ZoneAreaCommand.OutlinesWereDrawn(metrics))
            {
                var h = new List<string>();
                PolygonMetrics m;
                ZoneAreaCommand.TryRunWithBoundary(
                    doc, floor, ZoneAreaCommand.ZoningMode.GridWithCap,
                    echoMessages, 0, h, out m);
                if (ZoneAreaCommand.OutlinesWereDrawn(m))
                {
                    metrics = m;
                    modeUsed = "grid_with_cap";
                    createdZoneBoundaryHandles.Clear();
                    createdZoneBoundaryHandles.AddRange(h);
                }
                else
                    fallbacksAttempted.Add("grid_with_cap: " + (m?.ZoningSummary ?? "no outlines"));
            }

            return ZoneAreaCommand.OutlinesWereDrawn(metrics);
        }

        private static bool IsEasyDividableOrthogonalFloor(List<Point2d> ring)
        {
            if (ring == null || ring.Count < 4)
                return false;

            // Bounding box
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
            double w = maxX - minX;
            double h = maxY - minY;
            if (w <= 1e-9 || h <= 1e-9)
                return false;

            // "Rectangle-ish": polygon fills most of its bbox area.
            double area = 0.0;
            try { area = PolygonVerticalHalfPlaneClip2d.AbsArea(ring); } catch { area = 0.0; }
            double bboxArea = w * h;
            double fill = bboxArea > 1e-9 ? (area / bboxArea) : 0.0;
            if (fill < 0.78)
                return false;

            // Mostly orthogonal edges: most segments are near horizontal or near vertical.
            int n = ring.Count;
            int ortho = 0;
            int edges = 0;
            // within ~15 degrees of axis
            const double tan15 = 0.2679491924311227;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double adx = System.Math.Abs(dx);
                double ady = System.Math.Abs(dy);
                if (adx + ady <= 1e-9) continue;
                edges++;
                bool nearHorizontal = ady <= adx * tan15;
                bool nearVertical = adx <= ady * tan15;
                if (nearHorizontal || nearVertical)
                    ortho++;
            }
            if (edges < 4) return false;
            double orthoRatio = (double)ortho / edges;
            if (orthoRatio < 0.80)
                return false;

            return true;
        }
    }
}
