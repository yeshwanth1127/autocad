using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;

namespace autocad_final.Agent.Planning
{
    /// <summary>
    /// Runs the placement and routing algorithms in read-only mode.
    /// No <c>doc.LockDocument()</c>, no transactions, no drawing modifications.
    /// Called when <see cref="ZonePlan.Preview"/> is <c>true</c> so the LLM can evaluate
    /// a plan's outcome before committing any changes.
    /// All methods run on the UI thread — same threading constraint as the rest of the agent.
    /// </summary>
    public static class PreviewEngine
    {
        public sealed class PreviewResult
        {
            public bool   Success              { get; set; }
            public string ErrorMessage         { get; set; }
            public int    ProjectedHeadCount   { get; set; }
            public bool   ProjectedCoverageOk  { get; set; }
            public string TrunkOrientation     { get; set; }  // "horizontal" | "vertical" | null
            public double ProjectedTrunkLengthM { get; set; }
            public string Summary              { get; set; }
        }

        /// <summary>
        /// Simulates sprinkler grid placement inside the zone ring and returns projected statistics.
        /// Calls <see cref="SprinklerGridPlacement2d.TryPlaceForZoneRing"/> which is already pure-compute.
        /// </summary>
        public static PreviewResult SimulatePlacement(
            Database db,
            Polyline zoneBoundary,
            ZonePlan plan)
        {
            try
            {
                var ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zoneBoundary);
                if (ring == null || ring.Count < 3)
                    return Fail("Zone boundary produced fewer than 3 ring points.");

                bool ok = SprinklerGridPlacement2d.TryPlaceForZoneRing(
                    ring,
                    db,
                    spacingMeters:                  plan.SpacingM,
                    coverageRadiusMeters:           plan.CoverageRadiusM,
                    maxBoundarySprinklerGapMeters:  plan.MaxBoundaryGapM,
                    out var placement,
                    out string err,
                    gridAnchorOffsetXMeters:        plan.GridOffsetXM,
                    gridAnchorOffsetYMeters:        plan.GridOffsetYM);

                if (!ok)
                    return Fail(err ?? "Placement simulation failed.");

                return new PreviewResult
                {
                    Success             = true,
                    ProjectedHeadCount  = placement.Sprinklers?.Count ?? 0,
                    ProjectedCoverageOk = placement.CoverageOk,
                    Summary             =
                        $"Preview: {placement.Sprinklers?.Count ?? 0} heads, " +
                        $"coverage {(placement.CoverageOk ? "OK" : "NOT OK")}. " +
                        (placement.Summary ?? string.Empty)
                };
            }
            catch (Exception ex)
            {
                return Fail("Placement preview error: " + ex.Message);
            }
        }

        /// <summary>
        /// Simulates main pipe routing and returns projected trunk orientation and length.
        /// Uses the zone centroid as a stand-in shaft point (connector path is not the meaningful
        /// output for preview — trunk position and orientation are).
        /// </summary>
        public static PreviewResult SimulateRoute(
            Database db,
            Polyline zoneBoundary,
            ZonePlan plan)
        {
            try
            {
                var ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zoneBoundary);
                if (ring == null || ring.Count < 3)
                    return Fail("Zone boundary produced fewer than 3 ring points.");

                var centroid  = ComputeCentroid(ring);
                var shaftPt   = new Point3d(centroid.X, centroid.Y, zoneBoundary.Elevation);
                string orient = OrientationString(plan.Orientation);

                bool ok = MainPipeRouting2d.TryRoute(
                    zoneBoundary,
                    shaftPt,
                    db,
                    plan.SpacingM,
                    plan.CoverageRadiusM,
                    out var route,
                    out string err,
                    preferredOrientation: orient);

                if (!ok)
                    return Fail(err ?? "Route simulation failed.");

                double trunkLenDu = PolylineLength(route.TrunkPath);
                double duPerM     = DuPerMeter(db);
                double trunkLenM  = duPerM > 0 ? trunkLenDu / duPerM : trunkLenDu;

                return new PreviewResult
                {
                    Success                 = true,
                    ProjectedHeadCount      = route.Sprinklers?.Count ?? 0,
                    TrunkOrientation        = route.TrunkIsHorizontal ? "horizontal" : "vertical",
                    ProjectedTrunkLengthM   = Math.Round(trunkLenM, 2),
                    Summary                 =
                        $"Preview: trunk {(route.TrunkIsHorizontal ? "horizontal" : "vertical")}, " +
                        $"~{trunkLenM:F1} m, {route.Sprinklers?.Count ?? 0} projected heads. " +
                        (route.Summary ?? string.Empty)
                };
            }
            catch (Exception ex)
            {
                return Fail("Route preview error: " + ex.Message);
            }
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        private static PreviewResult Fail(string msg) =>
            new PreviewResult { Success = false, ErrorMessage = msg, Summary = msg };

        private static Point2d ComputeCentroid(List<Point2d> ring)
        {
            double sx = 0, sy = 0;
            foreach (var p in ring) { sx += p.X; sy += p.Y; }
            return new Point2d(sx / ring.Count, sy / ring.Count);
        }

        private static double PolylineLength(List<Point2d> path)
        {
            if (path == null || path.Count < 2) return 0;
            double len = 0;
            for (int i = 1; i < path.Count; i++)
            {
                double dx = path[i].X - path[i - 1].X;
                double dy = path[i].Y - path[i - 1].Y;
                len += Math.Sqrt(dx * dx + dy * dy);
            }
            return len;
        }

        private static double DuPerMeter(Database db)
        {
            if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 1.0, out double du) && du > 0)
                return du;
            return 1.0;
        }

        private static string OrientationString(TrunkOrientation o)
        {
            switch (o)
            {
                case TrunkOrientation.Horizontal: return "horizontal";
                case TrunkOrientation.Vertical:   return "vertical";
                default:                          return "auto";
            }
        }
    }
}
