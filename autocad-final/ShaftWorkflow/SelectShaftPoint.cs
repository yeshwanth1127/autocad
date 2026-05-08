using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.ShaftWorkflow
{
    public static class SelectShaftPoint
    {
        public static bool Run(Editor ed, out Point3d point)
        {
            point = Point3d.Origin;
            var ppr = ed.GetPoint("\nSelect shaft point: ");
            if (ppr.Status != PromptStatus.OK)
                return false;

            point = ppr.Value;
            return true;
        }
    }
}

