using System.Collections.Generic;
using System.Runtime.Serialization;

namespace autocad_final.Agent
{
    [DataContract]
    public sealed class DrawingSnapshot
    {
        [DataMember(Name = "drawing")]
        public string DrawingName { get; set; }

        [DataMember(Name = "zones")]
        public List<ZoneSnapshot> Zones { get; set; }

        [DataMember(Name = "shafts")]
        public List<ShaftSnapshot> Shafts { get; set; }

        [DataMember(Name = "nfpa")]
        public NfpaSnapshot Nfpa { get; set; }

        [DataMember(Name = "recent_decisions")]
        public List<DecisionSnapshot> RecentDecisions { get; set; }

        [DataMember(Name = "pending_issues")]
        public List<string> PendingIssues { get; set; }

        /// <summary>Full drawing census: every layer, entity type breakdown, block inventory, extents.</summary>
        [DataMember(Name = "census")]
        public DrawingCensus Census { get; set; }
    }

    // ── Drawing Census ────────────────────────────────────────────────────────

    /// <summary>
    /// Compact, token-efficient summary of the entire drawing.
    /// Included in every agent context snapshot so the AI always knows what
    /// exists on every layer — not just zone-tagged design content.
    /// Use the drill-down tools (get_all_closed_polylines, get_text_content,
    /// list_entities_on_layer, get_entity_details) for deeper inspection.
    /// </summary>
    [DataContract]
    public sealed class DrawingCensus
    {
        [DataMember(Name = "units")]
        public string Units { get; set; }

        /// <summary>[minX, minY, maxX, maxY] in drawing units.</summary>
        [DataMember(Name = "extents")]
        public double[] Extents { get; set; }

        [DataMember(Name = "total_entity_count")]
        public int TotalEntityCount { get; set; }

        [DataMember(Name = "layers")]
        public List<LayerCensusEntry> Layers { get; set; }

        [DataMember(Name = "block_types")]
        public List<BlockTypeSummary> BlockTypes { get; set; }

        [DataMember(Name = "closed_polyline_count")]
        public int ClosedPolylineCount { get; set; }

        [DataMember(Name = "text_count")]
        public int TextCount { get; set; }

        [DataMember(Name = "dimension_count")]
        public int DimensionCount { get; set; }

        [DataMember(Name = "hatch_count")]
        public int HatchCount { get; set; }

        [DataMember(Name = "line_count")]
        public int LineCount { get; set; }

        /// <summary>Entity count on layers the agent should prioritize (sprinkler automation + floor boundary + zone labels).</summary>
        [DataMember(Name = "sprinkler_scope_entity_count")]
        public int SprinklerScopeEntityCount { get; set; }

        /// <summary>Per-layer breakdown for <see cref="SprinklerScopeEntityCount"/> only (subset of <see cref="Layers"/>).</summary>
        [DataMember(Name = "layers_sprinkler_scope")]
        public List<LayerCensusEntry> LayersSprinklerScope { get; set; }
    }

