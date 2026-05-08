using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using autocad_final.AreaWorkflow;
using autocad_final.Licensing;
using autocad_final.ShaftWorkflow;

namespace autocad_final.Commands
{
    public class SelectShaftPointCommand
    {
        [CommandMethod("SELECTSHAFTPOINT", CommandFlags.Modal)]
        public void SelectShaftPoint()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            if (!TrialGuard.EnsureActive(doc.Editor)) return;
            Run(doc);
        }

        public static bool Run(Document doc)
        {
            if (doc == null) return false;
            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return false;
            var db = doc.Database;

            if (!ShaftWorkflow.SelectShaftPoint.Run(ed, out var shaftPoint))
                return false;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                ObjectId shaftLayerId = SprinklerLayers.EnsureMcdShaftsLayer(tr, db);
                var blockDefId = EnsureShaftBlockDefinition.Run(db, tr);
                InsertShaftBlockReference.Run(db, tr, shaftPoint, blockDefId, shaftLayerId);
                tr.Commit();
            }

            ed.WriteMessage("\nShaft block placed.\n");
            return true;
        }
    }
}

