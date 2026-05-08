using System;
using System.Text;
using autocad_final.Agent;

namespace autocad_final.Agent.Planning
{
    /// <summary>
    /// Pure validation and clamping for <see cref="ZonePlan"/>. No AutoCAD or drawing references.
    /// <para>
    /// <see cref="Validate"/> is the hard gate — it returns a human-readable rejection reason the
    /// planning loop feeds back to the LLM for a replanning retry.
    /// <see cref="ClampParams"/> is the silent safety net — it enforces range limits after validation
    /// passes, so execution code never receives out-of-range values regardless of drift.
    /// </para>
    /// </summary>
    public static class ZonePlanValidator
    {
        // NFPA 13 light hazard hard limits
        public const double MinSpacingM        = 1.5;
        public const double MaxSpacingM        = 4.6;
        public const double MinCoverageRadiusM = 0.75;
        public const double MaxCoverageRadiusM = 2.3;
        public const double MaxGridOffsetM     = 2.0;

        public sealed class ValidationResult
        {
            public bool   IsValid         { get; }
            public string RejectionReason { get; }

            private ValidationResult(bool valid, string reason)
            {
                IsValid         = valid;
                RejectionReason = reason;
            }

            public static ValidationResult Ok()               => new ValidationResult(true,  null);
            public static ValidationResult Fail(string reason) => new ValidationResult(false, reason);
        }

        /// <summary>
        /// Returns a structured rejection reason the planner can feed verbatim to the LLM.
        /// Returns <see cref="ValidationResult.Ok"/> when the plan is within all constraints.
        /// </summary>
        public static ValidationResult Validate(ZonePlan plan)
        {
            if (plan == null)
                return ValidationResult.Fail("Plan is null.");

            if (string.IsNullOrWhiteSpace(plan.BoundaryHandle))
                return ValidationResult.Fail("boundary_handle is required.");

            var sb = new StringBuilder();

            if (plan.SpacingM < MinSpacingM || plan.SpacingM > MaxSpacingM)
                sb.AppendLine(
                    $"spacing_m={plan.SpacingM:F2} is outside the allowed range [{MinSpacingM}, {MaxSpacingM}] " +
                    $"(NFPA 13 light hazard). Use a value in that range.");

            if (plan.CoverageRadiusM < MinCoverageRadiusM || plan.CoverageRadiusM > MaxCoverageRadiusM)
                sb.AppendLine(
                    $"coverage_radius_m={plan.CoverageRadiusM:F2} is outside [{MinCoverageRadiusM}, {MaxCoverageRadiusM}]. " +
                    $"Use a value in that range.");

            if (plan.SpacingM > 0 && plan.CoverageRadiusM > plan.SpacingM / 2.0 + 1e-9)
                sb.AppendLine(
                    $"coverage_radius_m={plan.CoverageRadiusM:F2} exceeds spacing_m/2={plan.SpacingM / 2:F2}. " +
                    $"Reduce coverage_radius_m or increase spacing_m.");

            if (Math.Abs(plan.GridOffsetXM) > MaxGridOffsetM)
                sb.AppendLine(
                    $"grid_offset_x_m={plan.GridOffsetXM:F2} exceeds max ±{MaxGridOffsetM}. Keep offsets within that range.");

            if (Math.Abs(plan.GridOffsetYM) > MaxGridOffsetM)
                sb.AppendLine(
                    $"grid_offset_y_m={plan.GridOffsetYM:F2} exceeds max ±{MaxGridOffsetM}. Keep offsets within that range.");

            if (plan.MaxBoundaryGapM < 0)
                sb.AppendLine(
                    $"max_boundary_gap_m={plan.MaxBoundaryGapM:F2} must be ≥ 0.");

            if (plan.CoverageRadiusM > 0 && plan.MaxBoundaryGapM > plan.CoverageRadiusM + 1e-9)
                sb.AppendLine(
                    $"max_boundary_gap_m={plan.MaxBoundaryGapM:F2} exceeds coverage_radius_m={plan.CoverageRadiusM:F2}. " +
                    $"Set max_boundary_gap_m ≤ coverage_radius_m.");

            if (sb.Length == 0)
                return ValidationResult.Ok();

            return ValidationResult.Fail("Plan rejected — fix the following parameters and retry:\n" + sb.ToString().TrimEnd());
        }

        /// <summary>
        /// Silently clamps all numeric fields to their valid ranges.
        /// Returns a new <see cref="ZonePlan"/> — the original is not mutated.
        /// </summary>
        public static ZonePlan ClampParams(ZonePlan plan)
        {
            if (plan == null) return null;

            double spacingM = Clamp(plan.SpacingM > 0 ? plan.SpacingM : RuntimeSettings.Load().SprinklerSpacingM, MinSpacingM, MaxSpacingM);
            double radiusM  = Clamp(plan.CoverageRadiusM > 0 ? plan.CoverageRadiusM : spacingM / 2.0,
                                    MinCoverageRadiusM, Math.Min(MaxCoverageRadiusM, spacingM / 2.0));
            double gapM     = Clamp(plan.MaxBoundaryGapM >= 0 ? plan.MaxBoundaryGapM : radiusM, 0, radiusM);

            return new ZonePlan
            {
                BoundaryHandle      = plan.BoundaryHandle,
                Strategy            = plan.Strategy,
                SpacingM            = spacingM,
                CoverageRadiusM     = radiusM,
                Orientation         = plan.Orientation,
                MaxBoundaryGapM     = gapM,
                TrunkAnchored       = plan.TrunkAnchored,
                GridOffsetXM        = Clamp(plan.GridOffsetXM,  -MaxGridOffsetM, MaxGridOffsetM),
                GridOffsetYM        = Clamp(plan.GridOffsetYM,  -MaxGridOffsetM, MaxGridOffsetM),
                Preview             = plan.Preview,
                OverrideManualEdits = plan.OverrideManualEdits
            };
        }

        private static double Clamp(double v, double min, double max)
            => v < min ? min : v > max ? max : v;
    }
}
