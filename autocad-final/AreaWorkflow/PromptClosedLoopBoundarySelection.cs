using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.DatabaseServices;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Prompts the user to select LINE / POLYLINE / LWPOLYLINE entities that form one closed loop boundary.
    /// </summary>
    public static class PromptClosedLoopBoundarySelection
    {
        public static SelectionSet Run(Editor ed)
        {
            var pso = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect lines/polylines forming one closed loop: ",
                AllowDuplicates = false
            };
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LINE,POLYLINE,LWPOLYLINE")
            });
            var psr = ed.GetSelection(pso, filter);
            return psr.Status == PromptStatus.OK ? psr.Value : null;
        }
    }
}
