using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.ShaftWorkflow
{
    public static class InsertShaftBlockReference
    {
        public static ObjectId Run(Database db, Transaction tr, Point3d insertionPoint, ObjectId blockDefId, ObjectId shaftLayerId)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            var br = new BlockReference(insertionPoint, blockDefId);
            if (!shaftLayerId.IsNull)
                br.LayerId = shaftLayerId;
            var id = ms.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);
            return id;
        }
    }
}

