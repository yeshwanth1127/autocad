using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using autocad_final.Agent;

namespace autocad_final.Agent.Planning
{
    /// <summary>
    /// Outer retry envelope around <see cref="AgentLoop"/>.
    /// Responsibilities:
    ///   1. Rebuild a fresh <see cref="DrawingSnapshot"/> before each retry iteration
    ///      so the LLM always sees current drawing state.
    ///   2. Detect <c>plan_rejected</c> results from <see cref="PlanningToolInterceptor"/>
    ///      and feed the rejection reasons back as a new user message for replanning.
    ///   3. Halt immediately (no retry) when a <c>confidence_regression</c> is detected.
    ///   4. Surface a clear error to the user after all retries are exhausted.
    /// </summary>
    public sealed class PlanningLoop
    {
        private const int MaxPlanRetries = 3;

        private readonly AgentLoop       _agentLoop;
        private readonly ZoneStateMachine _stateMachine;
        private readonly IAgentObserver  _observer;
        private readonly Document        _doc;
        private readonly ProjectMemory   _memory;
        private readonly string          _systemPromptBase;

        public PlanningLoop(
            AgentLoop        agentLoop,
            ZoneStateMachine stateMachine,
            IAgentObserver   observer,
            Document         doc,
            ProjectMemory    memory,
            string           systemPromptBase = null)
        {
            _agentLoop        = agentLoop   ?? throw new ArgumentNullException(nameof(agentLoop));
            _stateMachine     = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
            _observer         = observer    ?? throw new ArgumentNullException(nameof(observer));
            _doc              = doc;
            _memory           = memory      ?? new ProjectMemory();
            _systemPromptBase = string.IsNullOrWhiteSpace(systemPromptBase)
                                    ? RuntimeSettings.DefaultSystemPrompt
                                    : systemPromptBase;
        }

        /// <summary>
        /// Runs the planning loop with retry. Called from
        /// <see cref="UI.SprinklerPaletteControl.RunAgentCoreAsync"/> in place of the old
        /// <c>AgentLoop.RunAsync</c> call. No <c>ConfigureAwait(false)</c> — stays on UI thread.
        /// </summary>
        /// <param name="screenshotBase64Png">Optional full-screen capture for the user turn (vision model).</param>
        public async Task RunAsync(
            string                   userMessage,
            DrawingSnapshot          initialSnapshot,
            CancellationToken        cancellationToken,
            List<OpenRouterMessage>  conversationHistory = null,
            string                   screenshotBase64Png = null)
        {
            var allRejections = new List<string>();
            string currentMessage = userMessage;

            for (int attempt = 1; attempt <= MaxPlanRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // ── Fresh perception every iteration ──────────────────────────────
                DrawingSnapshot snapshot;
                try
                {
                    snapshot = AgentReadTools.BuildSnapshot(_doc, _memory);
                    AgentLog.Write("PlanningLoop", $"attempt={attempt} snapshot zones={snapshot?.Zones?.Count ?? 0}");
                }
                catch (Exception ex)
                {
                    _observer.OnError("Failed to scan drawing before planning: " + ex.Message);
                    return;
                }

                // ── Build system prompt with fresh snapshot ────────────────────────
                string systemPrompt = BuildSystemPrompt(snapshot);

                // ── Shared context bag for this run ───────────────────────────────
                var context = new PlanningContext();

                // Inject the context into the interceptor so it can record rejections.
                // The interceptor is already constructed with a reference to the same context
                // that was passed when building the ToolDispatcher. We expose it via the
                // dispatcher's interceptor field — but to avoid coupling, we reset the context
                // in-place before each run (PlanningContext.Reset clears prior state).
                // The same PlanningContext instance is shared with the interceptor via the
                // PlanningToolInterceptor constructor (held in ToolDispatcher._interceptor).
                // The caller (SprinklerPaletteControl) passes the same context to both.
                // Here we just use the context that was already wired at construction time —
                // but we reset it so leftover state from a previous retry doesn't bleed through.
                context.Reset();  // local; the real context lives in the interceptor — see note below.

                // NOTE: The PlanningToolInterceptor holds its OWN PlanningContext reference,
                // passed at construction time. We need the same object here to read results.
                // SprinklerPaletteControl passes the same PlanningContext to both PlanningLoop
                // and PlanningToolInterceptor, so _sharedContext below IS the same object.
                _sharedContext.Reset();

                AgentLog.Write("PlanningLoop", $"attempt={attempt} calling agentLoop.RunAsync");
                // Only attach screen capture on the first attempt — retries use plain text replan messages.
                var screenOnce = attempt == 1 ? screenshotBase64Png : null;
                await _agentLoop.RunAsync(currentMessage, systemPrompt, _observer, cancellationToken, conversationHistory, screenOnce);
                AgentLog.Write("PlanningLoop", $"attempt={attempt} agentLoop done. rejections={_sharedContext.PlanRejectionReasons.Count} regression={_sharedContext.HadConfidenceRegression}");

                // ── Confidence regression — halt, do not retry ────────────────────
                if (_sharedContext.HadConfidenceRegression)
                {
                    _observer.OnError(
                        "Coverage regressed after a committed change. " +
                        (_sharedContext.ConfidenceRegressionSummary ?? string.Empty) +
                        " Call cleanup_zone and re-design the affected zone.");
                    return;
                }

                // ── No rejections — done ──────────────────────────────────────────
                if (_sharedContext.PlanRejectionReasons.Count == 0)
                    return;

                // ── Collect rejections and build retry message ────────────────────
                allRejections.AddRange(_sharedContext.PlanRejectionReasons);

                if (attempt == MaxPlanRetries)
                    break;

                var retryMsg = new StringBuilder();
                retryMsg.AppendLine($"The previous plan was rejected {_sharedContext.PlanRejectionReasons.Count} time(s). Fix these issues and retry:");
                foreach (var r in _sharedContext.PlanRejectionReasons)
                    retryMsg.AppendLine("- " + r);

                currentMessage = retryMsg.ToString().TrimEnd();
                AgentLog.Write("PlanningLoop", $"attempt={attempt} retrying with rejection feedback");
            }

            // All retries exhausted
            var summary = new StringBuilder("Plan failed after " + MaxPlanRetries + " retries. Rejection reasons:\n");
            foreach (var r in allRejections.Distinct())
                summary.AppendLine("- " + r);

            _observer.OnError(summary.ToString().TrimEnd());
        }

