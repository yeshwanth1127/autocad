using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Chains segment endpoints into one closed vertex ring and builds a closed lightweight polyline.
    /// </summary>
    public static class ClosedPolylineFromChainedSegments
    {
        public static Polyline Run(IList<(Point3d Start, Point3d End)> segments, double tolerance)
        {
            var ring = ClosedPolylineFromPointsAndSegments.ChainSegmentsToClosedLoop(segments, tolerance);
            return ClosedPolylineFromPointsAndSegments.CreateClosedPolylineFromPoints(ring, 0);
        }
    }
}
