using Autodesk.AutoCAD.DatabaseServices;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Reads the net area of a closed lightweight polyline boundary (drawing-unit square measure).
    /// </summary>
    public static class PolylineNetArea
    {
        public static double Run(Polyline boundary)
        {
            return boundary.Area;
        }
    }
}
