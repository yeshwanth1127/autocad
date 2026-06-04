using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using autocad_final.AreaWorkflow;

namespace autocad_final.Commands
{
    /// <summary>
    /// Creates every standard plugin layer that does not already exist. Existing layers and their
    /// contents are left untouched.
    /// </summary>
    public class CreateStandardLayersCommand
    {
        [CommandMethod("AF_CREATELAYERS", CommandFlags.Modal)]
        public void Run()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            try
            {
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    int created = SprinklerLayers.EnsureAllStandardLayers(tr, db);
                    tr.Commit();
                    ed.WriteMessage(created > 0
                        ? $"\n[autocad-final] Standard layers ready ({created} created).\n"
                        : "\n[autocad-final] All standard layers already present.\n");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[autocad-final] Create layers failed: " + ex.Message + "\n");
            }
        }
    }
}
