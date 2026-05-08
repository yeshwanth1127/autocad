using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Blocks;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Inserts the pendent sprinkler block at placement points (block must be defined in the drawing).
    /// </summary>
    public static class PendentSprinklerBlockInsert
    {
        public static bool TryGetBlockDefinitionId(Transaction tr, Database db, out ObjectId blockDefId, out string errorMessage)
        {
            blockDefId = ObjectId.Null;
            errorMessage = null;
            if (tr == null || db == null)
            {
                errorMessage = "Invalid database.";
                return false;
            }

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            string sprinklerBlockName = SprinklerLayers.GetConfiguredSprinklerBlockName();

            if (bt.Has(sprinklerBlockName))
            {
                blockDefId = bt[sprinklerBlockName];
                return true;
            }

            foreach (ObjectId oid in bt)
            {
                if (!(tr.GetObject(oid, OpenMode.ForRead, false) is BlockTableRecord btr))
                    continue;
                if (btr.IsLayout || btr.IsAnonymous)
                    continue;
                if (string.Equals(btr.Name, sprinklerBlockName, StringComparison.OrdinalIgnoreCase))
                {
                    blockDefId = oid;
                    return true;
                }
            }

            // Not in drawing — import from BlocksLibrary.dwg.
            var imported = autocad_final.Blocks.BlockLibrary.EnsureBlockLoaded(db, sprinklerBlockName, out string libErr);
            if (!imported.IsNull)
            {
                blockDefId = imported;
                return true;
            }

            errorMessage =
                "Block \"" + sprinklerBlockName +
                "\" was not found and could not be loaded from BlocksLibrary.dwg" +
                (string.IsNullOrEmpty(libErr) ? "." : " (" + libErr + ").") +
                " Run Initialize first or add the block definition to this drawing.";
            return false;
        }

        public static void AppendBlocksAtPoints(
            Transaction tr,
            Database db,
            BlockTableRecord modelSpace,
            Polyline zone,
            List<Point2d> points,
            ObjectId blockDefId,
            ObjectId layerId,
            string zoneBoundaryHandleHex,
            double rotationRadians = 0.0)
        {
            AppendBlocksAtPoints(tr, db, modelSpace, zone, points, blockDefId, layerId, zoneBoundaryHandleHex, rotationRadians, createdIds: null);
        }

        public static void AppendBlocksAtPoints(
            Transaction tr,
            Database db,
            BlockTableRecord modelSpace,
            Polyline zone,
            List<Point2d> points,
            ObjectId blockDefId,
            ObjectId layerId,
            string zoneBoundaryHandleHex,
            double rotationRadians,
            List<ObjectId> createdIds)
        {
            if (tr == null || db == null || modelSpace == null || zone == null || points == null || blockDefId.IsNull)
                return;

            SprinklerXData.EnsureRegApp(tr, db);

            foreach (var p in points)
            {
                var ins = new Point3d(p.X, p.Y, zone.Elevation);
                var br = new BlockReference(ins, blockDefId);
                br.SetDatabaseDefaults(db);
                br.LayerId = layerId;
                br.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                try
                {
                    br.Normal = zone.Normal;
                }
                catch
                {
                    // ignore
                }
                try
                {
                    br.Rotation = rotationRadians;
                }
                catch
                {
                    // ignore
                }

                if (!string.IsNullOrEmpty(zoneBoundaryHandleHex))
                    SprinklerXData.ApplyZoneBoundaryTag(br, zoneBoundaryHandleHex);

                modelSpace.AppendEntity(br);
                tr.AddNewlyCreatedDBObject(br, true);
                try { createdIds?.Add(br.ObjectId); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Inserts pendent heads on above/below layers (paired along trunk).
        /// </summary>
        public static void AppendPairedBlocksAtPoints(
            Transaction tr,
            Database db,
            BlockTableRecord modelSpace,
            Polyline zone,
            List<Point2d> abovePoints,
            List<Point2d> belowPoints,
            ObjectId blockDefId,
            ObjectId aboveLayerId,
            ObjectId belowLayerId,
            string zoneBoundaryHandleHex,
            double rotationRadians = 0.0)
        {
            AppendBlocksAtPoints(tr, db, modelSpace, zone, abovePoints ?? new List<Point2d>(), blockDefId, aboveLayerId, zoneBoundaryHandleHex, rotationRadians);
            AppendBlocksAtPoints(tr, db, modelSpace, zone, belowPoints ?? new List<Point2d>(), blockDefId, belowLayerId, zoneBoundaryHandleHex, rotationRadians);
        }
    }
}
