using System;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.AreaWorkflow;
using autocad_final.ShaftWorkflow;

namespace autocad_final.Blocks
{
    public static class StandardBlockDefinitions
    {
        public static ObjectId EnsureShaft(Database db, Transaction tr) =>
            EnsureShaftBlockDefinition.Run(db, tr);

        public static ObjectId EnsureReducer(Database db, Transaction tr) =>
            EnsureReducerBlockDefinition(db, tr);

        public static ObjectId EnsurePendentSprinkler(Database db, Transaction tr) =>
            EnsurePendentSprinklerBlockDefinition(db, tr);

        private static ObjectId EnsureReducerBlockDefinition(Database db, Transaction tr)
        {
            string name = SprinklerLayers.GetConfiguredReducerBlockName();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (bt.Has(name))
            {
                return bt[name];
            }

            // Prefer central library; fall through to in-code geometry below if unavailable.
            var imported = BlockLibrary.EnsureBlockLoaded(db, name, out _);
            if (!imported.IsNull) return imported;

            bt.UpgradeOpen();
            var btr = new BlockTableRecord { Name = name, Origin = Point3d.Origin };
            var blockId = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);

            // Symbol: trapezoid outline (matches reducer marker).
            // Fixed drawing-unit size: smaller than shaft/sprinkler.
            double bottomHalfW = 0.015;
            double topHalfW = 0.010;
            double halfH = 0.020;
            var pl = new Polyline(4);
            pl.AddVertexAt(0, new Point2d(-bottomHalfW, -halfH), 0, 0, 0); // bottom-left
            pl.AddVertexAt(1, new Point2d(bottomHalfW, -halfH), 0, 0, 0);  // bottom-right
            pl.AddVertexAt(2, new Point2d(topHalfW, halfH), 0, 0, 0);      // top-right
            pl.AddVertexAt(3, new Point2d(-topHalfW, halfH), 0, 0, 0);     // top-left
            pl.Closed = true;
            pl.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
            btr.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);

            return blockId;
        }

        private static ObjectId EnsurePendentSprinklerBlockDefinition(Database db, Transaction tr)
        {
            string name = SprinklerLayers.GetConfiguredSprinklerBlockName();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (bt.Has(name))
            {
                return bt[name];
            }

            // Case-insensitive fallback (some drawings may have different casing).
            foreach (ObjectId oid in bt)
            {
                if (!(tr.GetObject(oid, OpenMode.ForRead, false) is BlockTableRecord btr0))
                    continue;
                if (btr0.IsLayout || btr0.IsAnonymous)
                    continue;
                if (string.Equals(btr0.Name, name, StringComparison.OrdinalIgnoreCase))
                    return oid;
            }

            // Prefer central library; fall through to in-code geometry below if unavailable.
            var imported = BlockLibrary.EnsureBlockLoaded(db, name, out _);
            if (!imported.IsNull) return imported;

            bt.UpgradeOpen();
            var btr = new BlockTableRecord { Name = name, Origin = Point3d.Origin };
            var blockId = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);
            return DrawPendentSprinkler(db, tr, btr, blockId);
        }

        private static ObjectId DrawPendentSprinkler(Database db, Transaction tr, BlockTableRecord btr, ObjectId blockId)
        {
            // Symbol: circle + crosshair (matches pendent sprinkler marker).
            // Fixed drawing-unit size: about 5x smaller than the 0.25 shaft.
            double r = 0.025;
            double crossHalf = 0.035;

            var c = new Circle(Point3d.Origin, Vector3d.ZAxis, r);
            c.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
            btr.AppendEntity(c);
            tr.AddNewlyCreatedDBObject(c, true);

            AddSpoke(btr, tr, db, new Point3d(-crossHalf, 0, 0), new Point3d(crossHalf, 0, 0));
            AddSpoke(btr, tr, db, new Point3d(0, -crossHalf, 0), new Point3d(0, crossHalf, 0));

            return blockId;
        }

        private static void AddSpoke(BlockTableRecord btr, Transaction tr, Database db, Point3d a, Point3d b)
        {
            var ln = new Line(a, b);
            ln.SetDatabaseDefaults(db);
            ln.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
            btr.AppendEntity(ln);
            tr.AddNewlyCreatedDBObject(ln, true);
        }
    }
}