        // ── Shared context (injected at construction, same instance as in interceptor) ──
        // Initialized to a throw-away instance so RunAsync never null-refs if SetSharedContext
        // is somehow skipped. SprinklerPaletteControl always calls SetSharedContext with the
        // real instance that is also wired into PlanningToolInterceptor.
        private PlanningContext _sharedContext = new PlanningContext();

        /// <summary>
        /// Replaces the shared <see cref="PlanningContext"/> with the instance that is also
        /// wired into <see cref="PlanningToolInterceptor"/>. Must be called before
        /// <see cref="RunAsync"/> to ensure both classes share the same object.
        /// </summary>
        public void SetSharedContext(PlanningContext context)
            => _sharedContext = context ?? throw new ArgumentNullException(nameof(context));

        // ── System prompt ─────────────────────────────────────────────────────────

        private string BuildSystemPrompt(DrawingSnapshot snapshot)
        {
            var zones  = snapshot?.Zones         != null ? snapshot.Zones.Count         : 0;
            var shafts = snapshot?.Shafts        != null ? snapshot.Shafts.Count        : 0;
            var issues = snapshot?.PendingIssues != null ? snapshot.PendingIssues.Count : 0;

            var sb = new StringBuilder();
            sb.AppendLine(_systemPromptBase);
            sb.AppendLine();
            sb.AppendLine("CURRENT SNAPSHOT (live, rebuilt this iteration):");
            sb.Append("zones=").Append(zones)
              .Append(", shafts=").Append(shafts)
              .Append(", pending_issues=").Append(issues).AppendLine();

            if (snapshot?.Census != null)
            {
                var c = snapshot.Census;
                sb.Append("drawing: total_entities=").Append(c.TotalEntityCount)
                  .Append(", sprinkler_scope_entities=").Append(c.SprinklerScopeEntityCount)
                  .Append(", closed_polylines=").Append(c.ClosedPolylineCount)
                  .Append(", text=").Append(c.TextCount).AppendLine();
                sb.AppendLine("Prioritize sprinkler automation layers (floor boundary, sprinkler - zone, zone labels).");

                if (c.LayersSprinklerScope != null && c.LayersSprinklerScope.Count > 0)
                {
                    sb.Append("layers (sprinkler scope): ");
                    foreach (var l in c.LayersSprinklerScope.OrderByDescending(x => x.EntityCount).Take(12))
                        sb.Append(l.Name).Append('=').Append(l.EntityCount).Append(' ');
                    sb.AppendLine();
                }
                else if (c.Layers != null && c.Layers.Count > 0)
                {
                    sb.Append("layers (full drawing sample): ");
                    foreach (var l in c.Layers.OrderByDescending(x => x.EntityCount).Take(8))
                        sb.Append(l.Name).Append('=').Append(l.EntityCount).Append(' ');
                    sb.AppendLine();
                }
            }

            sb.AppendLine();
            sb.AppendLine("PLANNING CONSTRAINTS:");
            sb.AppendLine("- You are a constrained optimizer. Search the parameter space of the tools to maximize coverage.");
            sb.AppendLine("- Use preview=true before committing any large zone design to verify projected head count.");
            sb.AppendLine("- strategy: \"shortest_path\" (default A*), \"perimeter\" (boundary-following), \"spine\" (central axis).");
            sb.AppendLine("- orientation: \"auto\" (engine chooses), \"horizontal\", \"vertical\".");
            sb.AppendLine("- spacing_m must be in [1.5, 4.6]. coverage_radius_m must be in [0.75, 2.3] and ≤ spacing_m/2.");
            sb.AppendLine("- STATE MACHINE: route_main_pipe must succeed before place_sprinklers. place_sprinklers must succeed before attach_branches.");
            sb.AppendLine("  If a zone is in Failed state, call cleanup_zone first.");
            sb.AppendLine("- Do NOT call place_sprinklers or attach_branches without a preceding successful route_main_pipe for that zone.");

            sb.AppendLine();
            sb.Append(JsonSupport.Serialize(new
            {
                nfpa             = snapshot?.Nfpa,
                recent_decisions = snapshot?.RecentDecisions,
                pending_issues   = snapshot?.PendingIssues,
                memory = new
                {
                    hazard_class      = _memory?.HazardClass          ?? "Light",
                    default_spacing_m = _memory?.DefaultSpacingM      ?? RuntimeSettings.Load().SprinklerSpacingM,
                    max_coverage_m2   = _memory?.MaxCoveragePerHeadM2 ?? 20.9
                }
            }));

            return sb.ToString();
        }
    }
}
