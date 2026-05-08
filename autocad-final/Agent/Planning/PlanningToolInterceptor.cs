using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using autocad_final.Agent;
using autocad_final.Agent.Planning.Validators;

namespace autocad_final.Agent.Planning
{
    /// <summary>
    /// Wraps every write-tool handler with the full guard pipeline:
    /// schema validation → parameter clamping → state-machine check → preview branch →
    /// original handler → confidence gate.
    /// Runs entirely on the UI thread. Never throws — always returns JSON.
    /// </summary>
    public sealed class PlanningToolInterceptor
    {
        private readonly ZoneStateMachine _stateMachine;
        private readonly PlanningContext  _context;
        private readonly Document         _doc;
        private readonly ProjectMemory    _memory;

        public PlanningToolInterceptor(
            ZoneStateMachine stateMachine,
            PlanningContext  context,
            Document         doc,
            ProjectMemory    memory)
        {
            _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
            _context      = context      ?? throw new ArgumentNullException(nameof(context));
            _doc          = doc;
            _memory       = memory       ?? new ProjectMemory();
        }

        /// <summary>
        /// Entry point called by <see cref="ToolDispatcher"/> for every write tool.
        /// Returns a JSON string in all cases.
        /// </summary>
        public string Intercept(string toolName, string argsJson, Func<string, string> originalHandler)
        {
            try
            {
                // ── 1. Parse ZonePlan from args ───────────────────────────────────
                var plan = ParseZonePlan(argsJson, _memory);

                // ── 2. Schema validation ──────────────────────────────────────────
                // Skip validation for tools that don't carry zone-design params (cleanup, attach, design).
                if (NeedsParamValidation(toolName))
                {
                    var vr = ZonePlanValidator.Validate(plan);
                    if (!vr.IsValid)
                    {
                        _context.AddRejection(vr.RejectionReason);
                        return PlanRejected(toolName, plan.BoundaryHandle, vr.RejectionReason);
                    }
                }

                // ── 3. Clamp parameters (silent safety net) ───────────────────────
                plan = ZonePlanValidator.ClampParams(plan) ?? plan;

                // ── 4. State-machine precondition check ───────────────────────────
                if (!_stateMachine.CanExecute(toolName, plan.BoundaryHandle, out string blockReason))
                {
                    _context.AddRejection(blockReason);
                    return PlanRejected(toolName, plan.BoundaryHandle, blockReason);
                }

                // ── 5. Preview branch — no commit ─────────────────────────────────
                if (plan.Preview && _doc != null)
                {
                    return RunPreview(toolName, plan);
                }

                // ── 6. Capture baseline coverage score before write ───────────────
                double priorRatio = _doc != null
                    ? ConfidenceGate.ReadCurrentGapRatio(_doc, _memory, plan.BoundaryHandle)
                    : 0.0;

                // ── 7. Execute original handler with clamped parameters ───────────
                // Rebuild args JSON so the handler sees clamped values, not the raw LLM output.
                // Nested arrays (e.g. cuts[]) are lost by DeserializeDictionary — pass raw JSON for those tools.
                string handlerArgs = ShouldPassArgsVerbatim(toolName) ? (argsJson ?? string.Empty) : BuildClampedArgsJson(plan, argsJson);
                string result = originalHandler(handlerArgs);

                // ── 8. Post-commit state machine + confidence gate ─────────────────
                bool success = result != null && result.Contains("\"status\":\"ok\"");
                if (success)
                {
                    _stateMachine.RecordSuccess(toolName, plan.BoundaryHandle);

                    if (_doc != null)
                    {
                        var gate = ConfidenceGate.Evaluate(_doc, _memory, plan.BoundaryHandle, priorRatio);
                        if (gate.Regressed)
                        {
                            _context.HadConfidenceRegression     = true;
                            _context.ConfidenceRegressionSummary = gate.Summary;
                            return ConfidenceRegression(toolName, plan.BoundaryHandle, gate);
                        }

                        // ── 9. Geometric validators — merge issues into the tool result ───
                        if (NeedsGeometricValidation(toolName))
                        {
                            try
                            {
                                var report = ValidatorRunner.RunForZone(_doc, plan.BoundaryHandle, plan.CoverageRadiusM);
                                if (report != null && report.Issues.Count > 0)
                                {
                                    _context.LastValidationReport = report;
                                    result = MergeValidationIntoResult(result, report, plan.BoundaryHandle);
                                }
                            }
                            catch (Exception vex)
                            {
                                AgentLog.Write("PlanningToolInterceptor", "validator error: " + vex.Message);
                            }
                        }
                    }
                }
                else
                {
                    _stateMachine.RecordFailure(toolName, plan.BoundaryHandle);
                }

                return result;
            }
            catch (Exception ex)
            {
                return JsonSupport.Serialize(new
                {
                    status           = "error",
                    tool             = toolName,
                    message          = "Interceptor error: " + ex.Message
                });
            }
        }

