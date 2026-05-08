using System;
using Autodesk.AutoCAD.DatabaseServices;
using autocad_final.Blocks;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Resolves the branch reducer block (<see cref="SprinklerLayers.ReducerBlockName"/>) in the current drawing.
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

            string name = SprinklerLayers.GetConfiguredReducerBlockName();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

            if (bt.Has(name))
            {
                blockDefId = bt[name];
                return true;
            }

            foreach (ObjectId oid in bt)
            {
                if (!(tr.GetObject(oid, OpenMode.ForRead, false) is BlockTableRecord btr))
                    continue;
                if (btr.IsLayout || btr.IsAnonymous)
                    continue;
                if (string.Equals(btr.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    blockDefId = oid;
                    return true;
                }
            }

            // Not in drawing — import from BlocksLibrary.dwg.
            var imported = autocad_final.Blocks.BlockLibrary.EnsureBlockLoaded(db, name, out string libErr);
            if (!imported.IsNull)
            {
                blockDefId = imported;
                return true;
            }

            errorMessage =
                "Block \"" + name +
                "\" was not found and could not be loaded from BlocksLibrary.dwg" +
                (string.IsNullOrEmpty(libErr) ? "." : " (" + libErr + ").") +
                " Run Initialize first or add the reducer block to this drawing.";
            return false;
        }
    }
}
