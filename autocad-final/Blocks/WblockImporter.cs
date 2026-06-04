using System;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Blocks
{
    /// <summary>
    /// Imports a standalone WBLOCK (.dwg) file as a block definition in the current drawing and
    /// places references of it. A WBLOCK's entire model space becomes a single block definition
    /// named after the file (or an explicit name).
    /// </summary>
    public static class WblockImporter
    {
        /// <summary>
        /// Ensures the WBLOCK file at <paramref name="dwgPath"/> is defined as a block in <paramref name="db"/>.
        /// If a block of that name already exists it is reused (not re-imported) unless <paramref name="redefine"/> is true.
        /// Returns ObjectId.Null on failure with <paramref name="error"/> set.
        /// </summary>
        public static ObjectId EnsureDefinitionFromFile(Database db, string dwgPath, out string error, bool redefine = false)
            => EnsureDefinitionFromFile(db, dwgPath, BlockNameFromFile(dwgPath), out error, redefine);

        /// <summary>
        /// Same as <see cref="EnsureDefinitionFromFile(Database,string,out string,bool)"/> but with an explicit block name.
        /// </summary>
        public static ObjectId EnsureDefinitionFromFile(Database db, string dwgPath, string blockName, out string error, bool redefine = false)
        {
            error = null;
            if (db == null) { error = "Database is null."; return ObjectId.Null; }
            if (string.IsNullOrWhiteSpace(dwgPath) || !File.Exists(dwgPath))
            {
                error = "WBLOCK file not found: " + (dwgPath ?? "(null)");
                return ObjectId.Null;
            }
            if (string.IsNullOrWhiteSpace(blockName))
            {
                error = "Block name is empty.";
                return ObjectId.Null;
            }

            // Reuse an existing definition unless the caller wants a redefine.
            if (!redefine && TryGetDefinition(db, blockName, out var existing))
                return existing;

            try
            {
                using (var srcDb = new Database(false, true))
                {
                    srcDb.ReadDwgFile(dwgPath, FileShare.Read, true, null);
                    srcDb.CloseInput(true);

                    // Match units so INSERT doesn't auto-scale the geometry.
                    try
                    {
                        if (db.Insunits == UnitsValue.Undefined && srcDb.Insunits != UnitsValue.Undefined)
                            db.Insunits = srcDb.Insunits;
                    }
                    catch { /* ignore */ }

                    // Whole source model space -> one block definition named blockName.
                    db.Insert(blockName, srcDb, true);
                }
            }
            catch (Exception ex)
            {
                error = "Import failed for \"" + blockName + "\": " + ex.Message;
                return ObjectId.Null;
            }

            if (TryGetDefinition(db, blockName, out var imported))
                return imported;

            error = "Block \"" + blockName + "\" not found after import.";
            return ObjectId.Null;
        }

        /// <summary>
        /// Imports (if needed) and places a reference of the WBLOCK file at <paramref name="position"/> in model space.
        /// Returns the new BlockReference ObjectId, or ObjectId.Null on failure.
        /// </summary>
        public static ObjectId InsertFromFile(
            Transaction tr,
            Database db,
            string dwgPath,
            Point3d position,
            out string error,
            string blockName = null,
            double scale = 1.0,
            double rotationRadians = 0.0,
            string layerName = null)
        {
            error = null;
            blockName = string.IsNullOrWhiteSpace(blockName) ? BlockNameFromFile(dwgPath) : blockName;

            ObjectId defId = EnsureDefinitionFromFile(db, dwgPath, blockName, out error);
            if (defId.IsNull) return ObjectId.Null;

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            var br = new BlockReference(position, defId);
            br.SetDatabaseDefaults(db);
            if (scale > 0 && Math.Abs(scale - 1.0) > 1e-12)
                try { br.ScaleFactors = new Scale3d(scale); } catch { /* ignore */ }
            if (Math.Abs(rotationRadians) > 1e-12)
                try { br.Rotation = rotationRadians; } catch { /* ignore */ }
            if (!string.IsNullOrWhiteSpace(layerName))
                try { br.Layer = layerName; } catch { /* ignore */ }

            ms.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);
            return br.ObjectId;
        }

        /// <summary>Block name convention: the file name without extension (spaces preserved).</summary>
        public static string BlockNameFromFile(string dwgPath)
        {
            if (string.IsNullOrWhiteSpace(dwgPath)) return null;
            try { return Path.GetFileNameWithoutExtension(dwgPath.Trim()); }
            catch { return null; }
        }

        private static bool TryGetDefinition(Database db, string blockName, out ObjectId id)
        {
            id = ObjectId.Null;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (bt.Has(blockName))
                {
                    id = bt[blockName];
                    tr.Commit();
                    return true;
                }
                tr.Commit();
            }
            return false;
        }
    }
}
