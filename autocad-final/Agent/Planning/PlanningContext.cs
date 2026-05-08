using System.Collections.Generic;
using autocad_final.Agent.Planning.Validators;

namespace autocad_final.Agent.Planning
{
    /// <summary>
    /// Shared mutable bag written by <see cref="PlanningToolInterceptor"/> and read by
    /// <see cref="PlanningLoop"/> after each <c>AgentLoop.RunAsync</c> completes.
    /// Avoids coupling <c>PlanningLoop</c> to the <c>IAgentObserver</c> interface.
    /// All access is on the UI thread — no locking needed.
    /// </summary>
    public sealed class PlanningContext
    {
        private readonly List<string> _rejectionReasons = new List<string>();

        /// <summary>Reasons collected from schema/state-machine rejections this run.</summary>
        public IReadOnlyList<string> PlanRejectionReasons => _rejectionReasons;

        /// <summary>Set when a confidence gate detected coverage regression after a commit.</summary>
        public bool HadConfidenceRegression { get; set; }

        /// <summary>Human-readable summary of the regression for surfacing to the user.</summary>
        public string ConfidenceRegressionSummary { get; set; }

        /// <summary>
        /// Most recent validator findings from the last successful write tool.
        /// Consumed by AgentLoop to overlay markers on the vision-feedback screenshot.
        /// </summary>
        public ValidationReport LastValidationReport { get; set; }

        public void AddRejection(string reason)
        {
            if (!string.IsNullOrWhiteSpace(reason))
                _rejectionReasons.Add(reason);
        }

        /// <summary>Clears all state so the context can be reused for the next retry iteration.</summary>
        public void Reset()
        {
            _rejectionReasons.Clear();
            HadConfidenceRegression      = false;
            ConfidenceRegressionSummary  = null;
            LastValidationReport         = null;
        }
    }
}
