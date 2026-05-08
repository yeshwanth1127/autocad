using Autodesk.AutoCAD.EditorInput;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Writes the polygon net area message to the command line (square drawing units).
    /// </summary>
    public static class EditorWritePolygonNetArea
    {
        public static void Run(Editor ed, double area)
        {
            ed.WriteMessage("\nPolygon area: " + area.ToString("F3") + " sq. drawing units.\n");
        }
    }
}