        // ── Preview ───────────────────────────────────────────────────────────────

        private string RunPreview(string toolName, ZonePlan plan)
        {
            Autodesk.AutoCAD.DatabaseServices.Polyline zone = null;
            try
            {
                AgentWriteTools.TryResolveBoundary(_doc.Database, plan.BoundaryHandle, out zone, out _);
                if (zone == null)
                    return JsonSupport.Serialize(new { status = "error", message = "boundary_handle not found for preview." });

                PreviewEngine.PreviewResult preview;
                switch (toolName.ToLowerInvariant())
                {
                    case "route_main_pipe":
                        preview = PreviewEngine.SimulateRoute(_doc.Database, zone, plan);
                        break;
                    case "place_sprinklers":
                    case "design_zone":
                        preview = PreviewEngine.SimulatePlacement(_doc.Database, zone, plan);
                        break;
                    default:
                        return JsonSupport.Serialize(new { status = "ok", preview = true, message = "Preview not applicable for " + toolName + ". Set preview=false to commit." });
                }

                if (!preview.Success)
                    return JsonSupport.Serialize(new { status = "error", preview = true, message = preview.ErrorMessage });

                return JsonSupport.Serialize(new
                {
                    status            = "ok",
                    preview           = true,
                    boundary_handle   = plan.BoundaryHandle,
                    projected_heads   = preview.ProjectedHeadCount,
                    coverage_ok       = preview.ProjectedCoverageOk,
                    trunk_orientation = preview.TrunkOrientation,
                    trunk_length_m    = preview.ProjectedTrunkLengthM,
                    summary           = preview.Summary,
                    next_step         = "Preview complete. If the projected result looks good, re-call with preview=false to commit."
                });
            }
            finally
            {
                try { zone?.Dispose(); } catch { }
            }
        }

        // ── JSON result helpers ───────────────────────────────────────────────────

        private static string PlanRejected(string toolName, string handle, string reason) =>
            JsonSupport.Serialize(new
            {
                status           = "plan_rejected",
                tool             = toolName,
                boundary_handle  = handle,
                rejection_reason = reason,
                next_step        = "Fix the rejection reason and retry."
            });

        private static string ConfidenceRegression(string toolName, string handle, ConfidenceGate.GateResult gate) =>
            JsonSupport.Serialize(new
            {
                status          = "confidence_regression",
                tool            = toolName,
                boundary_handle = handle,
                score_before    = gate.ScoreBefore,
                score_after     = gate.ScoreAfter,
                summary         = gate.Summary,
                next_step       = "Coverage regressed after this commit. Call evaluate_zone to inspect, then cleanup_zone and re-design."
            });

        // ── Parsing ───────────────────────────────────────────────────────────────

        internal static ZonePlan ParseZonePlan(string argsJson, ProjectMemory memory)
        {
            var v = JsonSupport.DeserializeDictionary(argsJson ?? string.Empty);

            var cfgParse = RuntimeSettings.Load();
            double defaultSpacing = memory?.DefaultSpacingM > 0 ? memory.DefaultSpacingM : cfgParse.SprinklerSpacingM;
            double spacing  = GetDouble(v, "spacing_m",         defaultSpacing);
            double radius   = GetDouble(v, "coverage_radius_m", spacing / 2.0);
            double gap      = GetDouble(v, "max_boundary_gap_m", cfgParse.SprinklerToBoundaryDistanceM);

            return new ZonePlan
            {
                BoundaryHandle      = GetString(v, "boundary_handle"),
                Strategy            = ParseStrategy(GetString(v, "strategy")),
                SpacingM            = spacing,
                CoverageRadiusM     = radius,
                Orientation         = ParseOrientation(GetString(v, "preferred_orientation") ?? GetString(v, "orientation")),
                MaxBoundaryGapM     = gap,
                TrunkAnchored       = GetBool(v, "trunk_anchored", defaultValue: true),
                GridOffsetXM        = GetDouble(v, "grid_anchor_offset_x_m", 0),
                GridOffsetYM        = GetDouble(v, "grid_anchor_offset_y_m", 0),
                Preview             = GetBool(v, "preview", defaultValue: false),
                OverrideManualEdits = GetBool(v, "override_manual_edits", defaultValue: false)
            };
        }

        private static bool ShouldPassArgsVerbatim(string toolName)
        {
            switch (toolName?.ToLowerInvariant())
            {
                case "create_zones_by_cuts":
                    return true;
                default:
                    return false;
            }
        }

