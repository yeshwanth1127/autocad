using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using autocad_final.Licensing;

namespace autocad_final.Commands
{
    /// <summary>
    /// Pick one entity; every entity on that layer in model space is erased.
    /// </summary>
    public class DeleteAllOnLayerCommand
    {
        [CommandMethod("SPRINKLERDELETEALLONLAYER", CommandFlags.Modal)]
        public void DeleteAllOnLayer()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;
            var db = doc.Database;

            var peo = new PromptEntityOptions("\nSelect any object on the layer to clear: ");
            peo.AllowNone = false;
            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
                return;

            ObjectId pickLayerId = ObjectId.Null;
            string layerName = "(unknown)";
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ent = tr.GetObject(per.ObjectId, OpenMode.ForRead, false) as Entity;
                if (ent == null)
                {
                    ed.WriteMessage("\nCould not read selected entity.\n");
                    tr.Commit();
                    return;
                }
                pickLayerId = ent.LayerId;
                try
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (lt.Has(ent.Layer))
                        layerName = ent.Layer;
                }
                catch { /* ignore */ }
                tr.Commit();
            }

            if (pickLayerId.IsNull)
            {
                ed.WriteMessage("\nInvalid layer.\n");
                return;
            }

            int erased = 0;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var toErase = new List<ObjectId>();
                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (ent == null) continue;
                    if (ent.LayerId != pickLayerId) continue;
                    toErase.Add(id);
                }

                foreach (var id in toErase)
                {
                    var ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    ent?.Erase();
                    erased++;
                }

                tr.Commit();
            }

            ed.WriteMessage("\nErased " + erased + " entit" + (erased == 1 ? "y" : "ies") + " on layer \"" + layerName + "\".\n");
        }
    }
}
