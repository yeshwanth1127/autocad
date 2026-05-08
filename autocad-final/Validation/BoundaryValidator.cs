using System;
using Autodesk.AutoCAD.DatabaseServices;
using autocad_final.AreaWorkflow;

namespace autocad_final.Validation
{
    public static class BoundaryValidator
    {
        public static bool IsGlobalZoneBoundary(Database db, ObjectId boundaryEntityId)
        {
            if (db == null || boundaryEntityId == ObjectId.Null)
                return false;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var obj = tr.GetObject(boundaryEntityId, OpenMode.ForRead, false) as Entity;
                string layer = obj?.Layer ?? string.Empty;
                tr.Commit();
                return SprinklerLayers.IsZoneGlobalBoundaryOrMcdZoneOutlineLayerName(layer.Trim());
            }
        }

        public static bool IsMcdFloorBoundary(Database db, ObjectId boundaryEntityId)
        {
            if (db == null || boundaryEntityId == ObjectId.Null)
                return false;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var obj = tr.GetObject(boundaryEntityId, OpenMode.ForRead, false) as Entity;
                string layer = obj?.Layer ?? string.Empty;
                tr.Commit();
                return string.Equals(layer.Trim(), SprinklerLayers.McdFloorBoundaryLayer, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
