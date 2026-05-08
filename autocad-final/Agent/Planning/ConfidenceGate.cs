using System;
using Autodesk.AutoCAD.ApplicationServices;

namespace autocad_final.Agent.Planning
{
    /// <summary>
    /// Post-commit coverage regression detector.
    /// After every committed write tool, re-scans the drawing and compares the new gap-ratio
    /// against the prior ratio. A regression of more than 5% is treated as a confidence failure
    /// and surfaces to the planner as a hard halt (not retried, because data regression means
    /// the algorithm made things worse, not that the parameters were wrong).
    /// Runs on the UI thread — no marshaling needed.
    /// </summary>
    public static class ConfidenceGate
    {
        private const double RegressionThreshold = 0.05;  // >5% worse gap ratio = halt

        public sealed class GateResult
        {
            public bool   Passed       { get; set; }
            public bool   Regressed    { get; set; }
            public double ScoreBefore  { get; set; }  // gap_count / max(1, expected) — lower is better
            public double ScoreAfter   { get; set; }
            public string Summary      { get; set; }
        }

        /// <summary>
        /// Re-scans coverage for <paramref name="boundaryHandle"/> and compares against
        /// <paramref name="priorGapRatio"/>. Returns a <see cref="GateResult"/> with
        /// <see cref="GateResult.Regressed"/> = true when coverage got meaningfully worse.
        /// </summary>
        public static GateResult Evaluate(
            Document doc,
            ProjectMemory memory,
            string boundaryHandle,
            double priorGapRatio)
        {
            try
            {
                var snapshot = AgentReadTools.BuildSnapshot(doc, memory);
                var zone     = snapshot?.Zones == null ? null
                    : System.Linq.Enumerable.FirstOrDefault(snapshot.Zones,
                        z => string.Equals(z.BoundaryHandle, boundaryHandle, StringComparison.OrdinalIgnoreCase));

                if (zone == null)
                {
                    return new GateResult
                    {
                        Passed    = true,   // can't evaluate — don't block
                        Regressed = false,
                        Summary   = "Coverage gate: zone not found after commit — skipping gate."
                    };
                }

                int expected      = zone.ExpectedHeadCount;
                int gaps          = zone.CoverageGaps;
                double afterRatio = expected > 0 ? (double)gaps / expected : 0.0;
                bool regressed    = afterRatio > priorGapRatio + RegressionThreshold;

                return new GateResult
                {
                    Passed      = !regressed,
                    Regressed   = regressed,
                    ScoreBefore = priorGapRatio,
                    ScoreAfter  = afterRatio,
                    Summary     = regressed
                        ? $"Coverage regressed after commit: gap_ratio {priorGapRatio:P1} → {afterRatio:P1} " +
                          $"(expected={expected}, gaps={gaps}). " +
                          $"This zone needs attention — call evaluate_zone for details."
                        : $"Coverage gate passed: gap_ratio {priorGapRatio:P1} → {afterRatio:P1} " +
                          $"(expected={expected}, gaps={gaps})."
                };
            }
            catch (Exception ex)
            {
                return new GateResult
                {
                    Passed    = true,   // gate errors must not block the run
                    Regressed = false,
                    Summary   = "Coverage gate error (ignored): " + ex.Message
                };
            }
        }

        /// <summary>
        /// Reads the current gap-ratio for a zone before a write so the gate has a baseline.
        /// Returns 0 when the zone has no prior coverage data (fresh zone).
        /// </summary>
        public static double ReadCurrentGapRatio(Document doc, ProjectMemory memory, string boundaryHandle)
        {
            try
            {
                var snapshot = AgentReadTools.BuildSnapshot(doc, memory);
                var zone     = snapshot?.Zones == null ? null
                    : System.Linq.Enumerable.FirstOrDefault(snapshot.Zones,
                        z => string.Equals(z.BoundaryHandle, boundaryHandle, StringComparison.OrdinalIgnoreCase));

                if (zone == null || zone.ExpectedHeadCount <= 0)
                    return 0.0;

                return (double)zone.CoverageGaps / zone.ExpectedHeadCount;
            }
            catch
            {
                return 0.0;
            }
        }
    }
}
