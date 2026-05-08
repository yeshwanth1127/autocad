using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Agent.Planning.Validators
{
    /// <summary>
    /// Runs all post-commit geometric validators for one zone and returns a report.
    /// Called by <see cref="PlanningToolInterceptor"/> after a successful write tool.
    /// </summary>
    internal static class ValidatorRunner
    {
        public static ValidationReport RunForZone(
            Document doc,
            string boundaryHandleHex,
            double coverageRadiusM)
        {
            var report = new ValidationReport();
            if (doc == null || string.IsNullOrEmpty(boundaryHandleHex)) return report;

            Polyline zone = null;
            try
            {
                if (!AgentWriteTools.TryResolveBoundary(doc.Database, boundaryHandleHex, out zone, out _))
                    return report;
                if (zone == null) return report;

                var ring = RingGeometry.FromPolyline(zone);
                if (ring.Count < 3) return report;

                try { BoundaryContainmentValidator.Validate(doc.Database, boundaryHandleHex, ring, report); } catch (Exception ex) { AgentLog.Write("Validator", "BoundaryContainment: " + ex.Message); }
                try { WallDistanceValidator.Validate(doc.Database, boundaryHandleHex, ring, coverageRadiusM, report); } catch (Exception ex) { AgentLog.Write("Validator", "WallDistance: " + ex.Message); }
                try { CoverageGapValidator.Validate(doc.Database, boundaryHandleHex, ring, coverageRadiusM, report); } catch (Exception ex) { AgentLog.Write("Validator", "CoverageGap: " + ex.Message); }
                try { ShaftIntersectValidator.Validate(doc.Database, boundaryHandleHex, report); } catch (Exception ex) { AgentLog.Write("Validator", "ShaftIntersect: " + ex.Message); }
            }
            finally
            {
                try { zone?.Dispose(); } catch { }
            }
            return report;
        }

        /// <summary>
        /// Extracts marker points (in WCS) from the report so the caller can overlay
        /// them on a screenshot for the vision model.
        /// </summary>
        public static List<(double x, double y)> GetMarkerPoints(ValidationReport report)
        {
            var pts = new List<(double, double)>();
            if (report == null) return pts;
            foreach (var i in report.Issues)
            {
                if (i.X == 0 && i.Y == 0) continue; // synthetic summary entries have no location
                pts.Add((i.X, i.Y));
            }
            return pts;
        }
    }
}
