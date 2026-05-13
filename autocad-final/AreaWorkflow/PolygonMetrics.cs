using System.Collections.Generic;

namespace autocad_final.AreaWorkflow
{
    /// <summary>One row for the palette / report: a named zone and its clipped polygon area.</summary>
    public class ZoneTableEntry
    {
        public string Name { get; set; }
        /// <summary>Absolute area in square drawing units (shoelace on the zone ring).</summary>
        public double AreaDrawingUnits { get; set; }
        /// <summary>Area in m² when INSUNITS allows conversion; otherwise null.</summary>
        public double? AreaM2 { get; set; }

        /// <summary>Grid zoning: 0-based shaft index, or -1 for uncovered. Null if not set.</summary>
        public int? ZoneOwnerIndex { get; set; }

        /// <summary>Resolved label for the shaft assigned to this zone (xdata from ASSIGNSHAFTOZONE or default assignment after zoning).</summary>
        public string AssignedShaftName { get; set; }
    }

    public class PolygonMetrics
    {
        public double Area { get; set; }
        public double Perimeter { get; set; }
        public string Layer { get; set; }
        public string RoomName { get; set; }
        public int ShaftCount { get; set; }
        public string ShaftCoordinates { get; set; }

        /// <summary>Floor area divided by shaft count (m²), when INSUNITS supports conversion; otherwise null.</summary>
        public double? ZoneAreaPerShaftM2 { get; set; }

        /// <summary>Human-readable zoning / zone-area outcome for the command line or grid.</summary>
        public string ZoningSummary { get; set; }

        /// <summary>Per-zone names and areas after SPRINKLERZONEAREA draws outlines (Zone 1 … Zone N).</summary>
        public List<ZoneTableEntry> ZoneTable { get; set; }
    }
}

