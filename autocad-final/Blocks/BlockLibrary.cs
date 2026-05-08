using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Blocks
{
    /// <summary>
    /// Centralized manager for the standard block library (BlocksLibrary.dwg).
    ///
    /// CAD designers maintain visuals as named blocks inside BlocksLibrary.dwg.
    /// This class imports definitions into the active drawing on demand via
    /// Database.ReadDwgFile + Database.Insert and tracks what has already been
    /// loaded so subsequent calls are no-ops.
    /// </summary>
    public static class BlockLibrary
    {
        public const string LibraryFileName = "BlocksLibrary.dwg";

        // Canonical block names — must match BlockTableRecord names in BlocksLibrary.dwg.
        public const string NameSprinkler   = "PENDENT SPRINKLER (RG)";
        public const string NameShaft       = "shaft";
        public const string NameReducer     = "reducer";
        public const string NameTee         = "tee";
        public const string NameElbow       = "elbow";
        public const string NameValve       = "valve";
        public const string NameAnnotation  = "annotation";
        public const string NameShaftMarker = "shaft marker";

        // Nested class kept for call-site compatibility (BlockLibrary.Names.Sprinkler, etc.)
        public static class Names
        {
            public const string Sprinkler   = NameSprinkler;
            public const string Shaft       = NameShaft;
            public const string Reducer     = NameReducer;
            public const string Tee         = NameTee;
            public const string Elbow       = NameElbow;
            public const string Valve       = NameValve;
            public const string Annotation  = NameAnnotation;
            public const string ShaftMarker = NameShaftMarker;
        }

        // Per-Database set of block names already imported.
        private static readonly Dictionary<Database, HashSet<string>> _imported
            = new Dictionary<Database, HashSet<string>>();

        // Per-path cache of open side Databases.
        private static readonly Dictionary<string, Database> _libraryCache
            = new Dictionary<string, Database>(StringComparer.OrdinalIgnoreCase);

        private static string _pathOverride;

        public static void SetLibraryPath(string fullPath)
        {
            _pathOverride = string.IsNullOrWhiteSpace(fullPath) ? null : fullPath;
        }

        public static string ResolveLibraryPath()
        {
            if (!string.IsNullOrWhiteSpace(_pathOverride) && File.Exists(_pathOverride))
                return _pathOverride;

            string docs = null;
            try { docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
            catch { }
            if (!string.IsNullOrEmpty(docs))
            {
                string p = Path.Combine(docs, "autocad-final", LibraryFileName);
                if (File.Exists(p)) return p;
            }

            string asmDir = null;
            try { asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
            catch { }
            if (!string.IsNullOrEmpty(asmDir))
            {
                string p1 = Path.Combine(asmDir, LibraryFileName);
                if (File.Exists(p1)) return p1;

                string p2 = Path.Combine(asmDir, "Blocks", LibraryFileName);
                if (File.Exists(p2)) return p2;
            }

            return null;
        }

        /// <summary>
        /// Ensure the named block is in <paramref name="db"/>, importing from
        /// BlocksLibrary.dwg if not already present. Returns ObjectId.Null on failure.
        /// </summary>
        public static ObjectId EnsureBlockLoaded(Database db, string blockName, out string error)
        {
            error = null;
            if (db == null)      { error = "Database is null.";    return ObjectId.Null; }
            if (string.IsNullOrWhiteSpace(blockName)) { error = "Block name is empty."; return ObjectId.Null; }

            ObjectId existing;
            if (TryFindBlock(db, blockName, out existing))
            {
                MarkLoaded(db, blockName);
                return existing;
            }

            string libPath = ResolveLibraryPath();
            if (string.IsNullOrEmpty(libPath))
            {
                error = "BlocksLibrary.dwg not found. Place it under Documents\\autocad-final\\ or the plugin folder.";
                return ObjectId.Null;
            }

            Database srcDb;
            try
            {
                srcDb = GetOrOpenLibrary(libPath);
            }
            catch (Exception ex)
            {
                error = "Failed to open BlocksLibrary.dwg: " + ex.Message;
                return ObjectId.Null;
            }

            ObjectId srcId;
            if (!TryFindBlock(srcDb, blockName, out srcId) || srcId.IsNull)
            {
                error = "Block \"" + blockName + "\" not found in BlocksLibrary.dwg.";
                return ObjectId.Null;
            }

            HarmonizeUnits(db, srcDb);

            try
            {
                db.Insert(blockName, srcDb, true);
            }
            catch (Exception ex)
            {
                error = "Import failed for \"" + blockName + "\": " + ex.Message;
                return ObjectId.Null;
            }

            ObjectId imported;
            if (TryFindBlock(db, blockName, out imported) && !imported.IsNull)
            {
                MarkLoaded(db, blockName);
                return imported;
            }

            error = "Block \"" + blockName + "\" not found after Insert — name mismatch in BlocksLibrary.dwg?";
            return ObjectId.Null;
        }

        /// <summary>
        /// Place a BlockReference of <paramref name="blockName"/> at <paramref name="position"/>,
        /// importing from the library if needed.
        /// </summary>
        public static ObjectId InsertBlock(
            Transaction tr,
            Database db,
            BlockTableRecord owner,
            string blockName,
            Point3d position,
            double scale,
            double rotationRadians,
            string layerName,
            Dictionary<string, string> attributeValues)
        {
            if (tr == null || db == null || owner == null) return ObjectId.Null;

            string unused;
            ObjectId blockDefId = EnsureBlockLoaded(db, blockName, out unused);
            if (blockDefId.IsNull) return ObjectId.Null;

            if (!owner.IsWriteEnabled)
                try { owner.UpgradeOpen(); } catch { }

            var br = new BlockReference(position, blockDefId);
            br.SetDatabaseDefaults(db);

            if (scale > 0 && Math.Abs(scale - 1.0) > 1e-12)
                try { br.ScaleFactors = new Scale3d(scale); } catch { }

            if (Math.Abs(rotationRadians) > 1e-12)
                try { br.Rotation = rotationRadians; } catch { }

            if (!string.IsNullOrWhiteSpace(layerName))
                try { br.Layer = layerName; } catch { }

            owner.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);

            // Materialize attribute references from the block definition.
            try
            {
                var btr = (BlockTableRecord)tr.GetObject(blockDefId, OpenMode.ForRead);
                if (btr.HasAttributeDefinitions)
                {
                    foreach (ObjectId entId in btr)
                    {
                        var ad = tr.GetObject(entId, OpenMode.ForRead, false) as AttributeDefinition;
                        if (ad == null || ad.Constant) continue;
                        var ar = new AttributeReference();
                        ar.SetAttributeFromBlock(ad, br.BlockTransform);
                        ar.SetDatabaseDefaults(db);
                        string val;
                        if (attributeValues != null && attributeValues.TryGetValue(ad.Tag, out val) && val != null)
                            ar.TextString = val;
                        br.AttributeCollection.AppendAttribute(ar);
                        tr.AddNewlyCreatedDBObject(ar, true);
                    }
                }
            }
            catch { }

            return br.ObjectId;
        }

        // Overload with defaults for callers that don't need all parameters.
        public static ObjectId InsertBlock(
            Transaction tr,
            Database db,
            BlockTableRecord owner,
            string blockName,
            Point3d position)
        {
            return InsertBlock(tr, db, owner, blockName, position, 1.0, 0.0, null, null);
        }

        // ── private helpers ────────────────────────────────────────────────────

        private static Database GetOrOpenLibrary(string path)
        {
            Database cached;
            if (_libraryCache.TryGetValue(path, out cached) && cached != null && !cached.IsDisposed)
                return cached;

            var db = new Database(false, true);
            db.ReadDwgFile(path, System.IO.FileShare.Read, true, null);
            db.CloseInput(true);
            _libraryCache[path] = db;
            return db;
        }

        private static bool TryFindBlock(Database db, string name, out ObjectId id)
        {
            id = ObjectId.Null;
            if (db == null || string.IsNullOrWhiteSpace(name)) return false;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (bt.Has(name))
                {
                    id = bt[name];
                    tr.Commit();
                    return true;
                }
                foreach (ObjectId oid in bt)
                {
                    var btr = tr.GetObject(oid, OpenMode.ForRead, false) as BlockTableRecord;
                    if (btr == null || btr.IsLayout || btr.IsAnonymous) continue;
                    if (string.Equals(btr.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        id = oid;
                        tr.Commit();
                        return true;
                    }
                }
                tr.Commit();
            }
            return false;
        }

        private static void HarmonizeUnits(Database target, Database source)
        {
            if (target == null || source == null) return;
            try
            {
                if (target.Insunits == UnitsValue.Undefined && source.Insunits != UnitsValue.Undefined)
                    target.Insunits = source.Insunits;
            }
            catch { }
        }

        private static bool IsLoaded(Database db, string name)
        {
            HashSet<string> set;
            return _imported.TryGetValue(db, out set) && set != null && set.Contains(name);
        }

        private static void MarkLoaded(Database db, string name)
        {
            HashSet<string> set;
            if (!_imported.TryGetValue(db, out set) || set == null)
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _imported[db] = set;
            }
            set.Add(name);
        }
    }
}