        private static bool NeedsParamValidation(string toolName)
        {
            switch (toolName?.ToLowerInvariant())
            {
                case "route_main_pipe":
                case "place_sprinklers":
                case "design_zone":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Tools whose output geometry is worth running the post-commit validators on.</summary>
        private static bool NeedsGeometricValidation(string toolName)
        {
            switch (toolName?.ToLowerInvariant())
            {
                case "route_main_pipe":
                case "place_sprinklers":
                case "attach_branches":
                case "design_zone":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Folds validator findings into the tool's existing JSON result.
        /// Appends a <c>validation_issues</c> array plus a summary flag so the LLM
        /// sees them alongside the tool's own status.
        /// </summary>
        private static string MergeValidationIntoResult(string originalJson, ValidationReport report, string boundaryHandle)
        {
            if (report == null || report.Issues.Count == 0 || string.IsNullOrEmpty(originalJson))
                return originalJson;

            var sb = new StringBuilder();
            // Strip final '}' so we can append fields inside the same object.
            int closeIdx = originalJson.LastIndexOf('}');
            if (closeIdx < 0) return originalJson;

            sb.Append(originalJson, 0, closeIdx);

            // Ensure preceding character is safe for comma insertion.
            bool needsComma = closeIdx > 0 && originalJson[closeIdx - 1] != '{';
            if (needsComma) sb.Append(',');

            sb.Append("\"validation_issue_count\":").Append(report.Issues.Count);
            sb.Append(",\"validation_has_errors\":").Append(report.HasErrors ? "true" : "false");
            sb.Append(",\"validation_issues\":").Append(JsonSupport.Serialize(report.Issues));
            sb.Append(",\"validated_boundary_handle\":\"").Append(boundaryHandle ?? string.Empty).Append('"');
            sb.Append(",\"validation_next_step\":\"Review validation_issues. Adjust spacing_m, grid_anchor_offset_x_m / y_m, or strategy and retry with preview=true before committing again.\"");

            sb.Append('}');
            return sb.ToString();
        }

        private static PipeStrategy ParseStrategy(string s)
        {
            if (string.IsNullOrEmpty(s)) return PipeStrategy.ShortestPath;
            switch (s.ToLowerInvariant().Replace("-", "_").Replace(" ", "_"))
            {
                case "perimeter": return PipeStrategy.Perimeter;
                case "spine":     return PipeStrategy.Spine;
                default:          return PipeStrategy.ShortestPath;
            }
        }

        private static TrunkOrientation ParseOrientation(string s)
        {
            if (string.IsNullOrEmpty(s)) return TrunkOrientation.Auto;
            switch (s.ToLowerInvariant())
            {
                case "horizontal": return TrunkOrientation.Horizontal;
                case "vertical":   return TrunkOrientation.Vertical;
                default:           return TrunkOrientation.Auto;
            }
        }

        // ── Clamped args builder ──────────────────────────────────────────────────

        /// <summary>
        /// Merges original flat args with clamped plan values so the downstream handler
        /// always receives values that have passed schema validation and safety clamping.
        /// Non-plan keys (e.g. boundary_handle, override_manual_edits) are preserved as-is.
        /// </summary>
        private static string BuildClampedArgsJson(ZonePlan plan, string originalArgsJson)
        {
            // Start from the original values so unknown keys are preserved.
            var vals = JsonSupport.DeserializeDictionary(originalArgsJson ?? string.Empty);

            // Overwrite with clamped plan values.
            vals["boundary_handle"]        = plan.BoundaryHandle ?? string.Empty;
            vals["spacing_m"]              = plan.SpacingM;
            vals["coverage_radius_m"]      = plan.CoverageRadiusM;
            vals["max_boundary_gap_m"]     = plan.MaxBoundaryGapM;
            vals["trunk_anchored"]         = plan.TrunkAnchored;
            vals["grid_anchor_offset_x_m"] = plan.GridOffsetXM;
            vals["grid_anchor_offset_y_m"] = plan.GridOffsetYM;
            vals["preview"]                = plan.Preview;
            vals["override_manual_edits"]  = plan.OverrideManualEdits;

            string orientStr = plan.Orientation == TrunkOrientation.Horizontal ? "horizontal"
                             : plan.Orientation == TrunkOrientation.Vertical   ? "vertical"
                             : "auto";
            vals["orientation"]            = orientStr;
            vals["preferred_orientation"]  = orientStr;

            string stratStr = plan.Strategy == PipeStrategy.Perimeter ? "perimeter"
                            : plan.Strategy == PipeStrategy.Spine      ? "spine"
                            : "shortest_path";
            vals["strategy"]               = stratStr;

            return JsonSupport.Serialize(vals);
        }

        private static string GetString(Dictionary<string, object> v, string key)
        {
            if (v.TryGetValue(key, out var o) && o != null) return o.ToString();
            return string.Empty;
        }

        private static double GetDouble(Dictionary<string, object> v, string key, double fallback)
        {
            if (!v.TryGetValue(key, out var o) || o == null) return fallback;
            if (o is double d) return d;
            if (double.TryParse(o.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double p))
                return p;
            return fallback;
        }

        private static bool GetBool(Dictionary<string, object> v, string key, bool defaultValue)
        {
            if (!v.TryGetValue(key, out var o) || o == null) return defaultValue;
            if (o is bool b) return b;
            if (bool.TryParse(o.ToString(), out bool p)) return p;
            return defaultValue;
        }
    }
}
