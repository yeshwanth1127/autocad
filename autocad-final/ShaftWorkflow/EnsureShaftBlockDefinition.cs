using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.AreaWorkflow;
using autocad_final.Blocks;

namespace autocad_final.ShaftWorkflow
{
    public static class EnsureShaftBlockDefinition
    {
        public static ObjectId Run(Database db, Transaction tr)
        {
            string shaftBlockName = SprinklerLayers.GetConfiguredShaftBlockName();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (bt.Has(shaftBlockName))
            {
                return bt[shaftBlockName];
            }

            // Try the central library first; fall back to in-code geometry below if missing.
            var imported = autocad_final.Blocks.BlockLibrary.EnsureBlockLoaded(db, shaftBlockName, out _);
            if (!imported.IsNull)
                return imported;

            bt.UpgradeOpen();
            var btr = new BlockTableRecord
            {
                Name = shaftBlockName,
                Origin = Point3d.Origin
            };

            var blockId = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);

            // Symbol: square outline + X (matches standard shaft marker).
            // Fixed drawing-unit size: 0.25 x 0.25.
            double half = 0.125;
            var square = new Polyline(4);
            square.AddVertexAt(0, new Point2d(-half, -half), 0, 0, 0);
            square.AddVertexAt(1, new Point2d(half, -half), 0, 0, 0);
            square.AddVertexAt(2, new Point2d(half, half), 0, 0, 0);
            square.AddVertexAt(3, new Point2d(-half, half), 0, 0, 0);
            square.Closed = true;
            btr.AppendEntity(square);
            tr.AddNewlyCreatedDBObject(square, true);

            var d1 = new Line(new Point3d(-half, -half, 0), new Point3d(half, half, 0));
            var d2 = new Line(new Point3d(-half, half, 0), new Point3d(half, -half, 0));
            btr.AppendEntity(d1);
            btr.AppendEntity(d2);
            tr.AddNewlyCreatedDBObject(d1, true);
            tr.AddNewlyCreatedDBObject(d2, true);

            return blockId;
        }
    }
}

