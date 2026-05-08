using System.Runtime.Serialization;

namespace autocad_final.Agent.Planning
{
    public enum PipeStrategy
    {
        ShortestPath,
        Perimeter,
        Spine
    }

    public enum TrunkOrientation
    {
        Auto,
        Horizontal,
        Vertical
    }

    /// <summary>
    /// The schema-validated parameter contract the LLM emits for every zone write operation.
    /// The LLM may only supply values within the constraints enforced by <see cref="ZonePlanValidator"/>.
    /// Raw coordinates are never part of this contract.
    /// </summary>
    [DataContract]
    public sealed class ZonePlan
    {
        [DataMember(Name = "boundary_handle")]        public string          BoundaryHandle    { get; set; }
        [DataMember(Name = "strategy")]               public PipeStrategy    Strategy          { get; set; }
        [DataMember(Name = "spacing_m")]              public double          SpacingM          { get; set; }
        [DataMember(Name = "coverage_radius_m")]      public double          CoverageRadiusM   { get; set; }
        [DataMember(Name = "orientation")]            public TrunkOrientation Orientation      { get; set; }
        [DataMember(Name = "max_boundary_gap_m")]     public double          MaxBoundaryGapM   { get; set; }
        [DataMember(Name = "trunk_anchored")]         public bool            TrunkAnchored     { get; set; }
        [DataMember(Name = "grid_offset_x_m")]        public double          GridOffsetXM      { get; set; }
        [DataMember(Name = "grid_offset_y_m")]        public double          GridOffsetYM      { get; set; }
        [DataMember(Name = "preview")]                public bool            Preview           { get; set; }
        [DataMember(Name = "override_manual_edits")]  public bool            OverrideManualEdits { get; set; }
    }
}
