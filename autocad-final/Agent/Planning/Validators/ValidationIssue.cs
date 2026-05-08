using System.Collections.Generic;
using System.Runtime.Serialization;

namespace autocad_final.Agent.Planning.Validators
{
    public enum IssueSeverity { Info, Warning, Error }

    public enum IssueCategory
    {
        BoundaryExit,
        WallDistance,
        CoverageGap,
        ZoneOverlap,
        ShaftIntersect,
        Obstruction
    }

    /// <summary>
    /// One geometric violation found by a validator after a write tool commits.
    /// Serialised into the tool response JSON so the LLM can adapt.
    /// </summary>
    [DataContract]
    public sealed class ValidationIssue
    {
        [DataMember(Name = "severity")]     public string Severity    { get; set; }
        [DataMember(Name = "category")]     public string Category    { get; set; }
        [DataMember(Name = "x")]            public double X           { get; set; }
        [DataMember(Name = "y")]            public double Y           { get; set; }
        [DataMember(Name = "description")]  public string Description { get; set; }
        [DataMember(Name = "suggested_fix")]public string SuggestedFix{ get; set; }
        [DataMember(Name = "auto_fixable")] public bool   AutoFixable { get; set; }
    }

    public sealed class ValidationReport
    {
        public List<ValidationIssue> Issues { get; } = new List<ValidationIssue>();
        public int AutoFixedCount { get; set; }

        public bool HasErrors
        {
            get
            {
                for (int i = 0; i < Issues.Count; i++)
                    if (Issues[i].Severity == "error") return true;
                return false;
            }
        }

        public void Add(IssueSeverity sev, IssueCategory cat, double x, double y, string desc, string fix, bool autoFixable)
        {
            Issues.Add(new ValidationIssue
            {
                Severity     = sev.ToString().ToLowerInvariant(),
                Category     = CategoryToString(cat),
                X            = x,
                Y            = y,
                Description  = desc,
                SuggestedFix = fix,
                AutoFixable  = autoFixable
            });
        }

        private static string CategoryToString(IssueCategory c)
        {
            switch (c)
            {
                case IssueCategory.BoundaryExit:   return "boundary_exit";
                case IssueCategory.WallDistance:   return "wall_distance";
                case IssueCategory.CoverageGap:    return "coverage_gap";
                case IssueCategory.ZoneOverlap:    return "zone_overlap";
                case IssueCategory.ShaftIntersect: return "shaft_intersect";
                default:                           return "obstruction";
            }
        }
    }
}