    [DataContract]
    public sealed class LayerCensusEntry
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "visible")]
        public bool Visible { get; set; }

        [DataMember(Name = "frozen")]
        public bool Frozen { get; set; }

        [DataMember(Name = "entity_count")]
        public int EntityCount { get; set; }

        /// <summary>Keyed by AutoCAD entity class name (e.g. "Polyline", "DBText", "BlockReference").</summary>
        [DataMember(Name = "types")]
        public Dictionary<string, int> EntityTypes { get; set; }
    }

    [DataContract]
    public sealed class BlockTypeSummary
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "count")]
        public int Count { get; set; }

        [DataMember(Name = "sample_layer")]
        public string SampleLayer { get; set; }
    }

    [DataContract]
    public sealed class ClosedPolylineEntry
    {
        [DataMember(Name = "handle")]
        public string Handle { get; set; }

        [DataMember(Name = "layer")]
        public string Layer { get; set; }

        [DataMember(Name = "area_du")]
        public double AreaDu { get; set; }

        [DataMember(Name = "area_m2")]
        public double? AreaM2 { get; set; }

        [DataMember(Name = "perimeter_du")]
        public double PerimeterDu { get; set; }

        [DataMember(Name = "centroid_x")]
        public double CentroidX { get; set; }

        [DataMember(Name = "centroid_y")]
        public double CentroidY { get; set; }

        [DataMember(Name = "vertex_count")]
        public int VertexCount { get; set; }

        [DataMember(Name = "is_zone_boundary")]
        public bool IsZoneBoundary { get; set; }
    }

    [DataContract]
    public sealed class TextEntityEntry
    {
        [DataMember(Name = "handle")]
        public string Handle { get; set; }

        [DataMember(Name = "layer")]
        public string Layer { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }   // "DBText" | "MText"

        [DataMember(Name = "content")]
        public string Content { get; set; }

        [DataMember(Name = "x")]
        public double X { get; set; }

        [DataMember(Name = "y")]
        public double Y { get; set; }
    }

    [DataContract]
    public sealed class EntityOnLayerEntry
    {
        [DataMember(Name = "handle")]
        public string Handle { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "layer")]
        public string Layer { get; set; }

        [DataMember(Name = "x")]
        public double? X { get; set; }

        [DataMember(Name = "y")]
        public double? Y { get; set; }

        /// <summary>Brief human-readable summary: area for polylines, text content for text, block name for references.</summary>
        [DataMember(Name = "summary")]
        public string Summary { get; set; }
    }

    [DataContract]
    public sealed class ZoneSnapshot
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "boundary_handle")]
        public string BoundaryHandle { get; set; }

        [DataMember(Name = "layer")]
        public string Layer { get; set; }

        [DataMember(Name = "area_m2")]
        public double? AreaM2 { get; set; }

        [DataMember(Name = "area_du")]
        public double AreaDrawingUnits { get; set; }

        [DataMember(Name = "perimeter_du")]
        public double PerimeterDrawingUnits { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "head_count")]
        public int HeadCount { get; set; }

        [DataMember(Name = "expected_head_count")]
        public int ExpectedHeadCount { get; set; }

        [DataMember(Name = "coverage_gaps")]
        public int CoverageGaps { get; set; }

        [DataMember(Name = "nearest_shaft_id")]
        public string NearestShaftId { get; set; }

        /// <summary>How many shaft block/hint sites lie inside this zone polygon (plan).</summary>
        [DataMember(Name = "shaft_sites_inside")]
        public int ShaftSitesInside { get; set; }

        /// <summary>True when <see cref="ShaftSitesInside"/> is at least 1 — required for valid shaft-driven zoning.</summary>
        [DataMember(Name = "has_shaft_inside")]
        public bool HasShaftInside { get; set; }

        [DataMember(Name = "has_manual_edits")]
        public bool HasManualEdits { get; set; }

        [DataMember(Name = "summary")]
        public string Summary { get; set; }

        [DataMember(Name = "vertex_count")]
        public int VertexCount { get; set; }

        [DataMember(Name = "centroid_x")]
        public double CentroidX { get; set; }

        [DataMember(Name = "centroid_y")]
        public double CentroidY { get; set; }

        [DataMember(Name = "main_pipe_count")]
        public int MainPipeCount { get; set; }

        [DataMember(Name = "branch_count")]
        public int BranchCount { get; set; }

        [DataMember(Name = "trunk_tagged_count")]
        public int TrunkTaggedCount { get; set; }

        [DataMember(Name = "total_pipe_entities")]
        public int TotalPipeEntities { get; set; }

        [DataMember(Name = "tags")]
        public List<ZoneTagSnapshot> Tags { get; set; }

        /// <summary>Handle of the shaft explicitly assigned via ASSIGNSHAFTOZONE (null if none).</summary>
        [DataMember(Name = "assigned_shaft_handle")]
        public string AssignedShaftHandle { get; set; }

        /// <summary>Dominant angle (degrees, 0–180) of existing tagged trunk entities, null if no trunk yet.</summary>
        [DataMember(Name = "main_pipe_angle_deg")]
        public double? MainPipeAngleDeg { get; set; }

        /// <summary>Number of individually detected trunk segments (0 if no trunk yet).</summary>
        [DataMember(Name = "main_pipe_segment_count")]
        public int MainPipeSegmentCount { get; set; }

        /// <summary>True when the assigned (or detected) shaft lies outside the zone boundary.</summary>
        [DataMember(Name = "shaft_is_outside_zone")]
        public bool ShaftIsOutsideZone { get; set; }
    }

    [DataContract]
    public sealed class ZoneGeometrySnapshot
    {
        [DataMember(Name = "boundary_handle")]
        public string BoundaryHandle { get; set; }

        [DataMember(Name = "layer")]
        public string Layer { get; set; }

        [DataMember(Name = "area_du")]
        public double AreaDrawingUnits { get; set; }

        [DataMember(Name = "area_m2")]
        public double? AreaM2 { get; set; }

        [DataMember(Name = "perimeter_du")]
        public double PerimeterDrawingUnits { get; set; }

        [DataMember(Name = "vertex_count")]
        public int VertexCount { get; set; }

        [DataMember(Name = "centroid_x")]
        public double CentroidX { get; set; }

        [DataMember(Name = "centroid_y")]
        public double CentroidY { get; set; }
    }

    [DataContract]
    public sealed class CoverageSnapshot
    {
        [DataMember(Name = "boundary_handle")]
        public string BoundaryHandle { get; set; }

        [DataMember(Name = "coverage_ok")]
        public bool CoverageOk { get; set; }

        [DataMember(Name = "expected_count")]
        public int ExpectedCount { get; set; }

        [DataMember(Name = "actual_count")]
        public int ActualCount { get; set; }

        [DataMember(Name = "gap_count")]
        public int GapCount { get; set; }

        [DataMember(Name = "summary")]
        public string Summary { get; set; }

        [DataMember(Name = "tags")]
        public List<ZoneTagSnapshot> Tags { get; set; }
    }

    [DataContract]
    public sealed class ZoneTagSnapshot
    {
        [DataMember(Name = "entity_handle")]
        public string EntityHandle { get; set; }

        [DataMember(Name = "entity_type")]
        public string EntityType { get; set; }

        [DataMember(Name = "layer")]
        public string Layer { get; set; }

        [DataMember(Name = "role")]
        public string Role { get; set; }
    }

    [DataContract]
    public sealed class PipeSummarySnapshot
    {
        [DataMember(Name = "boundary_handle")]
        public string BoundaryHandle { get; set; }

        [DataMember(Name = "main_pipe_count")]
        public int MainPipeCount { get; set; }

        [DataMember(Name = "branch_count")]
        public int BranchCount { get; set; }

        [DataMember(Name = "trunk_tagged_count")]
        public int TrunkTaggedCount { get; set; }

        [DataMember(Name = "total_pipe_entities")]
        public int TotalPipeEntities { get; set; }

        [DataMember(Name = "zones")]
        public List<ZoneSnapshot> Zones { get; set; }
    }

    [DataContract]
    public sealed class ZoneTagListSnapshot
    {
        [DataMember(Name = "boundary_handle")]
        public string BoundaryHandle { get; set; }

        [DataMember(Name = "tags")]
        public List<ZoneTagSnapshot> Tags { get; set; }
    }

    [DataContract]
    public sealed class ErrorSnapshot
    {
        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "message")]
        public string Message { get; set; }
    }

    [DataContract]
    public sealed class ShaftSnapshot
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "x")]
        public double X { get; set; }

        [DataMember(Name = "y")]
        public double Y { get; set; }

        [DataMember(Name = "connected_zone_ids")]
        public List<string> ConnectedZoneIds { get; set; }
    }

    [DataContract]
    public sealed class NfpaSnapshot
    {
        [DataMember(Name = "hazard_class")]
        public string HazardClass { get; set; }

        [DataMember(Name = "max_coverage_m2")]
        public double MaxCoverageM2 { get; set; }

        [DataMember(Name = "default_spacing_m")]
        public double DefaultSpacingM { get; set; }

        [DataMember(Name = "max_spacing_m")]
        public double MaxSpacingM { get; set; }

        [DataMember(Name = "preferred_pipe_orientation")]
        public string PreferredPipeOrientation { get; set; }
    }

    [DataContract]
    public sealed class DecisionSnapshot
    {
        [DataMember(Name = "zone_id")]
        public string ZoneId { get; set; }

        [DataMember(Name = "action")]
        public string Action { get; set; }

        [DataMember(Name = "parameters")]
        public string Parameters { get; set; }

        [DataMember(Name = "outcome")]
        public string Outcome { get; set; }

        [DataMember(Name = "timestamp")]
        public string Timestamp { get; set; }

        [DataMember(Name = "engineer_note")]
        public string EngineerNote { get; set; }
    }

    [DataContract]
    public sealed class EngineerCorrection
    {
        [DataMember(Name = "zone_id")]
        public string ZoneId { get; set; }

        [DataMember(Name = "lesson")]
        public string Lesson { get; set; }

        [DataMember(Name = "timestamp")]
        public string Timestamp { get; set; }

        [DataMember(Name = "is_active")]
        public bool IsActive { get; set; }
    }

    [DataContract]
    public sealed class ZoneMemory
    {
        [DataMember(Name = "has_manual_edits")]
        public bool HasManualEdits { get; set; }

        [DataMember(Name = "is_locked")]
        public bool IsLocked { get; set; }

        [DataMember(Name = "last_manual_edit_date")]
        public string LastManualEditDate { get; set; }

        [DataMember(Name = "engineer_note")]
        public string EngineerNote { get; set; }
    }
}
