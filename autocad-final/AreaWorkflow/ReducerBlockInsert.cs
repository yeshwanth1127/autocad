using System;
using Autodesk.AutoCAD.DatabaseServices;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Resolves the branch reducer block from the current drawing.
    /// Accepts block definitions named <see cref="SprinklerLayers.ReducerBlockName"/>
    /// or <see cref="SprinklerLayers.McdReducerBlockName"/> only.
    /// </summary>
    public static class ReducerBlockInsert
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

            foreach (string candidate in SprinklerLayers.ReducerBlockCandidateNames)
            {
                if (TryFindBlockDefinitionInDrawing(tr, db, candidate, out blockDefId))
                    return true;
            }

            errorMessage =
                "No reducer block found in this drawing. Add a block named \"" +
                SprinklerLayers.ReducerBlockName +
                "\" or \"" +
                SprinklerLayers.McdReducerBlockName +
                "\" (run Initialize to create the standard reducer block).";
            return false;
        }

        private static bool TryFindBlockDefinitionInDrawing(
            Transaction tr,
            Database db,
            string blockName,
            out ObjectId blockDefId)
        {
            blockDefId = ObjectId.Null;
            if (string.IsNullOrWhiteSpace(blockName))
                return false;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

            if (bt.Has(blockName))
            {
                blockDefId = bt[blockName];
                return !blockDefId.IsNull;
            }

            foreach (ObjectId oid in bt)
            {
                BlockTableRecord btr = null;
                try { btr = tr.GetObject(oid, OpenMode.ForRead, false) as BlockTableRecord; }
                catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
                if (btr == null) continue;
                if (btr.IsLayout || btr.IsAnonymous)
                    continue;
                if (string.Equals(btr.Name, blockName, StringComparison.OrdinalIgnoreCase))
                {
                    blockDefId = oid;
                    return true;
                }
            }

            return false;
        }
    }
}
