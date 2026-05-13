using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using autocad_final.Agent;
using autocad_final.AreaWorkflow;
using autocad_final.Blocks;

namespace autocad_final.Commands
{
    public class InitializeStandardsCommand
    {
        private const double BlockScaleUpFactor = 1.5;
        private enum StarterBlockChoice
        {
            Shaft,
            Reducer,
            Sprinkler
        }

        [CommandMethod("AF_INITSTANDARDS", CommandFlags.Modal)]
        public void Run()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            string wblockFolder = WblockExportService.DefaultWblockFolder();

            try
            {
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    // Layers
                    SprinklerLayers.EnsureAll(tr, db);
                    SprinklerLayers.EnsureMcdSprinklersLayer(tr, db);
                    SprinklerLayers.EnsureMcdShaftsLayer(tr, db);
                    SprinklerLayers.EnsureMcdReducerLayer(tr, db);
                    SprinklerLayers.EnsureMcdMainPipeLayer(tr, db);
                    SprinklerLayers.EnsureMcdBranchPipeLayer(tr, db);
                    SprinklerLayers.EnsureMcdLabelLayer(tr, db);
                    SprinklerLayers.EnsureMcdRoomBoundaryLayer(tr, db);

                    // Block definitions — load from central BlocksLibrary.dwg when present,
                    // otherwise fall back to in-code geometry so the plugin still initializes.
                    var libraryPath = autocad_final.Blocks.BlockLibrary.ResolveLibraryPath();
                    var shaftDefId = LoadOrFallback(db, tr, ed, SprinklerLayers.GetConfiguredShaftBlockName(),
                        () => StandardBlockDefinitions.EnsureShaft(db, tr));
                    var sprinklerDefId = LoadOrFallback(db, tr, ed, SprinklerLayers.GetConfiguredSprinklerBlockName(),
                        () => StandardBlockDefinitions.EnsurePendentSprinkler(db, tr));
                    var reducerDefId = LoadOrFallback(db, tr, ed, SprinklerLayers.GetConfiguredReducerBlockName(),
                        () => StandardBlockDefinitions.EnsureReducer(db, tr));

                    // Optional library blocks (tee, elbow, valve, annotation, shaft marker).
                    // Imported only if defined in BlocksLibrary.dwg — silently skipped otherwise.
                    LoadOptional(db, ed, autocad_final.Blocks.BlockLibrary.NameTee);
                    LoadOptional(db, ed, autocad_final.Blocks.BlockLibrary.NameElbow);
                    LoadOptional(db, ed, autocad_final.Blocks.BlockLibrary.NameValve);
                    LoadOptional(db, ed, autocad_final.Blocks.BlockLibrary.NameAnnotation);
                    LoadOptional(db, ed, autocad_final.Blocks.BlockLibrary.NameShaftMarker);

                    if (string.IsNullOrEmpty(libraryPath))
                        ed.WriteMessage("\n[autocad-final] BlocksLibrary.dwg not found — used built-in fallback geometry. Place BlocksLibrary.dwg under Documents\\autocad-final\\ to enable centralized block authoring.\n");
                    else
                        ed.WriteMessage("\n[autocad-final] Using block library: " + libraryPath + "\n");

                    // Normalize existing inserted instance scales into the definition (instances -> scale=1),
                    // then scale up the definition by 1.5x.
                    NormalizeInstancesThenScaleDefinition(tr, db, shaftDefId, BlockScaleUpFactor);
                    NormalizeInstancesThenScaleDefinition(tr, db, sprinklerDefId, BlockScaleUpFactor);
                    NormalizeInstancesThenScaleDefinition(tr, db, reducerDefId, BlockScaleUpFactor);

                    tr.Commit();

                    // Export WBLOCKs after commit (definitions guaranteed present)
                    Export(ed, db, shaftDefId, wblockFolder);
                    Export(ed, db, sprinklerDefId, wblockFolder);
                    Export(ed, db, reducerDefId, wblockFolder);

                    var choice = PromptStarterBlockChoice(ed);
                    if (choice.HasValue)
                    {
                        InsertSelectedStarterBlock(
                            doc,
                            choice.Value,
                            shaftDefId,
                            reducerDefId,
                            sprinklerDefId);
                    }
                }

