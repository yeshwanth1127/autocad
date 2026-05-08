using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Reads selected entities in model space and collects (start,end) segment endpoints from lines and polylines.
    /// </summary>
    public static class CollectSegmentEndpointsFromSelection
    {
        public static List<(Autodesk.AutoCAD.Geometry.Point3d Start, Autodesk.AutoCAD.Geometry.Point3d End)> Run(
            Database db,
            Autodesk.AutoCAD.EditorInput.SelectionSet selection)
        {
            var entities = new List<Entity>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (Autodesk.AutoCAD.EditorInput.SelectedObject so in selection)
                {
                    if (so == null) continue;
                    var ent = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Entity;
                    if (ent != null)
                        entities.Add(ent);
                }
                tr.Commit();
            }
            return ClosedPolylineFromPointsAndSegments.CollectSegments(entities);
        }
    }
}
