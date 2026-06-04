using System;
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
    /// <summary>
    /// Imports one of the fixed named blocks from its WBLOCK (.dwg) file and lets the user place
    /// references by picking points. The definition is imported once, then reused for every click.
    /// </summary>
    public class InsertStandardBlockCommand
    {
        private enum BlockKind { Shaft, Sprinkler, Reducer }

        [CommandMethod("AF_INSERTBLOCK", CommandFlags.Modal)]
        public void Run()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            var kind = PromptBlockKind(ed);
            if (!kind.HasValue) return;

            string canonicalName = CanonicalName(kind.Value);
            string filePath = ResolveWblockPath(kind.Value);
            string layerName = LayerFor(kind.Value);

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                ed.WriteMessage($"\n[autocad-final] WBLOCK file for \"{canonicalName}\" not found. " +
                    "Set block_path/<block>_block in Properties.config, or export it first.\n");
                return;
            }

            try
            {
                using (doc.LockDocument())
                {
                    // Ensure the layer and import the definition once.
                    ObjectId defId;
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        EnsureLayer(tr, db, kind.Value);
                        defId = WblockImporter.EnsureDefinitionFromFile(db, filePath, canonicalName, out string err);
                        if (defId.IsNull)
                        {
                            ed.WriteMessage("\n[autocad-final] " + (err ?? "Import failed.") + "\n");
                            tr.Commit();
                            return;
                        }
                        tr.Commit();
                    }

                    // Place references until the user presses Enter/Escape.
                    int placed = 0;
                    while (true)
                    {
                        var ppo = new PromptPointOptions(
                            placed == 0
                                ? $"\nPick insertion point for {canonicalName} (Enter to finish): "
                                : "\nPick next insertion point (Enter to finish): ");
                        ppo.AllowNone = true;
                        var pr = ed.GetPoint(ppo);
                        if (pr.Status != PromptStatus.OK)
                            break;

                        using (doc.LockDocument())
                        using (var tr = db.TransactionManager.StartTransaction())
                        {
                            WblockImporter.InsertFromFile(tr, db, filePath, pr.Value, out string err,
                                blockName: canonicalName, layerName: layerName);
                            if (err != null)
                                ed.WriteMessage("\n[autocad-final] " + err + "\n");
                            else
                                placed++;
                            tr.Commit();
                        }
                    }

                    ed.WriteMessage($"\n[autocad-final] Inserted {placed} {canonicalName} block(s).\n");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[autocad-final] Insert block failed: " + ex.Message + "\n");
            }
        }

        private static BlockKind? PromptBlockKind(Editor ed)
        {
            var pko = new PromptKeywordOptions("\nWhich block to insert [Shaft/Sprinkler/Reducer] <Sprinkler>: ");
            pko.Keywords.Add("Shaft");
            pko.Keywords.Add("Sprinkler");
            pko.Keywords.Add("Reducer");
            pko.Keywords.Default = "Sprinkler";
            pko.AllowNone = true;

            var res = ed.GetKeywords(pko);
            if (res.Status != PromptStatus.OK && res.Status != PromptStatus.None)
                return null;

            switch (res.StringResult)
            {
                case "Shaft":    return BlockKind.Shaft;
                case "Reducer":  return BlockKind.Reducer;
                case "Sprinkler":
                default:         return BlockKind.Sprinkler;
            }
        }

        private static string CanonicalName(BlockKind kind)
        {
            switch (kind)
            {
                case BlockKind.Shaft:    return SprinklerLayers.GetConfiguredShaftBlockName();
                case BlockKind.Reducer:  return SprinklerLayers.GetConfiguredReducerBlockName();
                case BlockKind.Sprinkler:
                default:                 return SprinklerLayers.GetConfiguredSprinklerBlockName();
            }
        }

        private static string LayerFor(BlockKind kind)
        {
            switch (kind)
            {
                case BlockKind.Shaft:    return SprinklerLayers.McdShaftsLayer;
                case BlockKind.Reducer:  return SprinklerLayers.McdReducerLayer;
                case BlockKind.Sprinkler:
                default:                 return SprinklerLayers.McdSprinklersLayer;
            }
        }

        private static void EnsureLayer(Transaction tr, Database db, BlockKind kind)
        {
            switch (kind)
            {
                case BlockKind.Shaft:    SprinklerLayers.EnsureMcdShaftsLayer(tr, db); break;
                case BlockKind.Reducer:  SprinklerLayers.EnsureMcdReducerLayer(tr, db); break;
                case BlockKind.Sprinkler:
                default:                 SprinklerLayers.EnsureMcdSprinklersLayer(tr, db); break;
            }
        }

        /// <summary>
        /// Resolves the WBLOCK file path: configured block_path + &lt;block&gt;_block from Properties.config first,
        /// then the default export folder (Documents\autocad-final\wblocks\&lt;name&gt;.dwg).
        /// </summary>
        private static string ResolveWblockPath(BlockKind kind)
        {
            var cfg = RuntimeSettings.Load();
            string configuredFile = null;
            switch (kind)
            {
                case BlockKind.Shaft:     configuredFile = cfg?.ShaftBlockFile; break;
                case BlockKind.Reducer:   configuredFile = cfg?.ReducerBlockFile; break;
                case BlockKind.Sprinkler: configuredFile = cfg?.SprinklerBlockFile; break;
            }

            if (!string.IsNullOrWhiteSpace(cfg?.BlockPath) && !string.IsNullOrWhiteSpace(configuredFile))
            {
                var p = Path.Combine(cfg.BlockPath, configuredFile);
                if (File.Exists(p)) return p;
            }

            // Fallback: the folder InitializeStandards exports WBLOCKs to.
            var fallback = Path.Combine(
                WblockExportService.DefaultWblockFolder(),
                WblockExportService.SafeBlockFileName(CanonicalName(kind)) + ".dwg");
            return fallback;
        }
    }
}