                ed.WriteMessage($"\n[autocad-final] Layers/blocks initialized. WBLOCKs saved to: {wblockFolder}\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[autocad-final] Init failed: " + ex.Message + "\n");
            }
        }

        private static ObjectId LoadOrFallback(Database db, Transaction tr, Editor ed, string blockName, Func<ObjectId> fallback)
        {
            var id = autocad_final.Blocks.BlockLibrary.EnsureBlockLoaded(db, blockName, out string err);
            if (!id.IsNull) return id;

            if (!string.IsNullOrEmpty(err))
                ed.WriteMessage("\n[autocad-final] " + err + " Falling back to built-in definition.\n");
            return fallback();
        }

        private static void LoadOptional(Database db, Editor ed, string blockName)
        {
            var id = autocad_final.Blocks.BlockLibrary.EnsureBlockLoaded(db, blockName, out string err);
            if (id.IsNull && !string.IsNullOrEmpty(err))
            {
                // Optional — only log when a library file exists but the named block is missing.
                if (!string.IsNullOrEmpty(autocad_final.Blocks.BlockLibrary.ResolveLibraryPath()))
                    ed.WriteMessage("\n[autocad-final] Optional block \"" + blockName + "\" not loaded: " + err + "\n");
            }
        }

        private static void NormalizeInstancesThenScaleDefinition(Transaction tr, Database db, ObjectId blockDefId, double scaleUpFactor)
        {
            if (tr == null || db == null || blockDefId.IsNull) return;

            // If any inserted instances have non-1 scale, bake that scale into the definition
            // and reset instance scales to 1.0 so future scaling is consistent.
            try
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                if (TryFindFirstInstanceUniformScale(tr, ms, blockDefId, out double bakeScale) &&
                    bakeScale > 0 &&
                    Math.Abs(bakeScale - 1.0) > 1e-9)
                {
                    ScaleBlockDefinitionEntities(tr, blockDefId, bakeScale);
                    ResetAllInstanceScalesToOne(tr, ms, blockDefId);
                }
            }
            catch
            {
                // ignore bake failures; proceed to scale-up
            }

            ScaleBlockDefinitionEntities(tr, blockDefId, scaleUpFactor);
        }

        private static void ResetAllInstanceScalesToOne(Transaction tr, BlockTableRecord modelSpace, ObjectId blockDefId)
        {
            foreach (ObjectId entId in modelSpace)
            {
                if (!(tr.GetObject(entId, OpenMode.ForWrite, false) is BlockReference br))
                    continue;
                if (br.BlockTableRecord != blockDefId)
                    continue;
                try { br.ScaleFactors = new Scale3d(1.0); } catch { /* ignore */ }
            }
        }

        private static bool TryFindFirstInstanceUniformScale(Transaction tr, BlockTableRecord modelSpace, ObjectId blockDefId, out double scale)
        {
            scale = 1.0;
            foreach (ObjectId entId in modelSpace)
            {
                if (!(tr.GetObject(entId, OpenMode.ForRead, false) is BlockReference br))
                    continue;
                if (br.BlockTableRecord != blockDefId)
                    continue;
                if (TryGetUniformScale(br, out double s))
                {
                    scale = s;
                    return true;
                }
                return true; // found an instance but can't read scale -> treat as found
            }
            return false;
        }

        private static bool TryGetUniformScale(BlockReference br, out double scale)
        {
            scale = 1.0;
            if (br == null) return false;
            try
            {
                var s = br.ScaleFactors;
                double x = s.X, y = s.Y, z = s.Z;
                if (!(x > 0) || !(y > 0) || !(z > 0)) return false;
                if (Math.Abs(x - y) > 1e-6) return false;
                if (Math.Abs(x - z) > 1e-6) return false;
                scale = x;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void ScaleBlockDefinitionEntities(Transaction tr, ObjectId blockDefId, double factor)
        {
            if (!(factor > 0) || Math.Abs(factor - 1.0) < 1e-9) return;
            var btr = (BlockTableRecord)tr.GetObject(blockDefId, OpenMode.ForWrite);
            if (btr == null) return;

            var mat = Matrix3d.Scaling(factor, Point3d.Origin);
            foreach (ObjectId id in btr)
            {
                if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity ent)
                {
                    try { ent.TransformBy(mat); } catch { /* ignore */ }
                }
            }
        }

        private static void Export(Editor ed, Database db, ObjectId blockDefId, string folder)
        {
            try
            {
                string name = "block";
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var btr = (BlockTableRecord)tr.GetObject(blockDefId, OpenMode.ForRead);
                    name = btr?.Name ?? "block";
                    tr.Commit();
                }

                var fileName = WblockExportService.SafeBlockFileName(name) + ".dwg";
                var path = Path.Combine(folder, fileName);
                if (!WblockExportService.TryExportBlockDefinition(db, blockDefId, path, out string err))
                    ed.WriteMessage("\n[autocad-final] WBLOCK export failed (" + name + "): " + err + "\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[autocad-final] WBLOCK export failed: " + ex.Message + "\n");
            }
        }

        private static void InsertIfMissing(Document doc, ObjectId blockDefId, string layerName, Point3d insertPoint)
        {
            if (doc == null || blockDefId.IsNull) return;

            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                bool hasAny = false;
                foreach (ObjectId entId in ms)
                {
                    if (!(tr.GetObject(entId, OpenMode.ForRead, false) is BlockReference br))
                        continue;
                    if (br.BlockTableRecord == blockDefId)
                    {
                        hasAny = true;
                        break;
                    }
                }

                if (hasAny)
                {
                    tr.Commit();
                    return;
                }

                ms.UpgradeOpen();
                var brNew = new BlockReference(insertPoint, blockDefId);
                brNew.SetDatabaseDefaults(db);
                try { brNew.Layer = layerName; } catch { /* ignore */ }
                ms.AppendEntity(brNew);
                tr.AddNewlyCreatedDBObject(brNew, true);
                tr.Commit();
            }
        }

        private static StarterBlockChoice? PromptStarterBlockChoice(Editor ed)
        {
            if (ed == null) return null;

            var options = new PromptKeywordOptions(
                "\nWhich block to insert [S/R/SP] <SP>: ",
                "S R SP");
            options.AllowNone = true;
            options.Keywords.Default = "SP";

            var result = ed.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return null;

            var keyword = string.IsNullOrWhiteSpace(result.StringResult)
                ? options.Keywords.Default
                : result.StringResult;

            switch (keyword?.ToUpperInvariant())
            {
                case "S":
                    return StarterBlockChoice.Shaft;
                case "R":
                    return StarterBlockChoice.Reducer;
                case "SP":
                default:
                    return StarterBlockChoice.Sprinkler;
            }
        }

        private static void InsertSelectedStarterBlock(
            Document doc,
            StarterBlockChoice choice,
            ObjectId shaftDefId,
            ObjectId reducerDefId,
            ObjectId sprinklerDefId)
        {
            if (doc == null) return;

            var configuredDefId = TryLoadSelectedDefinitionFromConfiguredPath(doc.Database, doc.Editor, choice);
            if (!configuredDefId.IsNull)
            {
                switch (choice)
                {
                    case StarterBlockChoice.Shaft:
                        shaftDefId = configuredDefId;
                        break;
                    case StarterBlockChoice.Reducer:
                        reducerDefId = configuredDefId;
                        break;
                    case StarterBlockChoice.Sprinkler:
                    default:
                        sprinklerDefId = configuredDefId;
                        break;
                }
            }

            switch (choice)
            {
                case StarterBlockChoice.Shaft:
                    EnsureLayerThenInsertIfMissing(doc, shaftDefId, SprinklerLayers.McdShaftsLayer, SprinklerLayers.EnsureMcdShaftsLayer);
                    break;
                case StarterBlockChoice.Reducer:
                    EnsureLayerThenInsertIfMissing(doc, reducerDefId, SprinklerLayers.McdReducerLayer, SprinklerLayers.EnsureMcdReducerLayer);
                    break;
                case StarterBlockChoice.Sprinkler:
                default:
                    EnsureLayerThenInsertIfMissing(doc, sprinklerDefId, SprinklerLayers.McdSprinklersLayer, SprinklerLayers.EnsureMcdSprinklersLayer);
                    break;
            }
        }

        private static ObjectId TryLoadSelectedDefinitionFromConfiguredPath(Database db, Editor ed, StarterBlockChoice choice)
        {
            if (db == null) return ObjectId.Null;

            try
            {
                var cfg = RuntimeSettings.Load();
                var blockPath = cfg?.BlockPath;
                var blockFile = GetConfiguredBlockFileName(cfg, choice);
                var blockName = GetCanonicalBlockName(choice);

                if (string.IsNullOrWhiteSpace(blockPath) || string.IsNullOrWhiteSpace(blockFile))
                    return ObjectId.Null;

                var fullPath = Path.Combine(blockPath, blockFile);
                if (!File.Exists(fullPath))
                {
                    ed?.WriteMessage($"\n[autocad-final] Configured block file not found: {fullPath}\n");
                    return ObjectId.Null;
                }

                using (var srcDb = new Database(false, true))
                {
                    srcDb.ReadDwgFile(fullPath, FileShare.Read, true, null);
                    srcDb.CloseInput(true);
                    db.Insert(blockName, srcDb, true);
                }

                if (TryGetBlockDefinitionId(db, blockName, out var importedId))
                {
                    ed?.WriteMessage($"\n[autocad-final] Loaded {blockName} from: {fullPath}\n");
                    return importedId;
                }
            }
            catch (System.Exception ex)
            {
                ed?.WriteMessage("\n[autocad-final] Configured block load failed: " + ex.Message + "\n");
            }

            return ObjectId.Null;
        }

        private static string GetConfiguredBlockFileName(RuntimeSettings cfg, StarterBlockChoice choice)
        {
            if (cfg == null) return null;
            switch (choice)
            {
                case StarterBlockChoice.Shaft:
                    return cfg.ShaftBlockFile;
                case StarterBlockChoice.Reducer:
                    return cfg.ReducerBlockFile;
                case StarterBlockChoice.Sprinkler:
                default:
                    return cfg.SprinklerBlockFile;
            }
        }

        private static string GetCanonicalBlockName(StarterBlockChoice choice)
        {
            switch (choice)
            {
                case StarterBlockChoice.Shaft:
                    return SprinklerLayers.GetConfiguredShaftBlockName();
                case StarterBlockChoice.Reducer:
                    return SprinklerLayers.GetConfiguredReducerBlockName();
                case StarterBlockChoice.Sprinkler:
                default:
                    return SprinklerLayers.GetConfiguredSprinklerBlockName();
            }
        }

        private static bool TryGetBlockDefinitionId(Database db, string blockName, out ObjectId id)
        {
            id = ObjectId.Null;
            if (db == null || string.IsNullOrWhiteSpace(blockName)) return false;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (!bt.Has(blockName))
                    return false;
                id = bt[blockName];
                tr.Commit();
                return true;
            }
        }

        private static void EnsureLayerThenInsertIfMissing(
            Document doc,
            ObjectId blockDefId,
            string layerName,
            Func<Transaction, Database, ObjectId> ensureLayer)
        {
            if (doc == null || blockDefId.IsNull || string.IsNullOrWhiteSpace(layerName) || ensureLayer == null)
                return;

            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                ensureLayer(tr, db);
                tr.Commit();
            }

            InsertIfMissing(doc, blockDefId, layerName, new Point3d(0, 0, 0));
        }
    }
}

